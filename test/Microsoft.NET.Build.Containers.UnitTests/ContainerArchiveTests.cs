// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.NET.Build.Containers.Tasks;
using Moq;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using System.Text.Json;
using Microsoft.NET.Build.Containers.LocalDaemons;

namespace Microsoft.NET.Build.Containers.UnitTests;

[TestClass]
public class ContainerArchiveTests
{
    [TestMethod]
    public async Task Failed_archive_write_preserves_existing_archive()
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(directory, "image.tar.gz");
        await File.WriteAllTextAsync(archivePath, "existing", TestContext.CancellationToken);
        ArchiveFileRegistry registry = new(archivePath);
        DestinationImageReference destination = new(registry, "repository", ["tag"]);

        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(
                () => registry.LoadAsync(
                    new object(),
                    default,
                    destination,
                    TestContext.CancellationToken,
                    async (_, _, _, stream, cancellationToken) =>
                    {
                        await stream.WriteAsync("partial"u8.ToArray(), cancellationToken);
                        throw new IOException("write interrupted");
                    }));

            Assert.AreEqual("existing", await File.ReadAllTextAsync(archivePath, TestContext.CancellationToken));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Rejects_unsupported_manifest_media_type()
    {
        BuiltImage image = new()
        {
            Config = string.Empty,
            Manifest = string.Empty,
            ManifestDigest = string.Empty,
            ManifestMediaType = "unsupported",
            Architecture = "unknown",
            OS = "unknown"
        };

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => ContainerArchive.WriteImageToStreamAsync(image, default, default, Stream.Null, default));
    }

    [TestMethod]
    [DataRow(SchemaTypes.DockerManifestV2)]
    [DataRow(SchemaTypes.OciManifestV1)]
    public async Task Requires_image_sha(string manifestMediaType)
    {
        BuiltImage image = new()
        {
            Config = string.Empty,
            Manifest = string.Empty,
            ManifestDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            ManifestMediaType = manifestMediaType,
            Layers = [],
            Architecture = "unknown",
            OS = "unknown"
        };

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ContainerArchive.WriteImageToStreamAsync(image, default, default, Stream.Null, default));
        Assert.Contains("image SHA", exception.Message);
    }

    [TestMethod]
    public async Task Oci_layout_supports_docker_manifest()
    {
        const string config = "{}";
        string imageSha = DigestUtils.ComputeSha256(config);
        string imageDigest = DigestUtils.FormatSha256Digest(imageSha);
        string manifest =
            $$"""
            {
              "schemaVersion": 2,
              "mediaType": "{{SchemaTypes.DockerManifestV2}}",
              "config": {
                "mediaType": "{{SchemaTypes.DockerContainerV1}}",
                "size": 2,
                "digest": "{{imageDigest}}"
              },
              "layers": []
            }
            """;
        string manifestDigest = DigestUtils.ComputeSha256Digest(manifest);
        BuiltImage image = new()
        {
            Config = config,
            ImageDigest = imageDigest,
            ImageSha = imageSha,
            Manifest = manifest,
            ManifestDigest = manifestDigest,
            ManifestMediaType = SchemaTypes.DockerManifestV2,
            Layers = [],
            Architecture = "arm64",
            OS = "linux"
        };
        DestinationImageReference destination = new(
            new ArchiveFileRegistry("unused"),
            "repository",
            ["tag"]);
        using MemoryStream archive = new();

        await ContainerArchive.WriteOciImageToStreamAsync(
            image,
            default,
            destination,
            archive,
            TestContext.CancellationToken);

        archive.Position = 0;
        using TarReader reader = new(archive);
        var entries = new Dictionary<string, string>();
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: TestContext.CancellationToken) is { } entry)
        {
            if (entry.DataStream is not null)
            {
                using StreamReader streamReader = new(entry.DataStream);
                entries.Add(entry.Name, await streamReader.ReadToEndAsync(TestContext.CancellationToken));
            }
        }

        Assert.Contains("oci-layout", entries.Keys);
        string manifestPath = $"blobs/sha256/{DigestUtils.GetEncoded(manifestDigest)}";
        Assert.AreEqual(manifest, entries[manifestPath]);
        using JsonDocument index = JsonDocument.Parse(entries["index.json"]);
        Assert.AreEqual(SchemaTypes.OciImageIndexV1, index.RootElement.GetProperty("mediaType").GetString());
        Assert.AreEqual(
            SchemaTypes.DockerManifestV2,
            index.RootElement.GetProperty("manifests")[0].GetProperty("mediaType").GetString());
        Assert.AreEqual(
            "arm64",
            index.RootElement.GetProperty("manifests")[0].GetProperty("platform").GetProperty("architecture").GetString());
    }

    [TestMethod]
    public async Task Multi_arch_oci_layout_has_unknown_platform_for_the_image_index_descriptor()
    {
        BuiltImage[] images =
        [
            CreateImage("amd64"),
            CreateImage("arm64")
        ];
        (string imageIndex, string imageIndexMediaType) = ImageIndexGenerator.GenerateImageIndex(images);
        MultiArchImage multiArchImage = new()
        {
            ImageIndex = imageIndex,
            ImageIndexMediaType = imageIndexMediaType,
            Images = images
        };
        DestinationImageReference destination = new(
            new ArchiveFileRegistry("unused"),
            "repository",
            ["tag"]);
        using MemoryStream archive = new();

        await ContainerArchive.WriteMultiArchOciImageToStreamAsync(
            multiArchImage,
            default,
            destination,
            archive,
            TestContext.CancellationToken);

        archive.Position = 0;
        using TarReader reader = new(archive);
        var entries = new Dictionary<string, string>();
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: TestContext.CancellationToken) is { } entry)
        {
            if (entry.DataStream is not null)
            {
                using StreamReader streamReader = new(entry.DataStream);
                entries.Add(entry.Name, await streamReader.ReadToEndAsync(TestContext.CancellationToken));
            }
        }

        using JsonDocument index = JsonDocument.Parse(entries["index.json"]);
        JsonElement manifests = index.RootElement.GetProperty("manifests");
        Assert.AreEqual(1, manifests.GetArrayLength());
        Assert.AreEqual(SchemaTypes.DockerManifestListV2, manifests[0].GetProperty("mediaType").GetString());
        Assert.AreEqual("unknown", manifests[0].GetProperty("platform").GetProperty("architecture").GetString());
        Assert.AreEqual("unknown", manifests[0].GetProperty("platform").GetProperty("os").GetString());
        string imageIndexPath = $"blobs/sha256/{DigestUtils.GetEncoded(DigestUtils.ComputeSha256Digest(imageIndex))}";
        Assert.AreEqual(imageIndex, entries[imageIndexPath]);
        Assert.IsTrue(entries.ContainsKey($"blobs/sha256/{DigestUtils.GetEncoded(images[0].ManifestDigest)}"));
        Assert.IsTrue(entries.ContainsKey($"blobs/sha256/{DigestUtils.GetEncoded(images[1].ManifestDigest)}"));
    }

    [TestMethod]
    public void Multi_arch_cache_tracks_order_tags_and_archive_integrity()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string archive = Path.Combine(directory, "image.tar");
        string cache = Path.Combine(directory, "index.json");
        BuiltImage[] images = [CreateImage("amd64"), CreateImage("arm64")];
        try
        {
            string fingerprint = Fingerprint(images, ["latest"]);
            File.WriteAllText(archive, "archive");
            ContainerArchiveCache.SaveIndex(archive, cache, fingerprint, TestContext.CancellationToken);
            Assert.IsTrue(ContainerArchiveCache.TryRestoreIndex(archive, cache, fingerprint, TestContext.CancellationToken));
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            cancellation.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() =>
                ContainerArchiveCache.TryRestoreIndex(archive, cache, fingerprint, cancellation.Token));
            Assert.IsFalse(ContainerArchiveCache.TryRestoreIndex(archive, cache, Fingerprint(images.Reverse().ToArray(), ["latest"]), TestContext.CancellationToken));
            Assert.IsFalse(ContainerArchiveCache.TryRestoreIndex(archive, cache, Fingerprint(images, ["changed"]), TestContext.CancellationToken));
            var (index, type) = ImageIndexGenerator.GenerateImageIndex(images);
            MultiArchImage multi = new() { Images = images, ImageIndex = index, ImageIndexMediaType = type };
            Assert.AreNotEqual(
                ContainerArchiveCache.ComputeIndexFingerprint(multi, "repo", ["latest"], ["linux-x64", "linux-arm64"]),
                ContainerArchiveCache.ComputeIndexFingerprint(multi, "repo", ["latest"], ["linux-arm64", "linux-x64"]));
            File.WriteAllText(archive, "corrupt");
            Assert.IsFalse(ContainerArchiveCache.TryRestoreIndex(archive, cache, fingerprint, TestContext.CancellationToken));
            File.WriteAllText(cache, "{");
            Assert.IsFalse(ContainerArchiveCache.TryRestoreIndex(archive, cache, fingerprint, TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static string Fingerprint(BuiltImage[] images, string[] tags)
        {
            var (index, type) = ImageIndexGenerator.GenerateImageIndex(images);
            return ContainerArchiveCache.ComputeIndexFingerprint(
                new MultiArchImage { Images = images, ImageIndex = index, ImageIndexMediaType = type }, "repo", tags);
        }
    }

    [TestMethod]
    [ResourceLock(WellKnownResources.CurrentDirectory)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Multi_arch_task_reuses_archive_and_rebuilds_for_changed_tags_and_order(bool relativeOutput)
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string archive = Path.Combine(directory, "image.tar");
        string cache = Path.Combine(directory, "index.json");
        string archiveArgument = relativeOutput ? Path.GetRelativePath(Environment.CurrentDirectory, archive) : archive;
        BuiltImage[] images = [CreateImage("amd64"), CreateImage("arm64")];
        try
        {
            using CreateImageIndex first = CreateTask(images, ["latest"]);
            Assert.IsTrue(await first.ExecuteAsync(TestContext.CancellationToken));
            byte[] original = await File.ReadAllBytesAsync(archive, TestContext.CancellationToken);
            DateTime sentinel = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(archive, sentinel);
            using CreateImageIndex cached = CreateTask(images, ["latest"]);
            Assert.IsTrue(await cached.ExecuteAsync(TestContext.CancellationToken));
            Assert.AreEqual(sentinel, File.GetLastWriteTimeUtc(archive));
            Assert.AreEqual(first.GeneratedImageIndex, cached.GeneratedImageIndex);
            Assert.AreEqual(archiveArgument, cached.GeneratedArchiveOutputPath);
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(archive, TestContext.CancellationToken));

            using CreateImageIndex changed = CreateTask(images.Reverse().ToArray(), ["changed"]);
            Assert.IsTrue(await changed.ExecuteAsync(TestContext.CancellationToken));
            Assert.AreNotEqual(sentinel, File.GetLastWriteTimeUtc(archive));
            Assert.AreNotEqual(first.GeneratedImageIndex, changed.GeneratedImageIndex);
            await File.WriteAllTextAsync(archive, "damaged", TestContext.CancellationToken);
            using CreateImageIndex repaired = CreateTask(images.Reverse().ToArray(), ["changed"]);
            Assert.IsTrue(await repaired.ExecuteAsync(TestContext.CancellationToken));
            Assert.IsGreaterThan((long)"damaged".Length, new FileInfo(archive).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        CreateImageIndex CreateTask(BuiltImage[] orderedImages, string[] tags) => new()
        {
            BuildEngine = new Mock<IBuildEngine>().Object,
            Repository = "repo",
            ImageTags = tags,
            ArchiveOutputPath = archiveArgument,
            EnableArchiveIncrementalCache = true,
            ArchiveIncrementalCachePath = cache,
            GeneratedContainers = orderedImages.Select(image =>
            {
                TaskItem item = new(image.Architecture);
                item.SetMetadata("Configuration", image.Config);
                item.SetMetadata("Manifest", image.Manifest);
                item.SetMetadata("ManifestDigest", image.ManifestDigest);
                item.SetMetadata("ManifestMediaType", image.ManifestMediaType);
                return (ITaskItem)item;
            }).ToArray()
        };
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Archive_digest_tracks_exact_committed_bytes_only_when_requested(bool computeDigest)
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(directory, "image.tar");
        ArchiveFileRegistry registry = new(path) { ComputeArchiveDigest = computeDigest };
        byte[] payload = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
        try
        {
            await registry.LoadAsync(new object(), default, new(registry, "repo", ["tag"]), TestContext.CancellationToken,
                async (_, _, _, stream, token) =>
                {
                    stream.Write(payload.AsSpan(0, 5));
                    await stream.WriteAsync(payload.AsMemory(5), token);
                });
            Assert.AreSequenceEqual(payload, await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
            Assert.AreEqual(computeDigest ? expected : null, registry.ArchiveDigest);
            await Assert.ThrowsExactlyAsync<IOException>(() => registry.LoadAsync(new object(), default,
                new(registry, "repo", ["tag"]), TestContext.CancellationToken,
                (_, _, _, _, _) => throw new IOException("aborted replacement")));
            Assert.IsNull(registry.ArchiveDigest);
            Assert.AreSequenceEqual(payload, await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task Cancellation_before_archive_replacement_keeps_the_previous_archive()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(directory, "image.tar");
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        ArchiveFileRegistry registry = new(path) { ComputeArchiveDigest = true };
        try
        {
            File.WriteAllText(path, "existing");
            await Assert.ThrowsAsync<OperationCanceledException>(() => registry.LoadAsync(new object(), default,
                new(registry, "repo", ["tag"]), cancellation.Token, async (_, _, _, stream, token) =>
                {
                    await stream.WriteAsync("new"u8.ToArray(), token);
                    cancellation.Cancel();
                }));
            Assert.AreEqual("existing", File.ReadAllText(path));
            Assert.IsNull(registry.ArchiveDigest);
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void Optional_index_metadata_failures_and_cancelled_updates_preserve_output()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string archive = Path.Combine(directory, "image.tar");
        string cache = Path.Combine(directory, "index.json");
        try
        {
            File.WriteAllText(archive, "archive");
            ContainerArchiveCache.SaveIndex(archive, cache, "first", TestContext.CancellationToken);
            string original = File.ReadAllText(cache);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => ContainerArchiveCache.SaveIndex(archive, cache, "changed", cancellation.Token));
            Assert.AreEqual(original, File.ReadAllText(cache));
            File.Delete(cache);
            Directory.CreateDirectory(cache);
            ContainerArchiveCache.SaveIndex(archive, cache, "changed", TestContext.CancellationToken);
            Assert.IsFalse(ContainerArchiveCache.TryRestoreIndex(archive, cache, "changed", TestContext.CancellationToken));
            Assert.AreEqual("archive", File.ReadAllText(archive));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static BuiltImage CreateImage(string architecture)
    {
        string config = $$"""{"architecture":"{{architecture}}","os":"linux"}""";
        string imageSha = DigestUtils.ComputeSha256(config);
        string imageDigest = DigestUtils.FormatSha256Digest(imageSha);
        string manifest =
            $$"""
            {
              "schemaVersion": 2,
              "mediaType": "{{SchemaTypes.DockerManifestV2}}",
              "config": {
                "mediaType": "{{SchemaTypes.DockerContainerV1}}",
                "size": {{config.Length}},
                "digest": "{{imageDigest}}"
              },
              "layers": []
            }
            """;

        return new BuiltImage
        {
            Config = config,
            ImageDigest = imageDigest,
            ImageSha = imageSha,
            Manifest = manifest,
            ManifestDigest = DigestUtils.ComputeSha256Digest(manifest),
            ManifestMediaType = SchemaTypes.DockerManifestV2,
            Layers = [],
            Architecture = architecture,
            OS = "linux"
        };
    }

    public TestContext TestContext { get; set; } = default!;
}
