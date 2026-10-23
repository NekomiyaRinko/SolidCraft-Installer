using McDownload.i18n;
using System.Security.Cryptography;
using McDownload.Model;

namespace McDownload.Service;

internal static class ModpackSafety
{
    internal const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaximumPackBytes = 20L * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 100000;

    internal static string RelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\\') || Path.IsPathRooted(value))
            throw new InvalidDataException(Text.Get("ModpackSafety", "InvalidRelativePath"));
        foreach (string part in value.Split('/')) InstanceManager.ValidateName(part);
        return value;
    }

    internal static void ValidateTarget(string path, string? directoryName = null)
    {
        RelativePath(path);
        string root = path.Split('/')[0];
        if (root.Equals(".launcher", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("instance.json", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("versions", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("libraries", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("assets", StringComparison.OrdinalIgnoreCase) ||
            (directoryName is not null && (root.Equals(directoryName + ".json", StringComparison.OrdinalIgnoreCase) ||
                root.Equals(directoryName + ".jar", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(Text.Format("ModpackSafety", "ManagedFileConflict", path));
    }

    internal static void ValidateHashes(ModpackHashes hashes, bool required = true)
    {
        foreach (var (hash, length) in new[] { (hashes.Sha1, 40), (hashes.Sha512, 128), (hashes.Md5, 32), (hashes.Sha256, 64) })
            if (hash is not null && (hash.Length != length || !hash.All(Uri.IsHexDigit)))
                throw new InvalidDataException(Text.Get("ModpackSafety", "InvalidFileHash"));
        if (required && hashes is { Sha1: null, Sha512: null, Md5: null, Sha256: null })
            throw new InvalidDataException(Text.Get("ModpackSafety", "FileHashMissing"));
    }

    internal static void ValidateFile(ModpackFile file)
    {
        if (file.Path is null) throw new InvalidDataException(Text.Get("ModpackSafety", "TargetPathMissing"));
        ValidateTarget(file.Path);
        if (file.Size is null or < 0 or > MaximumFileBytes) throw new InvalidDataException(Text.Get("ModpackSafety", "InvalidFileSize"));
        ValidateHashes(file.Hashes);
        foreach (var uri in file.Downloads) RequireDownloadUri(uri);
    }

    internal static void RequireDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0 || !AllowedHost(uri.IdnHost))
            throw new InvalidDataException(Text.Get("ModpackSafety", "UnsupportedDownloadAddress"));
    }

    private static bool AllowedHost(string host) => host.ToLowerInvariant() is
        "cdn.modrinth.com" or "github.com" or "raw.githubusercontent.com" or "objects.githubusercontent.com" or
        "release-assets.githubusercontent.com" or "gitlab.com" or "edge.forgecdn.net" or "media.forgecdn.net" or
        "mediafilez.forgecdn.net";

    internal static async Task<string> CopyVerifiedAsync(Stream input, Stream output, long size, ModpackHashes hashes,
        CancellationToken token, Action<long>? progress = null)
    {
        ValidateHashes(hashes, required: false);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long completed = 0;
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            completed += count;
            if (completed > size || completed > MaximumFileBytes) throw new DownloadIntegrityException(Text.Get("ModpackSafety", "FileTooLarge"));
            sha1.AppendData(buffer, 0, count); sha512.AppendData(buffer, 0, count);
            md5.AppendData(buffer, 0, count); sha256.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Invoke(completed);
        }
        if (completed != size) throw new DownloadIntegrityException(Text.Get("ModpackSafety", "FileSizeMismatch"));
        string result = Convert.ToHexString(sha256.GetHashAndReset());
        foreach (var (expected, actual) in new[]
        {
            (hashes.Sha1, Convert.ToHexString(sha1.GetHashAndReset())),
            (hashes.Sha512, Convert.ToHexString(sha512.GetHashAndReset())),
            (hashes.Md5, Convert.ToHexString(md5.GetHashAndReset())), (hashes.Sha256, result)
        })
            if (expected is not null && !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                throw new DownloadIntegrityException(Text.Get("ModpackSafety", "FileHashMismatch"));
        return result;
    }
}
