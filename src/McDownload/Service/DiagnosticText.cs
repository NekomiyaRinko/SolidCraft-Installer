using McDownload.i18n;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace McDownload.Service;

public static class DiagnosticText
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static string Address(Uri uri) => uri.IsAbsoluteUri
        ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped) : Text.Get("DiagnosticText", "RelativeUri");

    public static string Redact(string text)
    {
        if (text.Length > 65536) text = Text.Format("DiagnosticText", "Truncated", text[..65536]);
        try
        {
            text = Regex.Replace(text, "https?://[^\\s\"'<>]+", match =>
                Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? Address(uri) : Text.Get("DiagnosticText", "RedactedUrl"),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            text = Regex.Replace(text,
                "([?&](?:sign|signature|token|access[_-]?token|refresh[_-]?token|key|apikey|auth|expires|x-amz-[^=&#\\s]+))=[^&\\s#]*",
                "$1=[redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            text = Regex.Replace(text,
                "\\b(authorization|proxy-authorization|cookie|set-cookie|access[_-]?token|refresh[_-]?token|id[_-]?token|token|password|passwd|client[_-]?secret|api[_-]?key)\\b[\"']?\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\r\\n,;]+)",
                "$1=[redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            text = Regex.Replace(text, @"\b(Bearer|Basic)\s+[A-Za-z0-9+/=._~-]+", "$1 [redacted]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            foreach (var (folder, replacement) in new[]
            {
                (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"),
                (Environment.SpecialFolder.ApplicationData, "%APPDATA%"),
                (Environment.SpecialFolder.UserProfile, "%USERPROFILE%")
            })
            {
                string path = Environment.GetFolderPath(folder);
                if (path.Length > 0) text = text.Replace(path, replacement, StringComparison.OrdinalIgnoreCase)
                    .Replace(path.Replace('\\', '/'), replacement, StringComparison.OrdinalIgnoreCase);
            }
            return text;
        }
        catch (RegexMatchTimeoutException) { return Text.Get("DiagnosticText", "RedactionFailed"); }
    }

    public static string Summary(Exception error) => Redact(string.Join(Text.Get("DiagnosticText", "ExceptionSeparator"), Enumerate(error).Take(8)
        .Select(x => x.Message).Distinct()));

    public static string ExceptionDetails(Exception error)
    {
        var errors = Enumerate(error).ToArray();
        var text = new StringBuilder();
        for (int i = 0; i < errors.Length; i++)
        {
            var current = errors[i];
            text.AppendLine($"Exception[{i}] {current.GetType().FullName}; HResult=0x{current.HResult:X8}; {current.Message}");
            if (current is HttpRequestException request)
                text.AppendLine($"HttpRequestError={request.HttpRequestError}; StatusCode={(int?)request.StatusCode}");
            if (current is Win32Exception native) text.AppendLine($"NativeErrorCode={native.NativeErrorCode}");
            foreach (string key in new[] { "RequestUri", "ResponseUri" })
                if (current.Data[key] is string address) text.AppendLine($"{key}={address}");
        }
        for (int i = 0; i < errors.Length; i++)
            if (errors[i].StackTrace is { Length: > 0 } stack) text.AppendLine($"Stack[{i}] {stack}");
        return Redact(text.ToString());
    }

    private static IEnumerable<Exception> Enumerate(Exception error)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.Count > 0 && seen.Count < 32)
        {
            var current = pending.Pop();
            if (!seen.Add(current)) continue;
            yield return current;
            if (current is AggregateException aggregate)
            { foreach (var inner in aggregate.InnerExceptions.Reverse()) pending.Push(inner); }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
    }
}
