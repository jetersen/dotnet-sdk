// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
    public async Task Prefetch_is_bounded_and_downloads_shared_blobs_once()
    {
        string salt = Guid.NewGuid().ToString();
        Dictionary<string, byte[]> blobs = Enumerable.Range(0, 12).Select(i => Encoding.UTF8.GetBytes($"{salt}-{i}"))
            .ToDictionary(bytes => DigestUtils.ComputeSha256Digest(Encoding.UTF8.GetString(bytes)));
        Descriptor[] descriptors = blobs.Select(blob => new Descriptor(SchemaTypes.OciLayerGzipV1, blob.Key, blob.Value.Length)).ToArray();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;
        int calls = 0;
        Mock<IRegistryAPI> api = new(MockBehavior.Strict);
        api.Setup(api => api.Blob.GetUnvalidatedStreamAsync("repo", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string digest, CancellationToken token) =>
            {
                Interlocked.Increment(ref calls);
                int count = Interlocked.Increment(ref active);
                InterlockedExtensionsMax(ref maximum, count);
                if (count == 4)
                {
                    started.TrySetResult();
                }

                await gate.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return (Stream)new MemoryStream(blobs[digest]);
            });
        Registry registry = new("localhost:5000", NullLogger.Instance, api.Object);
        BuiltImage image = CreateImage("amd64") with
        {
            Layers = descriptors.Select(d => new ManifestLayer(d.MediaType, d.Size, d.Digest, null)).ToList()
        };
        try
        {
            await using ContainerArchive.LayerDownloads prefetch = new(
                [image, image], new(registry, "repo", "tag", ""), TestContext.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
            Assert.AreEqual(4, Volatile.Read(ref calls));
            gate.SetResult();
            await prefetch.Completion;
            Assert.AreEqual(4, maximum);
            Assert.AreEqual(blobs.Count, calls);
            foreach (var blob in blobs)
            {
                Assert.AreSequenceEqual(blob.Value, await File.ReadAllBytesAsync(await prefetch.GetPathAsync(blob.Key, TestContext.CancellationToken), TestContext.CancellationToken));
            }
        }
        finally
        {
            gate.TrySetResult();
            foreach (Descriptor descriptor in descriptors)
            {
                File.Delete(ContentStore.PathForDescriptor(descriptor));
            }
        }

        static void InterlockedExtensionsMax(ref int location, int value)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref location);
                if (observed >= value)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref location, value, observed) != observed);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Archive_writes_ready_layers_before_later_downloads_and_cancels_on_writer_failure(bool failWriter, bool cancelWriter)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] fast = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
        byte[] slow = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
        Descriptor first = new(SchemaTypes.OciLayerGzipV1, DigestUtils.ComputeSha256Digest(Encoding.UTF8.GetString(fast)), fast.Length);
        Descriptor last = new(SchemaTypes.OciLayerGzipV1, DigestUtils.ComputeSha256Digest(Encoding.UTF8.GetString(slow)), slow.Length);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IRegistryAPI> api = new(MockBehavior.Strict);
        api.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", first.Digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(fast));
        api.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", last.Digest, It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken token) =>
            {
                slowStarted.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return (Stream)new MemoryStream(slow);
            });
        Registry registry = new("localhost:5000", NullLogger.Instance, api.Object);
        BuiltImage image = CreateImage("amd64") with
        {
            Layers = [new(first.MediaType, first.Size, first.Digest, null), new(last.MediaType, last.Size, last.Digest, null)]
        };
        using ObservingArchiveStream archive = new(fast, failWriter, cancelWriter, slowStarted.Task);
        Task write = ContainerArchive.WriteOciImageToStreamAsync(image, new(registry, "repo", "tag", ""),
            new(new ArchiveFileRegistry("unused"), "repo", ["tag"]), archive, timeout.Token);
        try
        {
            await archive.FirstLayerWritten.Task.WaitAsync(timeout.Token);
            Assert.IsFalse(release.Task.IsCompleted);
            if (cancelWriter)
            {
                timeout.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => write);
                await cancelled.Task.WaitAsync(TestContext.CancellationToken);
            }
            else if (failWriter)
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => write);
                await cancelled.Task.WaitAsync(timeout.Token);
            }
            else
            {
                Assert.IsFalse(write.IsCompleted);
                release.SetResult();
                await write;
                archive.Position = 0;
                using TarReader reader = new(archive);
                List<string> layers = [];
                while (await reader.GetNextEntryAsync(cancellationToken: timeout.Token) is { } entry)
                {
                    if (entry.Name == "blobs/sha256/" + first.Digest[7..] || entry.Name == "blobs/sha256/" + last.Digest[7..])
                    {
                        Assert.AreEqual(0, entry.Uid);
                        Assert.AreEqual(0, entry.Gid);
                        Assert.AreEqual(string.Empty, ((PosixTarEntry)entry).UserName);
                        Assert.AreEqual(string.Empty, ((PosixTarEntry)entry).GroupName);
                        layers.Add(entry.Name);
                    }
                }
                Assert.AreSequenceEqual(new[] { "blobs/sha256/" + first.Digest[7..], "blobs/sha256/" + last.Digest[7..] }, layers);
            }
        }
        finally
        {
            release.TrySetResult();
            try { await write; } catch { }
            // Reopen exclusively before deleting: Unix unlink alone would conceal a leaked reader.
            using (File.Open(ContentStore.PathForDescriptor(first), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(ContentStore.PathForDescriptor(first));
            File.Delete(ContentStore.PathForDescriptor(last));
        }
    }

    [TestMethod]
    public async Task Failed_background_download_is_not_reported_as_writer_cancellation()
    {
        byte[] bytes = Guid.NewGuid().ToByteArray();
        string digest = DigestUtils.FormatSha256Digest(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        Descriptor layer = new(SchemaTypes.OciLayerGzipV1, digest, bytes.Length);
        Mock<IRegistryAPI> api = new(MockBehavior.Strict);
        api.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", digest, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidAuthResponseException("localhost:5000", "rejected test authentication"));
        Registry registry = new("localhost:5000", NullLogger.Instance, api.Object);
        BuiltImage image = CreateImage("amd64") with { Layers = [new(layer.MediaType, layer.Size, layer.Digest, null)] };
        using MemoryStream archive = new();
        await Assert.ThrowsExactlyAsync<InvalidAuthResponseException>(() => ContainerArchive.WriteOciImageToStreamAsync(
            image, new(registry, "repo", "tag", ""), new(new ArchiveFileRegistry("unused"), "repo", ["tag"]), archive, TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(ContentStore.PathForDescriptor(layer)));
    }

    [TestMethod]
    [DataRow("digest")]
    [DataRow("io")]
    [DataRow("cancel")]
    public async Task Failed_partial_downloads_leave_no_blob_or_temporary_archive(string failure)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        byte[] expected = Guid.NewGuid().ToByteArray();
        string digest = DigestUtils.FormatSha256Digest(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expected)));
        Descriptor descriptor = new(SchemaTypes.OciLayerGzipV1, digest, expected.Length);
        Mock<IRegistryAPI> api = new(MockBehavior.Strict);
        int requests = 0;
        int disposed = 0;
        api.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", digest, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult<Stream>(new PartialDownloadStream(failure, cancellation, () => Interlocked.Increment(ref disposed)));
            });
        Registry registry = new("localhost:5000", NullLogger.Instance, api.Object, retryDelayProvider: () => TimeSpan.Zero);
        BuiltImage image = CreateImage("amd64") with { Layers = [new(descriptor.MediaType, descriptor.Size, digest, null)] };
        string directory = Directory.CreateTempSubdirectory().FullName;
        string archivePath = Path.Combine(directory, "image.tar");
        await File.WriteAllTextAsync(archivePath, "existing", TestContext.CancellationToken);
        ArchiveFileRegistry destination = new(archivePath);
        try
        {
            Task write = destination.LoadAsync(image, new(registry, "repo", "tag", ""),
                new(destination, "repo", ["tag"]), cancellation.Token, ContainerArchive.WriteOciImageToStreamAsync);
            if (failure == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => write);
            else await Assert.ThrowsExactlyAsync<UnableToDownloadFromRepositoryException>(() => write);
            Assert.AreEqual("existing", await File.ReadAllTextAsync(archivePath, TestContext.CancellationToken));
            Assert.HasCount(1, Directory.GetFiles(directory));
            Assert.IsFalse(File.Exists(ContentStore.PathForDescriptor(descriptor)));
            Assert.IsEmpty(Directory.GetFiles(ContentStore.ContentRoot, DigestUtils.GetEncoded(digest) + ".*.tmp"));
            Assert.AreEqual(failure == "cancel" ? 1 : 5, requests);
            Assert.AreEqual(requests, disposed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class PartialDownloadStream(string failure, CancellationTokenSource cancellation, Action disposed) : Stream
    {
        private bool _read;
        private bool _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_read)
            {
                _read = true;
                "partial"u8.CopyTo(buffer.Span);
                return ValueTask.FromResult(7);
            }
            if (failure == "digest") return ValueTask.FromResult(0);
            if (failure == "cancel")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("fixture body interrupted");
        }
        protected override void Dispose(bool disposing)
        {
            if (!_disposed) { _disposed = true; disposed(); }
            base.Dispose(disposing);
        }
    }

    private sealed class ObservingArchiveStream(byte[] firstLayer, bool failWriter, bool cancelWriter, Task slowStarted) : MemoryStream
    {
        internal TaskCompletionSource FirstLayerWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => buffer.Span.IndexOf(firstLayer) >= 0
                ? new ValueTask(WriteObservedAsync(buffer, cancellationToken))
                : base.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => buffer.AsSpan(offset, count).IndexOf(firstLayer) >= 0
                ? WriteObservedAsync(buffer.AsMemory(offset, count), cancellationToken)
                : base.WriteAsync(buffer, offset, count, cancellationToken);

        private async Task WriteObservedAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await slowStarted.WaitAsync(cancellationToken);
            FirstLayerWritten.TrySetResult();
            if (failWriter) throw new IOException("test archive sink failed");
            if (cancelWriter) await Task.Delay(Timeout.Infinite, cancellationToken);
            base.Write(buffer.Span);
        }
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
