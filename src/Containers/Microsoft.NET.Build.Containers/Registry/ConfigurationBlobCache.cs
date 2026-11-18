// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.NET.Build.Containers;

/// <summary>Caches immutable base configurations after their manifest has been resolved.</summary>
internal static class ConfigurationBlobCache
{
    internal static async Task<JsonNode> GetAsync(
        string digest, Func<CancellationToken, Task<ReadOnlyMemory<byte>>> fetch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? contentRoot = null;
        try
        {
            contentRoot = ContentStore.ContentRoot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            TypeInitializationException { InnerException: IOException or UnauthorizedAccessException })
        {
            // Configuration caching is optional, including resolving the cache directory.
        }
        return contentRoot is null
            ? await FetchAndSaveAsync(null, null, digest, fetch, cancellationToken).ConfigureAwait(false)
            : await GetAsync(contentRoot, digest, fetch, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<JsonNode> GetAsync(
        string contentRoot, string digest, Func<CancellationToken, Task<ReadOnlyMemory<byte>>> fetch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(contentRoot, DigestUtils.GetEncoded(digest) + ".config.json");
        if (await TryReadAsync(path, digest, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        using IDisposable lease = await BlobDownloadCoordinator.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
        if (await TryReadAsync(path, digest, cancellationToken).ConfigureAwait(false) is { } winner)
        {
            return winner;
        }

        // Preserve the transport's existing retries and exceptions. A failed owner
        // releases its lease so another project can retry using its own transport.
        return await BlobDownloadCoordinator.CompleteAsync<JsonNode>(lease, path,
            token => FetchAndSaveAsync(path, contentRoot, digest, fetch, token),
            token => TryReadAsync(path, digest, token), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode> FetchAndSaveAsync(string? path, string? contentRoot, string digest,
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> fetch, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> content = await fetch(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        DigestUtils.ValidateDigestContent(digest, content.Span);
        cancellationToken.ThrowIfCancellationRequested();
        JsonNode? document = JsonNode.Parse(content.Span);
        Debug.Assert(document is not null);
        if (document is not null && path is not null && contentRoot is not null)
        {
            await SaveAsync(path, contentRoot, content, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return document!;
    }

    private static async Task<JsonNode?> TryReadAsync(string path, string digest, CancellationToken cancellationToken)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await ReadVerifiedAsync(stream, digest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDigestException or JsonException)
        {
            return null;
        }
    }

    internal static async Task<JsonNode?> ReadVerifiedAsync(Stream stream, string digest, CancellationToken cancellationToken)
    {
        using MemoryStream snapshot = new();
        await stream.CopyToAsync(snapshot, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> content = snapshot.GetBuffer().AsSpan(0, checked((int)snapshot.Length));
        DigestUtils.ValidateDigestContent(digest, content);
        cancellationToken.ThrowIfCancellationRequested();
        JsonNode? document = JsonNode.Parse(content);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    private static async Task SaveAsync(string path, string contentRoot, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string? temporary = null;
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(contentRoot);
            else Directory.CreateDirectory(contentRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            temporary = Path.Combine(contentRoot, $"{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache write cannot invalidate the configuration already fetched and verified.
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Optional cache cleanup must not replace the publish result or cancellation.
                }
            }
        }
    }
}
