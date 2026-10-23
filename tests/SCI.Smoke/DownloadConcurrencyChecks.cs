using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using McDownload.Model;
using McDownload.Service;

internal static class DownloadConcurrencyChecks
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = null!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Respond(request, token);
    }

    private sealed class BlockingBody(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) return await base.ReadAsync(buffer[..Math.Min(4, buffer.Length)], cancellationToken);
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "download-concurrency");
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.UTF8.GetBytes("parallel-game-file");
        string hash = Convert.ToHexString(SHA1.HashData(bytes));
        var files = Enumerable.Range(0, 12).Select(i => new GameDownloadFile(
            new Uri($"https://libraries.minecraft.net/concurrency/{i}.jar"), Path.Combine(directory, i + ".jar"), hash, bytes.Length)).ToArray();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var fetcher = new HmclFetchTask(http, new(DownloadSource.Official));
        foreach (int limit in new[] { 1, 3 })
        {
            int verifying = 0, downloading = 0, requests = 0;
            var verificationFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseVerification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloadsFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDownloads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var counts = new ConcurrentDictionary<string, int>();
            handler.Respond = async (request, token) =>
            {
                Interlocked.Increment(ref requests);
                counts.AddOrUpdate(request.RequestUri!.AbsolutePath, 1, (_, n) => n + 1);
                int active = Interlocked.Increment(ref downloading);
                try
                {
                    if (active == limit) downloadsFull.TrySetResult();
                    if (active > limit) throw new Exception("download concurrency exceeds limit");
                    await releaseDownloads.Task.WaitAsync(token);
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                }
                finally { Interlocked.Decrement(ref downloading); }
            };
            var downloader = new GameFileDownloader(fetcher, limit, async (_, token) =>
            {
                int active = Interlocked.Increment(ref verifying);
                try
                {
                    if (active == limit) verificationFull.TrySetResult();
                    if (active > limit) throw new Exception("verification concurrency exceeds limit");
                    await releaseVerification.Task.WaitAsync(token);
                    return false;
                }
                finally { Interlocked.Decrement(ref verifying); }
            });
            var pending = downloader.DownloadAsync(files.Concat(new[] { files[0] }).ToArray(), default);
            try
            {
                await verificationFull.Task.WaitAsync(TimeSpan.FromSeconds(10));
                check(verifying == limit && requests == 0, $"verification uses configured concurrency {limit} before downloads");
                releaseVerification.TrySetResult();
                await downloadsFull.Task.WaitAsync(TimeSpan.FromSeconds(10));
                check(downloading == limit && verifying == 0, $"downloads use configured concurrency {limit}");
            }
            finally
            {
                releaseVerification.TrySetResult(); releaseDownloads.TrySetResult();
                await pending;
            }
            check(requests == files.Length && counts.Values.All(n => n == 1), "duplicate targets are downloaded once");
        }

        int cacheRequests = 0;
        handler.Respond = (_, _) => { Interlocked.Increment(ref cacheRequests); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); };
        var real = new GameFileDownloader(fetcher, 3);
        await real.DownloadAsync(files, default);
        check(cacheRequests == 0, "verified cache avoids all downloads");
        await File.WriteAllBytesAsync(files[0].Path, Encoding.UTF8.GetBytes(new string('x', bytes.Length)));
        await File.WriteAllBytesAsync(files[1].Path, new byte[1]);
        await real.DownloadAsync(files, default);
        check(cacheRequests == 2 && (await File.ReadAllBytesAsync(files[0].Path)).SequenceEqual(bytes) &&
            (await File.ReadAllBytesAsync(files[1].Path)).SequenceEqual(bytes), "parallel verification repairs digest and size mismatches");
        bool conflict = false;
        try { await real.DownloadAsync(new[] { files[0], files[0] with { Sha1 = new string('0', 40) } }, default); }
        catch (InvalidDataException) { conflict = true; }
        check(conflict, "conflicting duplicate targets fail before writing");

        var aliases = new JsonObject
        {
            ["sound/a"] = new JsonObject { ["hash"] = hash },
            ["SOUND\\a"] = new JsonObject { ["hash"] = hash.ToLowerInvariant() }
        };
        var copies = GameDownloadService.PlanAssetCopies(aliases.ToArray(), Path.Combine(directory, "assets"), directory, "fixture", true, true);
        check(copies.Count == 2 && copies.Select(x => x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "resource mapping aliases produce one copy per normalized target");
        aliases["SOUND\\a"]!["hash"] = new string('0', 40);
        conflict = false;
        try { GameDownloadService.PlanAssetCopies(aliases.ToArray(), Path.Combine(directory, "assets"), directory, "fixture", true, true); }
        catch (InvalidDataException) { conflict = true; }
        check(conflict && !Directory.Exists(Path.Combine(directory, "assets")), "conflicting mapping aliases fail before any copy writes");

        using (var cancel = new CancellationTokenSource())
        {
            var bodies = new ConcurrentBag<BlockingBody>();
            handler.Respond = (_, _) =>
            {
                var body = new BlockingBody(bytes); bodies.Add(body);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            };
            var writingFiles = files.Take(3).ToArray();
            var pending = new GameFileDownloader(fetcher, 3, (_, _) => ValueTask.FromResult(false))
                .DownloadAsync(writingFiles, cancel.Token);
            bool canceled = false;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (bodies.Count < 3) await Task.Delay(10, deadline.Token);
                await Task.WhenAll(bodies.Select(x => x.Waiting.Task)).WaitAsync(deadline.Token);
                check(Directory.EnumerateFiles(directory, ".tmp.*").Count() == 3,
                    "body cancellation fixture reaches three partial temporary writes");
            }
            finally
            {
                cancel.Cancel();
                try { await pending; } catch (OperationCanceledException) { canceled = true; }
            }
            check(canceled && bodies.All(x => x.Closed) && !Directory.EnumerateFiles(directory, ".tmp.*").Any(),
                "cancellation during body writes closes every stream and removes temporary files");
            foreach (var file in writingFiles)
                check((await File.ReadAllBytesAsync(file.Path)).SequenceEqual(bytes), "body cancellation preserves existing cache: " + file.Path);
        }

        foreach (bool cancelVerification in new[] { true, false })
        {
            using var cancel = new CancellationTokenSource();
            int active = 0, started = 0;
            var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Block(CancellationToken token)
            {
                Interlocked.Increment(ref active);
                if (Interlocked.Increment(ref started) == 3) full.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Interlocked.Decrement(ref active); }
            }
            handler.Respond = async (_, token) => { await Block(token); return new(HttpStatusCode.OK); };
            var downloader = new GameFileDownloader(fetcher, 3, async (_, token) =>
            {
                if (cancelVerification) await Block(token);
                return false;
            });
            var pending = downloader.DownloadAsync(files, cancel.Token);
            bool canceled = false;
            try { await full.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { cancel.Cancel(); }
            try { await pending; } catch (OperationCanceledException) { canceled = true; }
            check(canceled && active == 0 && started == 3, "cancellation drains all workers without scheduling more: " + cancelVerification);
            check(!Directory.EnumerateFiles(directory, ".tmp.*").Any(), "cancellation leaves no temporary files");
        }

        int failureActive = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = new GameFileDownloader(fetcher, 2, async (file, token) =>
        {
            if (Interlocked.Increment(ref failureActive) == 2) bothStarted.TrySetResult();
            try
            {
                await bothStarted.Task.WaitAsync(token);
                if (file == files[0]) throw new InvalidDataException("fixture verification failure");
                await Task.Delay(Timeout.Infinite, token);
                return false;
            }
            finally { Interlocked.Decrement(ref failureActive); }
        });
        bool failed = false;
        try { await failing.DownloadAsync(files, default).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidDataException) { failed = true; }
        check(failed && failureActive == 0, "verification failure cancels and joins sibling workers");
    }
}
