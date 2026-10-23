using System.Net;
using McDownload.Model;

namespace McDownload.Service;

public sealed class ModpackFileDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly DiagnosticLog? _diagnostics;
    private readonly TimeSpan _requestTimeout;

    public ModpackFileDownloader(HttpMessageHandler? handler = null, DiagnosticLog? diagnostics = null, TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(10);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _diagnostics = diagnostics;
    }

    public async Task<string> DownloadAsync(ModpackFile file, string target, CancellationToken token,
        Action<long>? progress = null)
    {
        ModpackSafety.ValidateFile(file);
        Exception? lastError = null;
        foreach (var original in file.Downloads)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(_requestTimeout);
                try
                {
                    string? digest = null;
                    using var response = await GetAsync(original, deadline.Token);
                    if (response.Content.Headers.ContentLength is long length && length != file.Size)
                        throw new DownloadIntegrityException("整合包下载响应大小不一致。");
                    await InstanceManager.AtomicWriteAsync(target, async (output, ct) =>
                    {
                        await using var input = await response.Content.ReadAsStreamAsync(ct);
                        digest = await ModpackSafety.CopyVerifiedAsync(input, output, file.Size!.Value, file.Hashes, ct, progress);
                    }, deadline.Token);
                    return digest!;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    lastError = error is OperationCanceledException ? new TimeoutException("整合包文件下载超时。", error) : error;
                    _diagnostics?.Write("HTTP", "整合包下载失败：" + DiagnosticText.Address(original), lastError);
                    bool retry = error is not (DownloadIntegrityException or InvalidDataException) &&
                        (error is not HttpRequestException request || request.StatusCode is null or HttpStatusCode.RequestTimeout ||
                            (int)request.StatusCode.Value >= 500);
                    if (!retry || attempt == 2) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), token);
                }
            }
        }
        throw lastError ?? new InvalidDataException("整合包文件没有可用下载地址。");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (int redirects = 0; ; redirects++)
        {
            ModpackSafety.RequireDownloadUri(uri);
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            Uri final = response.RequestMessage?.RequestUri ?? uri;
            try
            {
                ModpackSafety.RequireDownloadUri(final);
                _diagnostics?.Write("HTTP", $"整合包 status={(int)response.StatusCode}; uri={DiagnosticText.Address(uri)}");
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location)
                        throw new InvalidDataException("整合包下载重定向无效或过多。");
                    uri = new Uri(uri, location);
                    response.Dispose();
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"整合包下载 HTTP {(int)response.StatusCode}：{DiagnosticText.Address(uri)}", null, response.StatusCode);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    public void Dispose() => _http.Dispose();
}
