using System.Collections.Concurrent;
using System.Security.Cryptography;
using McDownload.i18n;

namespace McDownload.Service;

internal sealed record GameDownloadFile(Uri Uri, string Path, string Sha1, long? Size);

internal sealed class GameFileDownloader(HmclFetchTask fetcher,
    int maxConcurrency = 8,
    Func<GameDownloadFile, CancellationToken, ValueTask<bool>>? verify = null)
{
    private readonly int _maxConcurrency = maxConcurrency is >= 1 and <= 64
        ? maxConcurrency : throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

    public async Task DownloadAsync(IReadOnlyCollection<GameDownloadFile> files, CancellationToken token,
        Action<int, int>? completed = null, Action<int, int>? verified = null)
    {
        token.ThrowIfCancellationRequested();
        var unique = new Dictionary<string, GameDownloadFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            VersionResolver.RequireHttps(file.Uri);
            VersionResolver.ValidateSha1(file.Sha1);
            if (file.Size is < 0 or > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException(Text.Get("HmclFetchTask", "InvalidDownloadSize"));
            string path = InstanceManager.Normal(file.Path);
            if (unique.TryGetValue(path, out var previous) &&
                (!previous.Sha1.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase) || previous.Size != file.Size))
                throw new InvalidDataException(Text.Get("GameDownloadService", "ConflictingFile"));
            unique.TryAdd(path, file with { Path = path });
        }

        var missing = new ConcurrentBag<GameDownloadFile>();
        int checkedCount = 0, completedCount = 0;
        await fetcher.ForEachAsync(unique.Values, _maxConcurrency, async (file, cancellation) =>
        {
            if (await (verify ?? VerifyAsync)(file, cancellation)) Interlocked.Increment(ref completedCount);
            else missing.Add(file);
            int count = Interlocked.Increment(ref checkedCount);
            verified?.Invoke(count, unique.Count);
        }, token);

        completed?.Invoke(completedCount, unique.Count);
        await fetcher.ForEachAsync(missing.ToArray(), _maxConcurrency, async (file, cancellation) =>
        {
            await fetcher.DownloadAsync(file.Uri, file.Path, file.Sha1, file.Size, cancellation);
            int count = Interlocked.Increment(ref completedCount);
            completed?.Invoke(count, unique.Count);
        }, token);
    }

    internal static async ValueTask<bool> VerifyAsync(GameDownloadFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstanceManager.CheckNoReparse(file.Path);
        if (!File.Exists(InstanceManager.Extended(file.Path))) return false;
        await using var input = new FileStream(InstanceManager.Extended(file.Path), FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Size is long size && input.Length != size) return false;
        return Convert.ToHexString(await SHA1.HashDataAsync(input, token))
            .Equals(file.Sha1, StringComparison.OrdinalIgnoreCase);
    }
}
