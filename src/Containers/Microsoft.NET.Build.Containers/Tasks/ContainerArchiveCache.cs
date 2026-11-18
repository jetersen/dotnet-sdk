// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.Build.Containers.LocalDaemons;

namespace Microsoft.NET.Build.Containers.Tasks;

internal static class ContainerArchiveCache
{
    private const int FormatVersion = 2;

    public static string ComputeFingerprint(
        CreateNewImage task,
        string baseImageManifestDigest,
        bool baseImageIsResolved,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(hash, FormatVersion.ToString());
        Append(hash, Constants.Version);
        Append(hash, baseImageManifestDigest);
        Append(hash, task.ContainerRuntimeIdentifier);
        Append(hash, task.Repository);
        Append(hash, task.WorkingDirectory);
        Append(hash, task.ContainerUser);
        Append(hash, task.ImageFormat);
        Append(hash, task.AppCommandInstruction);
        // A generated UtcNow value is output metadata. Hashing it would make every invocation miss.
        DateTime? sourceDateEpoch = CreateNewImage.ParseSourceDateEpoch(task.SourceDateEpoch);
        Append(hash, sourceDateEpoch is null
            ? null
            : new DateTimeOffset(sourceDateEpoch.Value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Append(hash, task.GenerateLabels.ToString());
        if (task.GenerateLabels)
        {
            Append(hash, task.GenerateCreatedLabels.ToString());
            Append(hash, task.GenerateDigestLabel.ToString());
            AppendLabels(hash, task.Labels);
        }
        Append(hash, task.SkipPublishing.ToString());
        Append(hash, "ImageTags", task.ImageTags);
        Append(hash, "Entrypoint", task.Entrypoint);
        Append(hash, "EntrypointArgs", task.EntrypointArgs);
        Append(hash, "DefaultArgs", task.DefaultArgs);
        Append(hash, "AppCommand", task.AppCommand);
        Append(hash, "AppCommandArgs", task.AppCommandArgs);
        Append(hash, "ExposedPorts", task.ExposedPorts, "Type");
        Append(hash, "EnvironmentVariables", task.ContainerEnvironmentVariables, "Value");
        if (!baseImageIsResolved)
        {
            AppendFile(hash, task.RuntimeIdentifierGraphPath, "RuntimeIdentifierGraph", includeMode: false, cancellationToken);
        }

        string publishDirectory = Path.GetFullPath(task.PublishDirectory);
        AppendFile(hash, publishDirectory, ".", includeMode: true, cancellationToken);
        foreach (string path in Directory.EnumerateFileSystemEntries(publishDirectory, "*",
            new EnumerationOptions { AttributesToSkip = FileAttributes.System, RecurseSubdirectories = true })
            .OrderBy(path => Path.GetRelativePath(publishDirectory, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(publishDirectory, path);
            if (OperatingSystem.IsWindows()) relativePath = relativePath.Replace('\\', '/');
            AppendFile(hash, path, relativePath, includeMode: true, cancellationToken);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool TryRestore(CreateNewImage task, string fingerprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(task.ArchiveOutputPath)
            || string.IsNullOrEmpty(task.ArchiveIncrementalCachePath)
            || (!task.SkipPublishing && !File.Exists(GetArchiveOutputPath(task)))
            || !File.Exists(task.ArchiveIncrementalCachePath))
        {
            return false;
        }

        ArchiveCacheEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<ArchiveCacheEntry>(File.ReadAllText(task.ArchiveIncrementalCachePath));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (entry is null || entry.FormatVersion != FormatVersion || entry.Fingerprint != fingerprint
            || entry.ContainerNames is null || entry.ContainerNames.Any(name => name is null)
            || entry.DigestLabel is { Name: null } or { Value: null })
        {
            return false;
        }

        try
        {
            if (!TryGetContentPath(task, entry, out string contentPath)
                || entry.ArchiveDigest != ComputeFileDigest(contentPath, cancellationToken))
            {
                return false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDigestException or ArgumentException)
        {
            return false;
        }

        task.GeneratedContainerManifest = entry.Manifest;
        task.GeneratedContainerConfiguration = entry.Configuration;
        task.GeneratedContainerDigest = entry.ManifestDigest;
        task.GeneratedArchiveOutputPath = task.ArchiveOutputPath;
        task.GeneratedContainerMediaType = entry.ManifestMediaType;
        task.GeneratedContainerNames = entry.ContainerNames.Select(name => new TaskItem(name)).ToArray();
        if (entry.DigestLabel is not null)
        {
            TaskItem label = new(entry.DigestLabel.Name);
            label.SetMetadata("Value", entry.DigestLabel.Value);
            task.GeneratedDigestLabel = label;
        }

        return true;
    }

    public static void Save(CreateNewImage task, string fingerprint, CancellationToken cancellationToken = default, string? archiveDigest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(task.ArchiveOutputPath)
            || string.IsNullOrEmpty(task.ArchiveIncrementalCachePath)
            || (!task.SkipPublishing && !File.Exists(GetArchiveOutputPath(task))))
        {
            return;
        }

        DigestLabel? digestLabel = task.GeneratedDigestLabel is null
            ? null
            : new(task.GeneratedDigestLabel.ItemSpec, task.GeneratedDigestLabel.GetMetadata("Value"));
        ArchiveCacheEntry entry = new(
            FormatVersion,
            fingerprint,
            task.SkipPublishing
                ? DigestUtils.GetEncoded(JsonSerializer.Deserialize<ManifestV2>(task.GeneratedContainerManifest)!.Layers.Last().digest)
                : archiveDigest ?? ComputeFileDigest(GetArchiveOutputPath(task), cancellationToken),
            task.GeneratedContainerManifest,
            task.GeneratedContainerConfiguration,
            task.GeneratedContainerDigest,
            task.GeneratedContainerMediaType,
            task.GeneratedContainerNames.Select(item => item.ItemSpec).ToArray(),
            digestLabel);
        WriteCacheEntry(task.ArchiveIncrementalCachePath, entry, cancellationToken);
    }


    private static bool TryGetContentPath(CreateNewImage task, ArchiveCacheEntry entry, out string path)
    {
        path = string.Empty;
        if (!task.SkipPublishing)
        {
            path = GetArchiveOutputPath(task);
            return true;
        }
        if (string.IsNullOrEmpty(entry.Manifest) || string.IsNullOrEmpty(entry.Configuration)) return false;
        ManifestV2? image = JsonSerializer.Deserialize<ManifestV2>(entry.Manifest);
        if (image is null || image.Layers is not { Count: > 0 }
            || string.IsNullOrEmpty(entry.ManifestMediaType) || image.MediaType != entry.ManifestMediaType
            || entry.ManifestDigest != DigestUtils.ComputeSha256Digest(entry.Manifest)
            || image.Config.digest != DigestUtils.ComputeSha256Digest(entry.Configuration)
            || image.Config.size != Encoding.UTF8.GetByteCount(entry.Configuration)) return false;
        ManifestLayer layer = image.Layers[^1];
        if (entry.ArchiveDigest != DigestUtils.GetEncoded(layer.digest)) return false;
        path = ContentStore.PathForDescriptor(new Descriptor(layer.mediaType, layer.digest, layer.size));
        return new FileInfo(path).Length == layer.size;
    }

    public static string ComputeIndexFingerprint(MultiArchImage image, string repository, string[] tags, IEnumerable<string>? runtimeIdentifiers = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, FormatVersion.ToString());
        Append(hash, Constants.Version);
        Append(hash, image.ImageIndex);
        Append(hash, repository);
        Append(hash, "Tags", tags);
        Append(hash, "RuntimeIdentifiers", runtimeIdentifiers ?? []);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool TryRestoreIndex(string archivePath, string cachePath, string fingerprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IndexCacheEntry? entry = JsonSerializer.Deserialize<IndexCacheEntry>(File.ReadAllText(cachePath));
            return entry is not null && entry.FormatVersion == FormatVersion
                && entry.Fingerprint == fingerprint && entry.ArchiveDigest == ComputeFileDigest(archivePath, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void SaveIndex(string archivePath, string cachePath, string fingerprint, CancellationToken cancellationToken, string? archiveDigest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IndexCacheEntry entry = new(FormatVersion, fingerprint, archiveDigest ?? ComputeFileDigest(archivePath, cancellationToken));
        WriteCacheEntry(cachePath, entry, cancellationToken);
    }

    private static void WriteCacheEntry<T>(string cachePath, T entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An optional cache write must not invalidate an image already produced.
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private sealed record IndexCacheEntry(int FormatVersion, string Fingerprint, string ArchiveDigest);

    private static string GetArchiveOutputPath(CreateNewImage task)
        => ArchiveFileRegistry.GetArchiveOutputPath(task.ArchiveOutputPath, task.Repository);

    private static void AppendLabels(IncrementalHash hash, IEnumerable<ITaskItem> labels)
    {
        ITaskItem[] labelArray = labels.ToArray();
        Append(hash, "Labels");
        Append(hash, labelArray.Length.ToString());
        foreach (ITaskItem label in labelArray)
        {
            Append(hash, label.ItemSpec);
            Append(hash, label.GetMetadata("Value"));
        }
    }

    private static void Append(IncrementalHash hash, string name, IEnumerable<ITaskItem> items, string? metadataName = null)
    {
        ITaskItem[] itemArray = items.ToArray();
        Append(hash, name);
        Append(hash, itemArray.Length.ToString());
        foreach (ITaskItem item in itemArray)
        {
            Append(hash, item.ItemSpec);
            if (metadataName is not null)
            {
                Append(hash, item.GetMetadata(metadataName));
            }
        }
    }

    private static void Append(IncrementalHash hash, string name, IEnumerable<string> values)
    {
        string[] valueArray = values.ToArray();
        Append(hash, name);
        Append(hash, valueArray.Length.ToString());
        foreach (string value in valueArray)
        {
            Append(hash, value);
        }
    }

    private static void AppendFile(
        IncrementalHash hash,
        string path,
        string identity,
        bool includeMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append(hash, identity);
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        Append(hash, info is DirectoryInfo ? "directory" : "file");
        if (includeMode)
        {
            Append(hash, ((int)Layer.DetermineFileMode(info)).ToString());
        }

        if (info is FileInfo)
        {
            using FileStream stream = File.OpenRead(path);
            Append(hash, stream.Length.ToString());
            byte[] buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
        }
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, -1);
            hash.AppendData(length);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string ComputeFileDigest(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = File.OpenRead(path);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record ArchiveCacheEntry(
        int FormatVersion,
        string Fingerprint,
        string ArchiveDigest,
        string Manifest,
        string Configuration,
        string ManifestDigest,
        string ManifestMediaType,
        string[] ContainerNames,
        DigestLabel? DigestLabel);

    private sealed record DigestLabel(string Name, string Value);
}
