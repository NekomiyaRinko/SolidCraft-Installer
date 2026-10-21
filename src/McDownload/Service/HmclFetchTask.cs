/*
 * Candidate fetching and checksum-aware downloads adapted from HMCL FetchTask and CacheFileTask.
 * Copyright (C) 2020-2026 huangyuhui and HMCL contributors.
 * This file is distributed under GNU GPL version 3 or later with the
 * additional terms in HMCL-ADDITIONAL-TERMS.md.
 * Modified for SCI (C# port), 2026-10-21.
 * Upstream: https://github.com/HMCL-dev/HMCL, commit acc9740fa9289ded38553b6a35045fd608c1ea99.
 */
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using McDownload.Model;

namespace McDownload.Service;

public sealed class HmclFetchTask(HttpClient http, HmclDownloadProvider provider, DiagnosticLog? diagnostics = null)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private const long MaximumDownloadBytes = 2L * 1024 * 1024 * 1024;

    public Task<T> ReadAsync<T>(Uri uri, int maximum, Func<byte[], T> parse, CancellationToken token, Uri? mirror = null)
    {
        ArgumentNullException.ThrowIfNull(parse);
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        return ExecuteAsync(uri, async (address, ct) =>
            parse(await ReadBytesAsync(address, maximum, ct)), token, mirror, TimeSpan.FromSeconds(30));
    }

    public Task DownloadAsync(Uri uri, string path, string? sha1, long? size, CancellationToken token,
        Action<long, long?>? progress = null)
    {
        if (sha1 is not null) VersionResolver.ValidateSha1(sha1);
        if (size is < 0 or > MaximumDownloadBytes) throw new InvalidDataException("下载文件大小超出允许范围。");
        return ExecuteAsync(uri, async (address, ct) =>
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
            Uri final = response.RequestMessage?.RequestUri ?? address;
            VersionResolver.RequireHttps(final);
            diagnostics?.Write("HTTP", $"Response status={(int)response.StatusCode}; requested={DiagnosticText.Address(address)}; final={DiagnosticText.Address(final)}");
            EnsureSuccess(response, address);
            if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaximumDownloadBytes)
                throw new InvalidDataException("下载文件超过大小上限。");

            await InstanceManager.AtomicWriteAsync(path, async (output, cancellation) =>
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                byte[] buffer = new byte[65536];
                long completed = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    completed += count;
                    if (completed > MaximumDownloadBytes) throw new InvalidDataException("下载文件超过大小上限。");
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    progress?.Invoke(completed, size ?? response.Content.Headers.ContentLength);
                }

                long? expectedSize = size ?? response.Content.Headers.ContentLength;
                if (expectedSize is long expected && completed != expected)
                    throw new DownloadIntegrityException("下载文件大小不一致。");
                if (sha1 is not null && !Convert.ToHexString(digest.GetHashAndReset()).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    throw new DownloadIntegrityException("下载文件 SHA-1 校验失败。");
            }, ct);
            return true;
        }, token, timeout: TimeSpan.FromMinutes(10));
    }

    public async Task ForEachAsync<T>(IReadOnlyCollection<T> items, int maxConcurrency,
        Func<T, CancellationToken, ValueTask> action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(action);
        if (maxConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = maxConcurrency
        }, (item, cancellation) => action(item, cancellation));
    }

    private async Task<T> ExecuteAsync<T>(Uri original, Func<Uri, CancellationToken, Task<T>> action,
        CancellationToken token, Uri? mirror = null, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        VersionResolver.RequireHttps(original);
        IReadOnlyList<HmclDownloadCandidate> candidates = provider.GetCandidates(original, mirror);
        Guid requestId = Guid.NewGuid();
        bool mirrorFailed = false;
        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            for (int attempt = 0; attempt <= candidate.RetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (timeout is { } limit) deadline.CancelAfter(limit);
                string context = $"Request={requestId:N}; Source={candidate.SourceName}; Uri={DiagnosticText.Address(candidate.Uri)}; Attempt={attempt + 1}/{candidate.RetryCount + 1}";
                var timer = Stopwatch.StartNew();
                diagnostics?.Write("HTTP", "Begin " + context);
                try
                {
                    T result = await action(candidate.Uri, deadline.Token);
                    diagnostics?.Write("HTTP", $"Success {context}; ElapsedMs={timer.ElapsedMilliseconds}");
                    if (mirrorFailed && !candidate.IsMirror) provider.LogFallbackSuccess(original);
                    return result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    diagnostics?.Write("HTTP", $"Cancelled {context}; ElapsedMs={timer.ElapsedMilliseconds}");
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or
                    System.Xml.XmlException or VersionResolutionException or ArgumentException or InvalidOperationException or
                    TimeoutException or OperationCanceledException)
                {
                    Exception failure = ex is OperationCanceledException && !token.IsCancellationRequested
                        ? new TimeoutException($"下载请求超时：{DiagnosticText.Address(candidate.Uri)}", ex) : ex;
                    lastError = failure;
                    bool shouldRetry = provider.ShouldRetry(candidate, failure);
                    diagnostics?.Write("HTTP", $"Failed {context}; ElapsedMs={timer.ElapsedMilliseconds}", failure);

                    bool retry = attempt < candidate.RetryCount && shouldRetry;
                    if (retry)
                    {
                        try { await Task.Delay(RetryDelay, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        continue;
                    }

                    if (candidate.IsMirror)
                    {
                        mirrorFailed = true;
                        provider.LogCandidateFailure(candidate, failure);
                    }
                    break;
                }
            }
        }

        if (lastError is TimeoutException timeoutError) throw timeoutError;
        if (lastError is OperationCanceledException canceled && !token.IsCancellationRequested)
            throw new TimeoutException($"下载请求超时：{DiagnosticText.Address(original)}", canceled);
        throw lastError ?? new HttpRequestException("没有可用的下载地址：" + DiagnosticText.Address(original));
    }

    private async Task<byte[]> ReadBytesAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        Uri final = response.RequestMessage?.RequestUri ?? uri;
        VersionResolver.RequireHttps(final);
        diagnostics?.Write("HTTP", $"Response status={(int)response.StatusCode}; requested={DiagnosticText.Address(uri)}; final={DiagnosticText.Address(final)}");
        EnsureSuccess(response, uri);
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("响应超过大小上限。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("响应超过大小上限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void EnsureSuccess(HttpResponseMessage response, Uri requested)
    {
        if (response.IsSuccessStatusCode) return;
        Uri final = response.RequestMessage?.RequestUri ?? requested;
        string address = DiagnosticText.Address(final);
        var error = new HttpRequestException($"HTTP {(int)response.StatusCode} ({response.StatusCode})：{address}", null, response.StatusCode);
        error.Data["RequestUri"] = DiagnosticText.Address(requested);
        error.Data["ResponseUri"] = address;
        throw error;
    }
}
