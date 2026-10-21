using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using McDownload.Model;
using McDownload.Service;
using McDownload.ViewModel;

internal static class DiagnosticChecks
{
    private sealed class SslHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var ssl = new AuthenticationException("Authentication failed because the remote party sent a TLS alert: ProtocolVersion.");
            throw new HttpRequestException(HttpRequestError.SecureConnectionError,
                "The SSL connection could not be established, see inner exception.", ssl);
        }
    }

    private static string JoinLogs(string directory) => string.Join(Environment.NewLine,
        Directory.GetFiles(directory, "*.log").OrderBy(x => x).Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }));

    public static async Task RunAsync(WorkspaceViewModel state, string root, Action<bool, string> check)
    {
        const string signature = "SIGNATURE_SENTINEL";
        const string token = "TOKEN_SENTINEL";
        const string bearer = "BEARER_SENTINEL";
        const string password = "PASSWORD_SENTINEL";
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string raw = "GET https://bmclapi2.bangbang93.com/path/file.jar?sign=" + signature + "&token=" + token +
            " HTTP/1.1\nAuthorization: Bearer " + bearer + "\npath=" + Path.Combine(local, "SCI", "settings.json") +
            "\npassword=" + password + "\nrelative=/file.jar?sign=" + signature;
        string redacted = DiagnosticText.Redact(raw);
        check(redacted.Contains("https://bmclapi2.bangbang93.com/path/file.jar") && redacted.Contains("%LOCALAPPDATA%"),
            "diagnostics retain useful origin and replace user directory");
        check(!redacted.Contains(signature) && !redacted.Contains(token) && !redacted.Contains(bearer) &&
            !redacted.Contains(password), "diagnostics redact query tokens, bearer tokens and passwords");
        check(!redacted.Contains("relative=/file.jar?sign=" + signature), "relative signed URL query is redacted");

        var ssl = new AuthenticationException("Authentication failed because the remote party sent a TLS alert.");
        var requestError = new HttpRequestException(HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established, see inner exception.", ssl);
        requestError.Data["RequestUri"] = "https://bmclapi2.bangbang93.com/file.jar?sign=" + signature;
        string details = DiagnosticText.ExceptionDetails(requestError);
        check(details.Contains("SecureConnectionError") && details.Contains("AuthenticationException") &&
            details.Contains("HResult=") && details.Contains("RequestUri=https://bmclapi2.bangbang93.com/file.jar"),
            "diagnostics expose HTTP and TLS exception chain without query");
        check(!details.Contains(signature) && !details.Contains("?sign="), "exception details omit signed query");
        check(DiagnosticText.Summary(requestError).Contains("SSL connection could not be established"),
            "UI summary explains SSL failure");
        check(DiagnosticText.Address(new Uri("https://user:pass@example.com/a?sign=hidden#fragment")) == "https://example.com/a",
            "address formatter strips credentials, query and fragment");

        string logDirectory = Path.Combine(root, "diagnostics");
        using (var log = new DiagnosticLog(logDirectory, 4096))
        {
            log.Write("ERROR", "TLS failure at https://bmclapi2.bangbang93.com/file?sign=" + signature, requestError);
            log.Write("INFO", new string('x', 5000));
            check(log.FilePath is not null && File.Exists(log.FilePath), "diagnostic log file is persisted");
        }
        string[] parts = Directory.GetFiles(logDirectory, "*.log");
        check(parts.Length >= 2, "diagnostic log rotates before growing without bound");
        string all = JoinLogs(logDirectory);
        check(all.Contains("[ERROR]") && all.Contains("AuthenticationException") && all.Contains("SecureConnectionError"),
            "persisted log includes exception chain and severity");
        check(!all.Contains(signature) && !all.Contains(local), "persisted log contains no signature or raw user path");

        string blocked = Path.Combine(root, "diagnostics-blocked");
        await File.WriteAllTextAsync(blocked, "not a directory");
        using (var broken = new DiagnosticLog(Path.Combine(blocked, "logs")))
        {
            check(broken.Error is not null, "unwritable log directory reports an error");
            broken.Write("INFO", "must not throw");
            check(broken.FilePath is null, "unwritable log directory does not claim a file");
        }

        var handler = new SslHandler();
        using var http = new HttpClient(handler);
        string sourceDirectory = Path.Combine(root, "source-diagnostics");
        using (var log = new DiagnosticLog(sourceDirectory))
        {
            var source = new HmclFetchTask(http, new HmclDownloadProvider(DownloadSource.Bmclapi), log);
            bool failed = false;
            try
            {
                await source.ReadAsync(new Uri("https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.20/forge-1.21.1-52.1.20-installer.jar.sha1?sign=" + signature),
                    4096, bytes => bytes, default);
            }
            catch (HttpRequestException) { failed = true; }
            check(failed && handler.Requests == 2, "TLS failure tries mirror then official");
        }
        string sourceLog = JoinLogs(sourceDirectory);
        check(sourceLog.Contains("Source=BMCLAPI") && sourceLog.Contains("Source=Official") &&
            sourceLog.Contains("SecureConnectionError") && sourceLog.Contains("AuthenticationException"),
            "source diagnostics identify both attempts and TLS cause");
        check(sourceLog.Contains("ElapsedMs=") && sourceLog.Contains("forge-1.21.1-52.1.20-installer.jar.sha1"),
            "source diagnostics include timing and requested file without query");
        check(!sourceLog.Contains(signature), "source diagnostics contain no signature");

        bool completed = await state.RunAsync("SSL 失败回归", _ =>
            Task.FromException(new HttpRequestException(HttpRequestError.SecureConnectionError,
                "The SSL connection could not be established, see inner exception.", ssl)));
        check(!completed && state.Status == "任务未完成" && state.Error?.Contains("SSL") == true &&
            state.Logs.Any(x => x.Contains("SSL 失败回归")), "operation failure is logged with task context");
        check(state.DiagnosticLogPath is not null && File.Exists(state.DiagnosticLogPath),
            "workspace exposes the active diagnostic log path");
        check(state.DiagnosticDirectory == Path.GetDirectoryName(state.DiagnosticLogPath),
            "workspace exposes the directory containing the active log");
    }
}
