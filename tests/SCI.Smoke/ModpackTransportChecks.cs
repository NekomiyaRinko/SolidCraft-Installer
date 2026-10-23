using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using McDownload.Model;
using McDownload.Service;

internal static class ModpackTransportChecks
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("transport-fixture");
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(-1, token); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Timeout { get; set; }
        public bool Cancel { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Cancel) return Task.Delay(TimeoutInfinite, token).ContinueWith<HttpResponseMessage>(_ =>
                throw new OperationCanceledException(token), token);
            if (Timeout) throw new TaskCanceledException("fixture timeout");
            if (request.RequestUri!.AbsolutePath == "/forbidden") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }
        private const int TimeoutInfinite = -1;
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var handler = new Handler();
        using var downloader = new ModpackFileDownloader(handler);
        string target = Path.Combine(root, "modpack-transport.bin");
        var file = new ModpackFile("file", "mods/transport.jar", Payload.Length,
            new(Convert.ToHexString(SHA1.HashData(Payload)), Convert.ToHexString(SHA512.HashData(Payload))),
            new[] { new Uri("https://cdn.modrinth.com/fixture") });
        bool rejected = false;
        try { await downloader.DownloadAsync(file with { Hashes = file.Hashes with { Sha512 = new string('0', 128) } }, target, default); }
        catch (DownloadIntegrityException) { rejected = true; }
        check(rejected && !File.Exists(target), "correct SHA-1 cannot bypass incorrect SHA-512");
        var md5 = file with { Hashes = new(Md5: Convert.ToHexString(MD5.HashData(Payload))) };
        string digest = await downloader.DownloadAsync(md5, target, default);
        check(digest == Convert.ToHexString(SHA256.HashData(Payload)), "MD5-only CurseForge metadata verifies and records SHA-256");
        int before = handler.Requests;
        await downloader.DownloadAsync(file with { Downloads = new[] { new Uri("https://cdn.modrinth.com/forbidden"), file.Downloads[0] } }, target, default);
        check(handler.Requests == before + 2, "403 is not retried and next declared candidate is used");
        handler.Timeout = true;
        bool timedOut = false;
        try { await downloader.DownloadAsync(file, target, default); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && (await File.ReadAllBytesAsync(target)).SequenceEqual(Payload), "transport timeout is not user cancellation and preserves target");
        handler.Timeout = false; handler.Cancel = true;
        using var cancellation = new CancellationTokenSource();
        var downloading = downloader.DownloadAsync(file, target, cancellation.Token);
        cancellation.Cancel();
        bool canceled = false;
        try { await downloading; }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && (await File.ReadAllBytesAsync(target)).SequenceEqual(Payload), "active download cancellation preserves previous target");

        using var stalled = new ModpackFileDownloader(new StalledHandler(), requestTimeout: TimeSpan.FromMilliseconds(30));
        timedOut = false;
        try { await stalled.DownloadAsync(file, target, default); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && (await File.ReadAllBytesAsync(target)).SequenceEqual(Payload), "deadline covers stalled download response body and preserves target");
        using var api = new CurseForgeFileResolver(() => "fixture-key", new StalledHandler(), TimeSpan.FromMilliseconds(30));
        timedOut = false;
        try { await api.ResolveAsync(file with { CurseForge = new(1, 2) }, default); }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut, "deadline covers stalled CurseForge API response body");
    }
}
