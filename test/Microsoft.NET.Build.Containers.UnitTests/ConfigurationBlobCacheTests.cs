// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Microsoft.NET.Build.Containers.UnitTests;

[TestClass]
public class ConfigurationBlobCacheTests
{
    public TestContext TestContext { get; }
    public ConfigurationBlobCacheTests(TestContext testContext) => TestContext = testContext;

    [TestMethod]
    public async Task A_cache_read_parses_only_the_verified_snapshot()
    {
        byte[] verified = """{"value":1}"""u8.ToArray();
        byte[] changed = """{"value":2}"""u8.ToArray();
        using Stream source = new RewrittenOnRewindStream(verified, changed);
        JsonNode? document = await ConfigurationBlobCache.ReadVerifiedAsync(source,
            DigestUtils.ComputeSha256Digest("""{"value":1}"""), TestContext.CancellationToken);
        Assert.AreEqual(1, document!["value"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Configuration_is_verified_repaired_and_parsed_independently()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        byte[] content = "{\"value\":1}"u8.ToArray();
        string digest = DigestUtils.ComputeSha256Digest("{\"value\":1}");
        int fetches = 0;
        try
        {
            JsonNode first = await Get();
            first["value"] = 2;
            JsonNode second = await Get();
            Assert.AreEqual(1, second["value"]!.GetValue<int>());
            Assert.AreEqual(1, fetches);
            await File.WriteAllTextAsync(Directory.GetFiles(root, "*.config.json").Single(), "corrupt", TestContext.CancellationToken);
            JsonNode repaired = await Get();
            Assert.AreEqual(1, repaired["value"]!.GetValue<int>());
            Assert.AreEqual(2, fetches);
            Assert.IsEmpty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }

        Task<JsonNode> Get() => ConfigurationBlobCache.GetAsync(root, digest, _ =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult<ReadOnlyMemory<byte>>(content);
        }, TestContext.CancellationToken);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_or_cancelled_owner_does_not_poison_another_publisher(bool cancelOwner)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string digest = DigestUtils.ComputeSha256Digest("{}");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using CancellationTokenSource ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<JsonNode>? owner = null;
        Task<JsonNode>? waiter = null;
        try
        {
            owner = ConfigurationBlobCache.GetAsync(root, digest, async token =>
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                throw new HttpRequestException("owner transport failed");
            }, ownerCancellation.Token);
            await started.Task.WaitAsync(timeout.Token);
            int waiterFetches = 0;
            waiter = ConfigurationBlobCache.GetAsync(root, digest, _ =>
            {
                waiterFetches++;
                return Task.FromResult<ReadOnlyMemory<byte>>("{}"u8.ToArray());
            }, timeout.Token);
            Assert.IsFalse(waiter.IsCompleted);
            if (cancelOwner)
            {
                ownerCancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => owner);
            }
            else
            {
                release.SetResult();
                await Assert.ThrowsExactlyAsync<HttpRequestException>(() => owner);
            }
            Assert.IsNotNull(await waiter.WaitAsync(timeout.Token));
            Assert.AreEqual(1, waiterFetches);
        }
        finally
        {
            timeout.Cancel();
            ownerCancellation.Cancel();
            release.TrySetResult();
            foreach (Task<JsonNode> task in new[] { owner, waiter }.OfType<Task<JsonNode>>())
            {
                try { await task; }
                catch (Exception) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Concurrent_configuration_requests_share_one_fetch()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string digest = DigestUtils.ComputeSha256Digest("{}");
        int requests = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<JsonNode>[] readers = Enumerable.Range(0, 8).Select(_ => ConfigurationBlobCache.GetAsync(root, digest, async token =>
            {
                Interlocked.Increment(ref requests);
                await release.Task.WaitAsync(token);
                return "{}"u8.ToArray();
            }, TestContext.CancellationToken)).ToArray();
            release.SetResult();
            await Task.WhenAll(readers);
            Assert.AreEqual(1, requests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Unavailable_cache_storage_does_not_fail_a_verified_configuration()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string unavailable = Path.Combine(root, "file-instead-of-directory");
            await File.WriteAllTextAsync(unavailable, "occupied", TestContext.CancellationToken);
            JsonNode result = await ConfigurationBlobCache.GetAsync(
                unavailable, DigestUtils.ComputeSha256Digest("{}"),
                _ => Task.FromResult<ReadOnlyMemory<byte>>("{}"u8.ToArray()), TestContext.CancellationToken);
            Assert.AreEqual("{}", result.ToJsonString());
            Assert.AreEqual("occupied", await File.ReadAllTextAsync(unavailable, TestContext.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task A_cancelled_request_never_fetches_or_writes_a_configuration()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => ConfigurationBlobCache.GetAsync(root,
                DigestUtils.ComputeSha256Digest("{}"), _ => throw new InvalidOperationException("Must not fetch"), cancellation.Token));
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task The_original_transport_failure_is_preserved()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        HttpRequestException expected = new("original transport error");
        try
        {
            HttpRequestException actual = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => ConfigurationBlobCache.GetAsync(root,
                DigestUtils.ComputeSha256Digest("{}"), _ => Task.FromException<ReadOnlyMemory<byte>>(expected), TestContext.CancellationToken));
            Assert.AreSame(expected, actual);
            Assert.IsEmpty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Incorrect_network_content_is_rejected_without_caching()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDigestException>(() => ConfigurationBlobCache.GetAsync(
                root, DigestUtils.ComputeSha256Digest("{}"),
                _ => Task.FromResult<ReadOnlyMemory<byte>>("{\"changed\":true}"u8.ToArray()), TestContext.CancellationToken));
            // Empty coordination files persist so waiting publishers keep the same inode.
            foreach (string file in Directory.GetFiles(root))
            {
                Assert.EndsWith(".download.lock", file);
                Assert.AreEqual(0L, new FileInfo(file).Length);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RewrittenOnRewindStream(byte[] original, byte[] changed) : MemoryStream(original, writable: true)
    {
        public override long Position
        {
            get => base.Position;
            set
            {
                if (value == 0 && base.Position > 0)
                {
                    base.Position = 0;
                    base.Write(changed);
                }
                base.Position = value;
            }
        }
    }
}
