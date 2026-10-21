using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using McDownload.Model;
using McDownload.Service;
using McDownload.ViewModel;

internal static class ForbiddenChecks
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        public bool OfficialForbidden { get; set; }
        public bool Timeout { get; set; }
        public HttpStatusCode MirrorStatus { get; set; } = HttpStatusCode.Forbidden;
        public byte[] Content { get; } = Encoding.UTF8.GetBytes("forge-installer-fixture");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            if (Timeout) throw new TaskCanceledException("fixture timeout", new TimeoutException());
            if (request.RequestUri!.Host == "bmclapi2.bangbang93.com")
                return Task.FromResult(new HttpResponseMessage(MirrorStatus)
                {
                    RequestMessage = new(HttpMethod.Get, "https://mirrors.cernet.edu.cn/bmclapi/forge-installer.jar?sign=fixture-signature"),
                    Content = new StringContent(MirrorStatus.ToString())
                });
            if (OfficialForbidden) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });
            byte[] body = request.RequestUri.AbsolutePath.EndsWith(".sha1")
                ? Encoding.ASCII.GetBytes(Convert.ToHexString(SHA1.HashData(Content))) : Content;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) });
        }
    }

    public static async Task RunAsync(WorkspaceViewModel state, string root, Action<bool, string> check)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var logs = new List<string>();
        var source = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Bmclapi, logs.Add));
        var installer = new Uri("https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.20/forge-1.21.1-52.1.20-installer.jar");
        var checksum = new Uri(installer.AbsoluteUri + ".sha1");
        string sha1 = await source.ReadAsync(checksum, 4096, Encoding.ASCII.GetString, default);
        check(handler.Requests.Count == 2 && handler.Requests[0].Host == "bmclapi2.bangbang93.com" && handler.Requests[1] == checksum,
            "Forge 403 falls back immediately to original checksum URL");
        check(logs.Count == 2 && logs[0].Contains("403") && logs[0].Contains("mirrors.cernet.edu.cn") && logs[1].Contains("回退成功"), "403 log identifies failing CDN and fallback success");
        check(!logs[0].Contains("fixture-signature") && !logs[0].Contains("?"), "403 diagnostics omit signed query");
        string file = Path.Combine(root, "forge-403-fixture.jar");
        await source.DownloadAsync(installer, file, sha1, handler.Content.Length, default);
        check(handler.Requests.Count == 3 && handler.Requests[^1] == installer, "same-origin installer skips mirror after checksum 403");
        check((await File.ReadAllBytesAsync(file)).SequenceEqual(handler.Content), "403 fallback preserves installer integrity check");
        int before = handler.Requests.Count;
        await source.ReadAsync(new Uri("https://libraries.minecraft.net/a.jar"), 4096, bytes => bytes, default);
        check(handler.Requests.Count == before + 2 && handler.Requests[before].Host == "bmclapi2.bangbang93.com",
            "Forge cooldown does not disable unrelated mirror origins");
        before = handler.Requests.Count;
        await source.ReadAsync(checksum, 4096, bytes => bytes, default);
        check(handler.Requests.Count == before + 1 && handler.Requests[^1] == checksum, "403 cooldown retained across requests");

        handler.MirrorStatus = HttpStatusCode.TooManyRequests;
        var rateLimited = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Bmclapi));
        before = handler.Requests.Count;
        await rateLimited.ReadAsync(checksum, 4096, Encoding.ASCII.GetString, default);
        check(handler.Requests.Count == before + 2 && handler.Requests[before].Host == "bmclapi2.bangbang93.com" &&
            handler.Requests[before + 1] == checksum, "429 skips retries and immediately falls back");
        before = handler.Requests.Count;
        await rateLimited.ReadAsync(checksum, 4096, Encoding.ASCII.GetString, default);
        check(handler.Requests.Count == before + 1 && handler.Requests[^1] == checksum,
            "429 temporarily suppresses the failed mirror node");
        handler.MirrorStatus = HttpStatusCode.Forbidden;

        handler.OfficialForbidden = true;
        HttpRequestException? forbidden = null;
        try { await source.DownloadAsync(installer, file, sha1, handler.Content.Length, default); }
        catch (HttpRequestException ex) { forbidden = ex; }
        check(forbidden?.StatusCode == HttpStatusCode.Forbidden && forbidden.Message.Contains("maven.minecraftforge.net"),
            "official 403 remains a real error with origin details");
        check((await File.ReadAllBytesAsync(file)).SequenceEqual(handler.Content), "failed requests leave existing installer untouched");

        handler.OfficialForbidden = false; handler.Timeout = true;
        bool timeout = false;
        try { await source.ReadAsync(checksum, 4096, bytes => bytes, default); }
        catch (TimeoutException ex) { timeout = ex.Message.Contains("maven.minecraftforge.net"); }
        check(timeout, "transport timeout is not propagated as user cancellation");
        bool complete = await state.RunAsync("非用户取消回归", _ => Task.FromException(new TaskCanceledException("fixture timeout")));
        check(!complete && state.Status == "任务未完成" && state.Error?.Contains("超时") == true,
            "unsignaled cancellation is not mislabeled as user cancellation");
        complete = await state.RunAsync("网络超时回归", _ => Task.FromException(new TimeoutException("fixture network timeout")));
        check(!complete && state.Status == "任务未完成" && state.Error == "fixture network timeout", "timeout error remains visible");
        var run = state.RunAsync("主动取消回归", context => Task.Delay(Timeout.Infinite, context.CancellationToken));
        state.CancelCommand.Execute(null);
        check(!await run && state.Status == "任务已取消" && state.Error is null, "manual cancellation still works");
        check(state.Logs.Any(x => x.Contains("已请求取消当前任务")), "manual cancellation request is logged");
    }

    public static async Task<int> OnlineAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var source = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Bmclapi, Console.WriteLine));
            var uri = new Uri("https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.20/forge-1.21.1-52.1.20-installer.jar.sha1");
            string checksum = await source.ReadAsync(uri, 4096, Encoding.ASCII.GetString, default);
            if (!checksum.Trim().Equals("e8dad3b52d88cc33badf3597b9f5a502e53e89e4", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unexpected Forge installer checksum.");
            Console.WriteLine("PASS Forge 1.21.1 / 52.1.20 checksum; no installer downloaded or executed.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
