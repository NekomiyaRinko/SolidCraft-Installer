using System.Runtime.InteropServices;
using System.Text;
using System.IO.Compression;
using System.Text.RegularExpressions;

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
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Func<string, CancellationToken, Task> _recycle;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCI", "Logs");
    public string? FilePath { get; private set; }
    public string? Error { get; private set; }
    public string DirectoryPath => InstanceManager.Normal(_directory);

    public DiagnosticLog(string? directory = null, long maxFileBytes = 8 * 1024 * 1024)
        : this(directory, maxFileBytes, InstanceManager.RecycleAsync) { }

    internal DiagnosticLog(string? directory, long maxFileBytes, Func<string, CancellationToken, Task> recycle)
    {
        _directory = directory ?? DefaultDirectory;
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        ArgumentNullException.ThrowIfNull(recycle);
        _recycle = recycle;
        Write("SESSION", $"SCI {typeof(DiagnosticLog).Assembly.GetName().Version}; {RuntimeInformation.FrameworkDescription}; " +
            $"{RuntimeInformation.OSDescription}; Architecture={RuntimeInformation.ProcessArchitecture}; Session={_session}");
    }

    private string[] LogFiles()
    {
        InstanceManager.CheckNoReparse(_directory);
        if (!Directory.Exists(InstanceManager.Extended(_directory))) return Array.Empty<string>();
        return Directory.EnumerateFiles(InstanceManager.Extended(_directory), "SCI-*.log", SearchOption.TopDirectoryOnly)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"\ASCI-\d{8}-\d{6}-\d+-[a-f0-9]{32}-\d{3,}\.log\z", RegexOptions.CultureInvariant))
            .Select(path => InstanceManager.Child(_directory, Path.GetFileName(path))).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public async Task<int> ExportAsync(string destination, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 ZIP 文件的完整保存路径。", nameof(destination));
        await _operations.WaitAsync(token);
        try
        {
            (string Path, long Length)[] files;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _writer?.Flush();
                files = LogFiles().Select(path => (path, new FileInfo(InstanceManager.Extended(path)).Length)).ToArray();
            }
            await InstanceManager.AtomicWriteAsync(destination, async (output, cancellation) =>
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
                byte[] buffer = new byte[65536];
                foreach (var file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    InstanceManager.CheckNoReparse(file.Path);
                    await using var input = new FileStream(InstanceManager.Extended(file.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await using var entry = archive.CreateEntry(Path.GetFileName(file.Path), CompressionLevel.Optimal).Open();
                    long remaining = file.Length;
                    while (remaining > 0)
                    {
                        int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellation);
                        if (read == 0) throw new IOException("日志在导出期间被截断，请重试。");
                        await entry.WriteAsync(buffer.AsMemory(0, read), cancellation);
                        remaining -= read;
                    }
                }
            }, token);
            return files.Length;
        }
        finally { _operations.Release(); }
    }

    public async Task<int> RecycleLogsAsync(CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try
        {
            string[] files;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                files = LogFiles();
                _writer?.Dispose(); _writer = null; FilePath = null; Error = null;
                Write("SESSION", "开始清理历史日志，后续日志写入新文件。");
                if (Error is not null) throw new IOException("无法重新开始日志记录：" + Error);
            }
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                InstanceManager.CheckNoReparse(file);
                await _recycle(file, token);
                InstanceManager.RequireRecycled(file);
            }
            Write("INFO", $"已将 {files.Length} 份日志移至回收站。");
            return files.Length;
        }
        finally { _operations.Release(); }
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
