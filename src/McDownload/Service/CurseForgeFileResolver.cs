using McDownload.i18n;
using System.Net;
using System.Text.Json.Nodes;
using McDownload.Model;

namespace McDownload.Service;

public sealed class CurseForgeFileResolver : ICurseForgeFileResolver, IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly TimeSpan _requestTimeout;

    public CurseForgeFileResolver(Func<string?>? apiKey = null, HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        _apiKey = apiKey ?? (() => null);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<CurseForgeFileResolution> ResolveAsync(ModpackFile file, CancellationToken token)
    {
        var reference = file.CurseForge ?? throw new ArgumentException(Text.Get("CurseForgeFileResolver", "FileReferenceMissing"), nameof(file));
        if (reference.ProjectId <= 0 || reference.FileId <= 0) throw new InvalidDataException(Text.Get("CurseForgeFileResolver", "InvalidFileReference"));
        Uri? website = null;
        CurseForgeFileResolution Missing(string reason) => new(null, new(file.Key, reason, website));
        string? key = _apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(Text.Get("CurseForgeFileResolver", "ApiKeyMissing"));
        key = CurseForgeCredentialStore.Validate(key);
        var data = await ReadAsync($"mods/{reference.ProjectId}/files/{reference.FileId}", key, token);
        if (data is null) return Missing(Text.Get("CurseForgeFileResolver", "FileUnavailable"));
        if (data["id"]?.GetValue<long>() != reference.FileId || data["modId"]?.GetValue<long>() != reference.ProjectId)
            throw new InvalidDataException(Text.Get("CurseForgeFileResolver", "FileIdMismatch"));
        string name = ModpackArchiveReader.Text(data, "fileName"); InstanceManager.ValidateName(name);
        var project = await ReadAsync($"mods/{reference.ProjectId}", key, token);
        if (project is null) return Missing(Text.Get("CurseForgeFileResolver", "ProjectUnavailable"));
        if (project["id"]?.GetValue<long>() != reference.ProjectId || project["gameId"]?.GetValue<int>() != 432)
            throw new InvalidDataException(Text.Get("CurseForgeFileResolver", "ProjectMismatch"));
        if (project["links"]?["websiteUrl"]?.GetValue<string>() is { } link &&
            TryGetFileWebsite(link, reference.FileId, out var fileUri))
            website = fileUri;
        string? folder = project["classId"]?.GetValue<int>() switch
        {
            6 => "mods", 12 => "resourcepacks", 6552 => "shaderpacks", 6945 => "datapacks", _ => null
        };
        if (folder is null) return Missing(Text.Get("CurseForgeFileResolver", "UnsupportedCategory"));
        string? sha1 = null, md5 = null;
        foreach (var hash in data["hashes"] as JsonArray ?? new())
        {
            if (hash?["algo"]?.GetValue<int>() == 1) sha1 = ModpackArchiveReader.Text(hash, "value");
            if (hash?["algo"]?.GetValue<int>() == 2) md5 = ModpackArchiveReader.Text(hash, "value");
        }
        if (sha1 is null && md5 is null) return Missing(Text.Get("CurseForgeFileResolver", "HashMissing"));
        var downloads = new List<Uri>();
        if (data["downloadUrl"]?.GetValue<string>() is { Length: > 0 } address && project["allowModDistribution"]?.GetValue<bool>() != false)
            downloads.Add(new Uri(address, UriKind.Absolute));
        var resolved = file with { Path = folder + "/" + name, Size = data["fileLength"]?.GetValue<long>(),
            Hashes = new(sha1, Md5: md5), Downloads = downloads };
        ModpackSafety.ValidateFile(resolved);
        return new(resolved, downloads.Count == 0 ? new(file.Key, Text.Get("CurseForgeFileResolver", "ManualDownloadRequired"), website) : null);
    }

    public static bool TryGetFileWebsite(string link, long fileId, out Uri? website)
    {
        website = null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("www.curseforge.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 3 || parts[0] != "minecraft" ||
            parts[1] is not ("mc-mods" or "texture-packs" or "shaders" or "data-packs") ||
            parts[2].Length == 0 || parts[2].All(char.IsAsciiDigit) ||
            parts[2].Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-')) return false;
        website = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/files/" + fileId);
        return true;
    }

    private async Task<JsonObject?> ReadAsync(string path, string key, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_requestTimeout);
        try { return await ReadCoreAsync(path, key, deadline.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException(Text.Get("CurseForgeFileResolver", "RequestTimeout"), error); }
    }

    private async Task<JsonObject?> ReadCoreAsync(string path, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.curseforge.com/v1/" + path);
        request.Headers.Add("x-api-key", key);
        HttpResponseMessage received;
        try { received = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new HttpRequestException(Text.Get("CurseForgeFileResolver", "ConnectionFailed")); }
        using var response = received;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new HttpRequestException(Text.Format("CurseForgeFileResolver", "AuthenticationFailed", (int)response.StatusCode), null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Text.Format("CurseForgeFileResolver", "HttpFailed", (int)response.StatusCode), null, response.StatusCode);
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException(Text.Get("CurseForgeFileResolver", "ResponseTooLarge"));
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > 4 * 1024 * 1024) throw new InvalidDataException(Text.Get("CurseForgeFileResolver", "ResponseTooLarge"));
            output.Write(buffer, 0, count);
        }
        return JsonNode.Parse(output.ToArray())?["data"] as JsonObject ?? throw new InvalidDataException(Text.Get("CurseForgeFileResolver", "ResponseMissing"));
    }

    public void Dispose() => _http.Dispose();
}
