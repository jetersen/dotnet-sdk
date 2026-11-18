// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Microsoft.NET.Build.Containers;

/// <summary>Lets a timed-out cache waiter accept a verified winner while its own request is pending.</summary>
internal static class CacheDownloadRace
{
    internal static async Task<T> RunAsync<T>(string path, Func<CancellationToken, Task<T>> download,
        Func<CancellationToken, Task<T?>> readCache, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource cacheCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<T?> cached = WaitForCacheAsync(path, readCache, cacheCancellation.Token);
        Task<T>? pending = null;
        try
        {
            pending = download(downloadCancellation.Token);
            await Task.WhenAny((Task)pending, cached).ConfigureAwait(false);
            if (cached.IsCompletedSuccessfully && cached.Result is { } winner)
            {
                downloadCancellation.Cancel();
                await ObserveCompletionAsync(pending).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return winner;
            }
            return await pending.ConfigureAwait(false);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Verification can finish while the failed transfer continuation is running.
            if (cached.IsCompletedSuccessfully && cached.Result is { } winner) return winner;
            throw;
        }
        finally
        {
            downloadCancellation.Cancel();
            cacheCancellation.Cancel();
            // The selected branch determines the result. Observe the other task and
            // await its cleanup without replacing that result with a losing exception.
            if (pending is not null) await ObserveCompletionAsync(pending).ConfigureAwait(false);
            await ObserveCompletionAsync(cached).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task<T?> WaitForCacheAsync<T>(string path, Func<CancellationToken, Task<T?>> readCache,
        CancellationToken cancellationToken) where T : class
    {
        (long Length, long Modified)? examined = null;
        long lastRead = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo file = new(path);
                if (file.Exists)
                {
                    var stamp = (file.Length, file.LastWriteTimeUtc.Ticks);
                    if (examined != stamp || Stopwatch.GetElapsedTime(lastRead) >= TimeSpan.FromSeconds(5))
                    {
                        // Metadata only triggers validation; it never establishes a hit.
                        if (await readCache(cancellationToken).ConfigureAwait(false) is { } winner) return winner;
                        examined = stamp;
                        lastRead = Stopwatch.GetTimestamp();
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Optional cache access must not replace the original transport result.
                return null;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }
}
