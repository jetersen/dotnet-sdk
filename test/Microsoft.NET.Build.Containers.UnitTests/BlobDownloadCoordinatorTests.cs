// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.Build.Containers.UnitTests;

[TestClass]
public class BlobDownloadCoordinatorTests
{
    public TestContext TestContext { get; }

    public BlobDownloadCoordinatorTests(TestContext testContext) => TestContext = testContext;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task External_lease_wait_and_cancellation_preserve_later_publishers(bool cancelWaiter)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(root, "blob.tar.gz");
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        FileStream? external = null;
        Task<IDisposable>? pending = null;
        try
        {
            external = HoldExternalLease(path, out bool sharingEnforced);
            pending = BlobDownloadCoordinator.AcquireAsync(path, cancellation.Token).AsTask();
            if (sharingEnforced)
            {
                Assert.IsFalse(pending.IsCompleted, "Another publisher still owns the filesystem lease.");
                if (cancelWaiter)
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                    external.Dispose();
                    using IDisposable recovered = await BlobDownloadCoordinator.AcquireAsync(path, TestContext.CancellationToken);
                    return;
                }
            }

            // Unsupported locking must fall back without blocking otherwise valid downloads.
            external.Dispose();
            using IDisposable acquired = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            acquired.Dispose();
        }
        finally
        {
            cancellation.Cancel();
            external?.Dispose();
            if (pending is not null)
            {
                try { (await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken)).Dispose(); }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_contended_blob_does_not_block_a_different_blob()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Task<IDisposable>? pending = null;
        FileStream? external = null;
        try
        {
            string path = Path.Combine(root, "first.tar.gz");
            external = HoldExternalLease(path, out bool sharingEnforced);
            pending = BlobDownloadCoordinator.AcquireAsync(path, cancellation.Token).AsTask();
            if (sharingEnforced) Assert.IsFalse(pending.IsCompleted);
            using IDisposable unrelated = await BlobDownloadCoordinator.AcquireAsync(
                Path.Combine(root, "second.tar.gz"), TestContext.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            external?.Dispose();
            if (pending is not null)
            {
                try { (await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken)).Dispose(); }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Unavailable_lease_storage_preserves_in_process_coordination()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(root, "blob.tar.gz");
        Directory.CreateDirectory(path + ".download.lock");
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        try
        {
            using IDisposable acquired = await BlobDownloadCoordinator.AcquireAsync(path, TestContext.CancellationToken);
            Task<IDisposable> pending = BlobDownloadCoordinator.AcquireAsync(path, cancellation.Token).AsTask();
            Assert.IsFalse(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            acquired.Dispose();
            using IDisposable recovered = await BlobDownloadCoordinator.AcquireAsync(path, TestContext.CancellationToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static FileStream HoldExternalLease(string path, out bool sharingEnforced)
    {
        FileStream held = new(path + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using FileStream second = new(path + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            sharingEnforced = false;
        }
        catch (IOException) { sharingEnforced = true; }
        return held;
    }
}
