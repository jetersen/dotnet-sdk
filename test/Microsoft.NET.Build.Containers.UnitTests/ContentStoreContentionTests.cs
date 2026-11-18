// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Microsoft.NET.Build.Containers.UnitTests;

[TestClass]
public class ContentStoreContentionTests
{
    public TestContext TestContext { get; set; } = default!;

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task Committed_blob_readers_allow_a_renaming_handle_to_retain_delete_access()
    {
        byte[] content = Guid.NewGuid().ToByteArray();
        Descriptor descriptor = new(SchemaTypes.OciLayerGzipV1,
            DigestUtils.FormatSha256Digest(Convert.ToHexStringLower(SHA256.HashData(content))), content.Length);
        string destination = ContentStore.PathForDescriptor(descriptor);
        string temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            // DeleteOnClose requests DELETE access, as a rename handle does. Keep that
            // handle open after the rename to make the Windows visibility window deterministic.
            using FileStream renaming = new(temporary, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete, bufferSize: 1, FileOptions.DeleteOnClose);
            File.Move(temporary, destination);
            Assert.ThrowsExactly<IOException>(() => { using FileStream denied = File.OpenRead(destination); });
            using FileStream reader = ContentStore.OpenCommittedBlob(destination, TestContext.CancellationToken);
            Assert.AreEqual(content.Length, reader.Length);
            using Stream pushReader = Layer.FromDescriptor(descriptor).OpenBackingFile();
            Assert.AreEqual(content.Length, pushReader.Length);
            Mock<IRegistryAPI> api = new(MockBehavior.Strict);
            string cached = await new Registry("localhost:5000", NullLogger.Instance, api.Object)
                .DownloadBlobAsync("repo", descriptor, TestContext.CancellationToken);
            Assert.AreEqual(destination, cached);
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(destination);
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task Commit_waits_for_a_temporarily_exclusive_destination(bool cancel, bool corrupt)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string temporary = Path.Combine(root, "download.tmp");
        string destination = Path.Combine(root, "blob");
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        FileStream? held = null;
        Task? commit = null;
        try
        {
            File.WriteAllText(temporary, "download");
            File.WriteAllText(destination, corrupt ? "corrupt" : "download");
            held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            commit = Task.Run(() =>
            {
                started.SetResult();
                ContentStore.CommitBlob(temporary, destination, DigestUtils.ComputeSha256Digest("download"), cancellation.Token);
            }, TestContext.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
            await Task.Delay(100, TestContext.CancellationToken);
            Assert.IsFalse(commit.IsCompleted, "Commit must wait until the competing file operation releases its exclusive handle.");
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => commit.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
            }
            else
            {
                held.Dispose();
                held = null;
                if (corrupt)
                {
                    await Assert.ThrowsExactlyAsync<InvalidDigestException>(() => commit.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
                }
                else
                {
                    await commit.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
                    Assert.AreEqual("download", File.ReadAllText(destination));
                }
            }
            Assert.IsFalse(File.Exists(temporary));
        }
        finally
        {
            cancellation.Cancel();
            held?.Dispose();
            if (commit is not null)
            {
                try { await commit; }
                catch (Exception) when (commit.IsFaulted || commit.IsCanceled) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task Commit_does_not_wait_indefinitely_for_an_exclusive_destination()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string temporary = Path.Combine(root, "download.tmp");
        string destination = Path.Combine(root, "blob");
        try
        {
            File.WriteAllText(temporary, "download");
            File.WriteAllText(destination, "download");
            using FileStream held = new(destination, FileMode.Open, FileAccess.Read, FileShare.None);
            Task commit = Task.Run(() => ContentStore.CommitBlob(temporary, destination,
                DigestUtils.ComputeSha256Digest("download"), TestContext.CancellationToken), TestContext.CancellationToken);
            await Assert.ThrowsExactlyAsync<IOException>(() => commit.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
            Assert.IsFalse(File.Exists(temporary));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
