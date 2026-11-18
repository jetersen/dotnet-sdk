// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Microsoft.NET.Build.Containers.UnitTests;

[TestClass]
public class SharedBlobDownloadTests
{
    public TestContext TestContext { get; }
    public SharedBlobDownloadTests(TestContext testContext) => TestContext = testContext;

    [TestMethod]
    public async Task Independent_registry_instances_download_shared_content_once()
    {
        byte[] content = Guid.NewGuid().ToByteArray();
        Descriptor descriptor = DescriptorFor(content);
        Mock<IRegistryAPI> api = new(MockBehavior.Strict);
        api.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Yield(); return (Stream)new MemoryStream(content); });
        try
        {
            string[] paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                new Registry("localhost:5000", NullLogger.Instance, api.Object)
                    .DownloadBlobAsync("repo", descriptor, TestContext.CancellationToken)));
            Assert.HasCount(1, paths.Distinct().ToArray());
            Assert.AreSequenceEqual(content, await File.ReadAllBytesAsync(paths[0], TestContext.CancellationToken));
            api.Verify(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()), Times.Once);
            await new Registry("localhost:5000", NullLogger.Instance, api.Object)
                .DownloadBlobAsync("repo", descriptor, TestContext.CancellationToken);
            api.Verify(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { RemoveOwnedBlob(descriptor); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_or_cancelled_partial_owner_does_not_poison_an_independent_waiter(bool cancelOwner)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using CancellationTokenSource ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using CancellationTokenSource waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        byte[] content = Guid.NewGuid().ToByteArray();
        Descriptor descriptor = DescriptorFor(content);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        int disposed = 0;
        Mock<IRegistryAPI> ownerApi = new(MockBehavior.Strict);
        ownerApi.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult<Stream>(new FailingBody(content, started, release.Task, () => Interlocked.Increment(ref disposed)));
            });
        Mock<IRegistryAPI> waiterApi = new(MockBehavior.Strict);
        waiterApi.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(content));
        Registry ownerRegistry = new("localhost:5000", NullLogger.Instance, ownerApi.Object, retryDelayProvider: () => TimeSpan.Zero);
        Registry waiterRegistry = new("localhost:5000", NullLogger.Instance, waiterApi.Object);
        Task<string>? owner = null;
        Task<string>? waiter = null;
        try
        {
            owner = ownerRegistry.DownloadBlobAsync("repo", descriptor, ownerCancellation.Token);
            await started.Task.WaitAsync(timeout.Token);
            Assert.IsNotEmpty(Directory.GetFiles(ContentStore.ContentRoot, DigestUtils.GetEncoded(descriptor.Digest) + ".*.tmp"));
            waiter = waiterRegistry.DownloadBlobAsync("repo", descriptor, waiterCancellation.Token);
            Assert.IsFalse(waiter.IsCompleted);
            if (cancelOwner)
            {
                ownerCancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => owner);
            }
            else
            {
                release.TrySetResult();
                await Assert.ThrowsExactlyAsync<UnableToDownloadFromRepositoryException>(() => owner);
            }
            string path = await waiter.WaitAsync(timeout.Token);
            Assert.AreSequenceEqual(content, await File.ReadAllBytesAsync(path, timeout.Token));
            Assert.AreEqual(cancelOwner ? 1 : 5, requests);
            Assert.AreEqual(requests, disposed);
            waiterApi.Verify(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()), Times.Once);
            Assert.IsEmpty(Directory.GetFiles(ContentStore.ContentRoot, DigestUtils.GetEncoded(descriptor.Digest) + ".*.tmp"));
        }
        finally
        {
            ownerCancellation.Cancel();
            waiterCancellation.Cancel();
            release.TrySetResult();
            foreach (Task<string> task in new[] { owner, waiter }.OfType<Task<string>>())
            {
                try { await task; }
                catch (Exception) { }
            }
            RemoveOwnedBlob(descriptor);
        }
    }

    [TestMethod]
    public async Task A_stalled_owner_does_not_block_a_healthy_registry_beyond_the_lease_budget()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using CancellationTokenSource ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        byte[] content = Guid.NewGuid().ToByteArray();
        Descriptor descriptor = DescriptorFor(content);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IRegistryAPI> ownerApi = new(MockBehavior.Strict);
        ownerApi.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FailingBody(content, started, release.Task, () => { }));
        Mock<IRegistryAPI> healthyApi = new(MockBehavior.Strict);
        healthyApi.Setup(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(content));
        Task<string>? owner = null;
        Task<string>? healthy = null;
        try
        {
            owner = new Registry("localhost:5000", NullLogger.Instance, ownerApi.Object)
                .DownloadBlobAsync("repo", descriptor, ownerCancellation.Token);
            await started.Task.WaitAsync(timeout.Token);
            healthy = new Registry("localhost:5000", NullLogger.Instance, healthyApi.Object)
                .DownloadBlobAsync("repo", descriptor, timeout.Token);
            string path = await healthy.WaitAsync(timeout.Token);
            Assert.IsFalse(owner.IsCompleted, "A healthy waiter must finish without cancelling the stalled owner.");
            Assert.AreSequenceEqual(content, await File.ReadAllBytesAsync(path, timeout.Token));
            healthyApi.Verify(a => a.Blob.GetUnvalidatedStreamAsync("repo", descriptor.Digest, It.IsAny<CancellationToken>()), Times.Once);
            ownerCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => owner);
            // In particular, the timed-out waiter must not release the owner's semaphore.
            using IDisposable recovered = await BlobDownloadCoordinator.AcquireAsync(path, timeout.Token);
            Assert.IsEmpty(Directory.GetFiles(ContentStore.ContentRoot, DigestUtils.GetEncoded(descriptor.Digest) + ".*.tmp"));
        }
        finally
        {
            timeout.Cancel();
            ownerCancellation.Cancel();
            release.TrySetResult();
            foreach (Task<string> task in new[] { owner, healthy }.OfType<Task<string>>())
            {
                try { await task; }
                catch (Exception) { }
            }
            RemoveOwnedBlob(descriptor);
        }
    }

    private static Descriptor DescriptorFor(byte[] bytes) => new(SchemaTypes.OciLayerGzipV1,
        DigestUtils.FormatSha256Digest(Convert.ToHexStringLower(SHA256.HashData(bytes))), bytes.Length);

    private static void RemoveOwnedBlob(Descriptor descriptor)
    {
        string path = ContentStore.PathForDescriptor(descriptor);
        File.Delete(path);
        // These fixtures use unique digests and await every publisher before cleanup.
        File.Delete(path + ".download.lock");
    }

    private sealed class FailingBody(byte[] bytes, TaskCompletionSource started, Task release, Action disposed) : Stream
    {
        private bool _read;
        private bool _disposed;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_read)
            {
                _read = true;
                bytes.AsSpan(0, 8).CopyTo(buffer.Span);
                return 8;
            }
            started.TrySetResult();
            await release.WaitAsync(cancellationToken);
            throw new IOException("fixture body interrupted");
        }
        protected override void Dispose(bool disposing)
        {
            if (!_disposed) { _disposed = true; disposed(); }
            base.Dispose(disposing);
        }
    }
}
