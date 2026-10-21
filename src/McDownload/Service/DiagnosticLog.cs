using System.Runtime.InteropServices;
using System.Text;

namespace McDownload.Service;

public sealed class DiagnosticLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _session = $"SCI-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly long _maxFileBytes;
    private StreamWriter? _writer;
    private int _part;
    private long _bytes;
    private bool _disposed;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCI", "Logs");
    public string? FilePath { get; private set; }
    public string? Error { get; private set; }

    public DiagnosticLog(string? directory = null, long maxFileBytes = 8 * 1024 * 1024)
    {
        _directory = directory ?? DefaultDirectory;
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        Write("SESSION", $"SCI {typeof(DiagnosticLog).Assembly.GetName().Version}; {RuntimeInformation.FrameworkDescription}; " +
            $"{RuntimeInformation.OSDescription}; Architecture={RuntimeInformation.ProcessArchitecture}; Session={_session}");
    }

    public void Write(string level, string message, Exception? error = null)
    {
        string text = $"{DateTimeOffset.Now:O} [{level}] {DiagnosticText.Redact(message)}{Environment.NewLine}";
        if (error is not null) text += DiagnosticText.ExceptionDetails(error) + Environment.NewLine;
        lock (_gate)
        {
            if (_disposed || Error is not null) return;
            try
            {
                if (_writer is null || (_bytes > 0 && _bytes + Encoding.UTF8.GetByteCount(text) > _maxFileBytes))
                {
                    _writer?.Dispose(); _writer = null;
                    InstanceManager.EnsureDirectory(_directory);
                    string path = InstanceManager.Child(_directory, $"{_session}-{++_part:000}.log");
                    _writer = new StreamWriter(new FileStream(InstanceManager.Extended(path), FileMode.CreateNew,
                        FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                    FilePath = path; _bytes = 0;
                }
                _writer.Write(text);
                _bytes += Encoding.UTF8.GetByteCount(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Error = DiagnosticText.Summary(ex);
                try { _writer?.Dispose(); } catch (IOException) { }
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); } catch (IOException ex) { Error = DiagnosticText.Summary(ex); }
            _writer = null;
        }
    }
}
