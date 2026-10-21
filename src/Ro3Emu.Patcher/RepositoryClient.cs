using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ro3Emu.Patcher;

/// <summary>Reads a release repository over HTTP(S). Only <c>channels/*.json</c> and <c>news.json</c> are fetched uncached.</summary>
public sealed class RepositoryClient : IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsClient;

    public RepositoryClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/");
        }
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                MaxConnectionsPerServer = 16,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = baseUri;
        // No overall limit here: small requests get RequestTimeout each (Within), and the launcher's own download only
        // gives up when it stalls, so a slow connection can still fetch a 60+ MB launcher.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ro3EmuLauncher/1.0");
    }

    public Uri BaseUri => _http.BaseAddress!;

    /// <summary>The time limit for one channel, manifest, chunk or small-file request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary><see cref="DownloadFileAsync"/> gives up on an attempt when no data arrives for this long.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The wait before <see cref="DownloadFileAsync"/>'s next attempt, times the attempt number.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    CancellationTokenSource Within(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        return cts;
    }

    public async Task<ChannelInfo> GetChannelAsync(string channel, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"channels/{channel}.json");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var limit = Within(ct);
        using var response = await _http.SendAsync(request, limit.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new PatcherException($"Channel '{channel}' does not exist on the update server.");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
        return await JsonSerializer.DeserializeAsync<ChannelInfo>(stream, Json.Compact, limit.Token)
            ?? throw new PatcherException("The update server returned an empty channel.");
    }

    public async Task<(Manifest Manifest, byte[] Raw)> GetManifestAsync(ChannelInfo channel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(channel.Manifest))
        {
            throw new PatcherException($"Channel '{channel.Channel}' has no game release on the update server.");
        }
        PathRules.ValidateRelative(channel.Manifest);
        using var limit = Within(ct);
        byte[] raw = await _http.GetByteArrayAsync(channel.Manifest, limit.Token);
        if (!string.Equals(Hashing.Sha256Hex(raw), channel.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatcherException("The release manifest is corrupt (hash mismatch). Try again in a moment.");
        }
        return (Manifest.FromCompressedBytes(raw), raw);
    }

    /// <summary>Downloads one stored chunk; <paramref name="onBytes"/> reports compressed bytes as they arrive.</summary>
    public async Task<byte[]> GetChunkAsync(string hash, int storedSize, Action<int>? onBytes, CancellationToken ct)
    {
        using var limit = Within(ct);
        ct = limit.Token;
        using var response = await _http.GetAsync(PathRules.ChunkRelative(hash), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[storedSize];
        int total = 0;
        while (total < storedSize)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(64 * 1024, storedSize - total)), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
            onBytes?.Invoke(read);
        }
        if (total != storedSize || await stream.ReadAsync(new byte[1], ct) != 0)
        {
            throw new IOException($"Chunk {hash[..12]} has the wrong size.");
        }
        return buffer;
    }

    public async Task<byte[]?> TryGetBytesAsync(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var limit = Within(ct);
        using var response = await _http.SendAsync(request, limit.Token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync(limit.Token);
    }

    /// <summary>
    /// Downloads a large file (the launcher's own update) to <paramref name="path"/>, streaming to disk. A broken
    /// download carries on where it stopped (HTTP range), and an attempt only fails when no data arrives for
    /// <see cref="StallTimeout"/>, so a slow connection gets there too. <paramref name="progress"/> gets (bytes so far,
    /// total or -1).
    /// </summary>
    public async Task DownloadFileAsync(string relative, string path, Action<long, long>? progress, CancellationToken ct, int attempts = 5)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(relative, path, progress, ct);
                return;
            }
            catch (Exception e) when (attempt < attempts && !ct.IsCancellationRequested && Retryable(e))
            {
                await Task.Delay(RetryDelay * attempt, ct);
            }
        }
    }

    /// <summary>A dropped connection, a stall or a server error is worth another try; a 4xx answer is not.</summary>
    static bool Retryable(Exception e) => e switch
    {
        HttpRequestException h => h.StatusCode is not { } status || (int)status >= 500,
        IOException or OperationCanceledException => true,
        _ => false,
    };

    async Task DownloadOnceAsync(string relative, string path, Action<long, long>? progress, CancellationToken ct)
    {
        long have = File.Exists(path) ? new FileInfo(path).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(path); // the file changed on the server: start over
            throw new IOException("The download no longer matches the file on the update server.");
        }
        response.EnsureSuccessStatusCode();
        bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
        {
            have = 0; // the server sent the whole file
        }
        long total = response.Content.Headers.ContentLength is { } length ? have + length : -1;
        await using var stream = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var file = new FileStream(path, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        byte[] buffer = new byte[1 << 16];
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int read = await stream.ReadAsync(buffer, stall.Token);
            if (read == 0)
            {
                break;
            }
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            have += read;
            progress?.Invoke(have, total);
        }
        if (total >= 0 && have != total)
        {
            throw new IOException($"The download stopped at {have} of {total} bytes.");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>An expected failure whose message can be shown to the player as is.</summary>
public sealed class PatcherException(string message, Exception? inner = null) : Exception(message, inner);
