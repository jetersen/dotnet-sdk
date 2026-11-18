// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Microsoft.NET.Build.Containers.UnitTests;

[TestClass]
public class CacheDownloadRaceTests
{
    public TestContext TestContext { get; }
    public CacheDownloadRaceTests(TestContext testContext) => TestContext = testContext;

    [TestMethod]
    public async Task A_verified_winner_cancels_the_redundant_download_even_with_unchanged_metadata()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(root, "blob");
        string digest = DigestUtils.ComputeSha256Digest("correct");
        TaskCompletionSource rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Task<string>? pending = null;
        try
        {
            await File.WriteAllTextAsync(path, "corrupt", cancellation.Token);
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch);
            pending = CacheDownloadRace.RunAsync<string>(path, async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); return "unused"; }
                finally { stopped.TrySetResult(); }
            }, ReadVerified, cancellation.Token);
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            Assert.IsFalse(pending.IsCompleted, "A corrupt candidate must not establish a cache hit.");
            // Same length and timestamp deliberately defeat a metadata-only check.
            await File.WriteAllTextAsync(path + ".tmp", "correct", cancellation.Token);
            File.SetLastWriteTimeUtc(path + ".tmp", DateTime.UnixEpoch);
            File.Move(path + ".tmp", path, overwrite: true);
            Assert.AreEqual("correct", await pending.WaitAsync(TimeSpan.FromSeconds(8), cancellation.Token));
            Assert.IsTrue(stopped.Task.IsCompletedSuccessfully, "The losing transport must finish cleanup before returning.");
        }
        finally
        {
            cancellation.Cancel();
            if (pending is not null)
            {
                try { await pending; }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(root, recursive: true);
        }

        async Task<string?> ReadVerified(CancellationToken token)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            try { DigestUtils.ValidateDigestContent(digest, bytes); }
            catch (InvalidDigestException) { rejected.TrySetResult(); return null; }
            return Encoding.UTF8.GetString(bytes);
        }
    }

    [TestMethod]
    public async Task A_verified_winner_is_not_replaced_by_a_cancelled_transfers_cleanup_error()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(root, "candidate");
        bool stopped = false;
        try
        {
            await File.WriteAllTextAsync(path, "verified", TestContext.CancellationToken);
            string result = await CacheDownloadRace.RunAsync<string>(path, async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); return "unused"; }
                finally { stopped = true; throw new IOException("losing transfer cleanup failed"); }
            }, _ => Task.FromResult<string?>("verified"), TestContext.CancellationToken);
            Assert.AreEqual("verified", result);
            Assert.IsTrue(stopped);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task The_original_transport_error_is_preserved()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        HttpRequestException expected = new("original transport failure");
        try
        {
            HttpRequestException actual = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => CacheDownloadRace.RunAsync<string>(
                Path.Combine(root, "missing"), _ => Task.FromException<string>(expected),
                _ => Task.FromResult<string?>(null), TestContext.CancellationToken));
            Assert.AreSame(expected, actual);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task A_completed_download_cancels_and_awaits_the_cache_reader()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(root, "candidate");
        bool readerStopped = false;
        try
        {
            await File.WriteAllTextAsync(path, "candidate", TestContext.CancellationToken);
            string result = await CacheDownloadRace.RunAsync<string>(path, _ => Task.FromResult("download"), async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { readerStopped = true; }
            }, TestContext.CancellationToken);
            Assert.AreEqual("download", result);
            Assert.IsTrue(readerStopped);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task User_cancellation_stops_both_operations()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(root, "candidate");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool downloadStopped = false;
        bool readerStopped = false;
        Task<string>? pending = null;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        try
        {
            await File.WriteAllTextAsync(path, "candidate", cancellation.Token);
            pending = CacheDownloadRace.RunAsync<string>(path, async token =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); return "unused"; }
                finally { downloadStopped = true; }
            }, async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { readerStopped = true; }
            }, cancellation.Token);
            await started.Task.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            Assert.IsTrue(downloadStopped);
            Assert.IsTrue(readerStopped);
        }
        finally
        {
            cancellation.Cancel();
            if (pending is not null)
            {
                try { await pending; }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task An_unavailable_cache_does_not_replace_a_completed_download()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(root, "candidate");
        try
        {
            await File.WriteAllTextAsync(path, "candidate", TestContext.CancellationToken);
            string result = await CacheDownloadRace.RunAsync<string>(path, _ => Task.FromResult("download"),
                _ => throw new IOException("cache unavailable"), TestContext.CancellationToken);
            Assert.AreEqual("download", result);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
