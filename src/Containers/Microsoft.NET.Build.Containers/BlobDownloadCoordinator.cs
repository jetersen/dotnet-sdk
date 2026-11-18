// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Microsoft.NET.Build.Containers;

/// <summary>Coordinates cache misses without sharing registry clients or credentials.</summary>
internal static class BlobDownloadCoordinator
{
    private const int WindowsSharingViolation = unchecked((int)0x80070020);
    private const int LinuxWouldBlock = 11;
    private const int MacOSWouldBlock = 35;
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(30);
    private static readonly Lock s_sync = new();
    private static readonly Dictionary<string, Entry> s_entries = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static async ValueTask<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        Entry entry;
        lock (s_sync)
        {
            if (!s_entries.TryGetValue(path, out entry!))
            {
                entry = new();
                s_entries.Add(path, entry);
            }
            entry.Users++;
        }
        bool acquired = false;
        try
        {
            TimeSpan remaining = MaximumWait - Stopwatch.GetElapsedTime(started);
            acquired = remaining > TimeSpan.Zero &&
                await entry.Semaphore.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new Lease(path, entry, null, monitorCache: true, ownsSemaphore: false);
            }
            ProcessLease processLease = await AcquireProcessLeaseAsync(path, started, cancellationToken).ConfigureAwait(false);
            return new Lease(path, entry, processLease.Handle, processLease.TimedOut);
        }
        catch
        {
            if (acquired)
            {
                entry.Semaphore.Release();
            }
            RemoveUser(path, entry);
            throw;
        }
    }

    private readonly record struct ProcessLease(FileStream? Handle, bool TimedOut);

    internal static Task<T> CompleteAsync<T>(IDisposable lease, string path,
        Func<CancellationToken, Task<T>> download, Func<CancellationToken, Task<T?>> readCache,
        CancellationToken cancellationToken) where T : class
        => lease is Lease { MonitorCache: true }
            ? CacheDownloadRace.RunAsync(path, download, readCache, cancellationToken)
            : download(cancellationToken);

    private static async ValueTask<ProcessLease> AcquireProcessLeaseAsync(string path, long started, CancellationToken cancellationToken)
    {
        // Keep the file after release. Unlinking could let waiters lock different inodes.
        string leasePath = path + ".download.lock";
        int retryDelayMilliseconds = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? held = null;
            try
            {
                held = OpenLease();
                // Filesystems that do not enforce sharing simply allow independent downloads.
                return new ProcessLease(held, TimedOut: false);
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                held?.Dispose();
                // Coordination is optional. Never wait indefinitely for an unresponsive owner.
                if (Stopwatch.GetElapsedTime(started) >= MaximumWait)
                {
                    return new ProcessLease(null, TimedOut: true);
                }
                // Most local downloads finish quickly; avoid imposing a full 50 ms wait.
                // Back off to the existing cap when the owner remains busy.
                await Task.Delay(retryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                retryDelayMilliseconds = Math.Min(retryDelayMilliseconds * 2, 50);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                held?.Dispose();
                // Download independently; existing digest checks and atomic commits still apply.
                return default;
            }
        }

        FileStream OpenLease()
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            return new FileStream(leasePath, options);
        }
    }

    private static bool IsSharingViolation(IOException error)
        => error.HResult == (OperatingSystem.IsMacOS() ? MacOSWouldBlock : OperatingSystem.IsWindows() ? WindowsSharingViolation : LinuxWouldBlock);

    private static void RemoveUser(string path, Entry entry)
    {
        lock (s_sync)
        {
            if (--entry.Users == 0)
            {
                s_entries.Remove(path);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }

    private sealed class Lease(string path, Entry entry, FileStream? processLease, bool monitorCache, bool ownsSemaphore = true) : IDisposable
    {
        internal bool MonitorCache { get; } = monitorCache;
        private Entry? _entry = entry;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _entry, null) is Entry owned)
            {
                try { processLease?.Dispose(); }
                finally
                {
                    if (ownsSemaphore) owned.Semaphore.Release();
                    RemoveUser(path, owned);
                }
            }
        }
    }
}
