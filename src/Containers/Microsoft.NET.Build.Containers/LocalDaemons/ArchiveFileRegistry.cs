// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.Build.Containers.Resources;
using System.Security.Cryptography;

namespace Microsoft.NET.Build.Containers.LocalDaemons;

/// <summary>
/// Writes built images to a local archive instead of loading them into a container runtime.
/// </summary>
internal class ArchiveFileRegistry : ILocalRegistry
{
    public string ArchiveOutputPath { get; private set; }

    internal string? ArchiveDigest { get; private set; }

    internal bool ComputeArchiveDigest { get; set; }

    public ArchiveFileRegistry(string archiveOutputPath)
    {
        ArchiveOutputPath = archiveOutputPath;
    }

    internal async Task LoadAsync<T>(T image, SourceImageReference sourceReference, 
        DestinationImageReference destinationReference, CancellationToken cancellationToken,
        Func<T, SourceImageReference, DestinationImageReference, Stream, CancellationToken, Task> writeStreamFunc)
    {
        ArchiveDigest = null;
        var fullPath = GetArchiveOutputPath(ArchiveOutputPath, destinationReference.Repository);

        // create parent directory if required.
        var parentDirectory = Path.GetDirectoryName(fullPath);
        if (parentDirectory != null && !Directory.Exists(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        ArchiveOutputPath = fullPath;
        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using SHA256? hash = ComputeArchiveDigest ? SHA256.Create() : null;
            await using (var fileStream = File.Create(temporaryPath))
            {
                using CryptoStream? hashingStream = hash is null ? null : new(fileStream, hash, CryptoStreamMode.Write, leaveOpen: true);
                await writeStreamFunc(image, sourceReference, destinationReference, hashingStream ?? (Stream)fileStream, cancellationToken).ConfigureAwait(false);
                if (hashingStream is not null)
                {
                    await hashingStream.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            ArchiveDigest = hash is null ? null : Convert.ToHexStringLower(hash.Hash!);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static string GetArchiveOutputPath(string archiveOutputPath, string repository)
    {
        var fullPath = Path.GetFullPath(archiveOutputPath);

        var directorySeparatorChar = Path.DirectorySeparatorChar;

        // if doesn't end with a file extension, assume it's a directory
        if (!Path.HasExtension(fullPath))
        {
           fullPath += Path.DirectorySeparatorChar;
        }

        // pointing to a directory? -> append default name
        if (fullPath.EndsWith(directorySeparatorChar))
        {
            fullPath = Path.Combine(fullPath, repository + ".tar.gz");
        }

        return fullPath;
    }

    /// <inheritdoc />
    public async Task LoadAsync(BuiltImage image, SourceImageReference sourceReference,
        DestinationImageReference destinationReference,
        CancellationToken cancellationToken) 
        => await LoadAsync(image, sourceReference, destinationReference, cancellationToken,
            ContainerArchive.WriteImageToStreamAsync);

    /// <inheritdoc />
    public async Task LoadAsync(MultiArchImage multiArchImage, SourceImageReference sourceReference,
        DestinationImageReference destinationReference,
        CancellationToken cancellationToken) 
        => await LoadAsync(multiArchImage, sourceReference, destinationReference, cancellationToken,
            ContainerArchive.WriteMultiArchOciImageToStreamAsync);

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc />
    public bool IsAvailable() => true;

    public override string ToString()
    {
        return string.Format(Strings.ArchiveRegistry_PushInfo, ArchiveOutputPath);
    }
}
