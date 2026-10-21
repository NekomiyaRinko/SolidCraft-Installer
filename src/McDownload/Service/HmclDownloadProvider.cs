/*
 * Minecraft download source selection adapted from HMCL.
 * Copyright (C) 2020-2026 huangyuhui and HMCL contributors.
 * This file is distributed under GNU GPL version 3 or later with the
 * additional terms in HMCL-ADDITIONAL-TERMS.md.
 * Modified for SCI (C# port), 2026-10-21.
 * Upstream: https://github.com/HMCL-dev/HMCL, commit acc9740fa9289ded38553b6a35045fd608c1ea99.
 */
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using McDownload.Model;

namespace McDownload.Service;

public sealed record HmclDownloadCandidate(Uri OriginalUri, Uri Uri, bool IsMirror, int RetryCount = 5)
{
    public string SourceName => IsMirror ? "BMCLAPI" : "Official";
}

public sealed class HmclDownloadProvider(DownloadSource source, Action<string>? log = null)
{
    private static readonly TimeSpan ForbiddenCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnavailableCooldown = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedMirrors = new(StringComparer.OrdinalIgnoreCase);

    public DownloadSource Source { get; } = source;

    public static Uri? MirrorUri(Uri original)
    {
        VersionResolver.RequireHttps(original);
        string path = original.PathAndQuery;
        string? mirrorPath = original.Host.ToLowerInvariant() switch
        {
            "piston-meta.mojang.com" or "launchermeta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com" => path,
            "resources.download.minecraft.net" => "/assets" + path,
            "libraries.minecraft.net" => "/libraries" + path,
            "maven.minecraftforge.net" or "maven.fabricmc.net" => "/maven" + path,
            "maven.neoforged.net" when path.StartsWith("/releases/", StringComparison.Ordinal) => "/maven/" + path[10..],
            "meta.fabricmc.net" => "/fabric-meta" + path,
            _ => null
        };
        return mirrorPath is null ? null : new Uri("https://bmclapi2.bangbang93.com" + mirrorPath);
    }

    public IReadOnlyList<HmclDownloadCandidate> GetCandidates(Uri original, Uri? mirror = null)
    {
        VersionResolver.RequireHttps(original);
        if (Source != DownloadSource.Bmclapi) return [new(original, original, false)];

        mirror ??= MirrorUri(original);
        if (mirror is null) return [new(original, original, false)];
        VersionResolver.RequireHttps(mirror);

        var candidates = new List<HmclDownloadCandidate>(2);
        if (!IsBlocked(_blockedOrigins, original.Authority) && !IsBlocked(_blockedMirrors, mirror.Authority))
            candidates.Add(new(original, mirror, true));
        candidates.Add(new(original, original, false));
        return candidates.AsReadOnly();
    }

    internal bool ShouldRetry(HmclDownloadCandidate candidate, Exception error)
    {
        if (candidate.IsMirror)
        {
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
                _blockedOrigins[candidate.OriginalUri.Authority] = DateTimeOffset.UtcNow + ForbiddenCooldown;
            else if (error is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                _blockedMirrors[candidate.Uri.Authority] = DateTimeOffset.UtcNow + UnavailableCooldown;
            else if (IsUnstableTransport(error))
                _blockedMirrors[candidate.Uri.Authority] = DateTimeOffset.UtcNow + UnavailableCooldown;
        }

        if (error is DownloadIntegrityException or InvalidDataException or System.Text.Json.JsonException or System.Xml.XmlException)
            return false;
        if (error is HttpRequestException { StatusCode: { } status })
            return status == HttpStatusCode.RequestTimeout || (int)status >= 500;
        if (error is HttpRequestException request && request.HttpRequestError == HttpRequestError.SecureConnectionError)
            return false;
        if (IsUnstableTransport(error)) return false;
        return error is HttpRequestException or IOException or TimeoutException or OperationCanceledException;
    }

    internal void LogCandidateFailure(HmclDownloadCandidate candidate, Exception error)
    {
        string address = DiagnosticText.Address(candidate.Uri);
        if (candidate.IsMirror && error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
            log?.Invoke(DiagnosticText.Redact($"BMCLAPI 节点拒绝访问（403），该官方来源 5 分钟内使用官方源：{ResponseAddress(error, address)}"));
        else if (candidate.IsMirror)
            log?.Invoke(DiagnosticText.Redact($"BMCLAPI 请求失败，回退官方源：{ResponseAddress(error, address)} {DiagnosticText.Summary(error)}"));
    }

    internal void LogFallbackSuccess(Uri original) =>
        log?.Invoke("官方源回退成功：" + DiagnosticText.Address(original));

    private static bool IsBlocked(ConcurrentDictionary<string, DateTimeOffset> blocked, string key)
    {
        if (!blocked.TryGetValue(key, out var until)) return false;
        if (DateTimeOffset.UtcNow < until) return true;
        blocked.TryRemove(key, out _);
        return false;
    }

    private static bool IsUnstableTransport(Exception error) => Enumerate(error).Any(item =>
        item is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } ||
        item is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted });

    private static string ResponseAddress(Exception error, string fallback) =>
        error.Data["ResponseUri"] as string ?? fallback;

    private static IEnumerable<Exception> Enumerate(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            yield return current;
    }
}
