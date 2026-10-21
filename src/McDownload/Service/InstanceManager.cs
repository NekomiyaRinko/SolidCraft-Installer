using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownload.Model;

namespace McDownload.Service;

public sealed class InstanceManager : IInstanceManager
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root;
    private readonly string _versions;
    private readonly string _locks;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<string, CancellationToken, Task> _recycle;

    public InstanceManager(string minecraftRoot, TimeSpan? lockTimeout = null)
        : this(minecraftRoot, lockTimeout, RecycleAsync) { }

    internal InstanceManager(string minecraftRoot, TimeSpan? lockTimeout,
        Func<string, CancellationToken, Task> recycle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _root = Normal(minecraftRoot);
        _versions = Child(_root, "versions");
        _locks = Child(_root, ".launcher/locks");
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);
        if (_lockTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        _recycle = recycle ?? throw new ArgumentNullException(nameof(recycle));
        foreach (string path in new[] { _root, _versions, _locks, Child(_root, "assets"), Child(_root, "libraries") })
            EnsureDirectory(path);
    }

    internal string MinecraftRoot => _root;

    public async Task<IReadOnlyList<InstanceMetadata>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<InstanceMetadata>();
        foreach (string entry in Directory.EnumerateDirectories(Extended(_versions)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Normal(entry);
            if ((File.GetAttributes(Extended(directory)) & FileAttributes.ReparsePoint) != 0) continue;
            if (!File.Exists(Extended(Child(directory, "instance.json")))) continue;
            try
            {
                var raw = await ReadRawAsync(directory, cancellationToken);
                await using var lease = await LockInstanceAsync(raw.Id, cancellationToken);
                result.Add(await ReadLockedAsync(directory, cancellationToken));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (result.Select(x => x.Id).Distinct().Count() != result.Count)
            throw new InstanceConflictException("实例标识重复。");
        return result.OrderBy(x => x.Editable.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<InstanceMetadata> GetAsync(InstanceId instanceId, CancellationToken cancellationToken)
    {
        string directory = await FindDirectoryAsync(instanceId, cancellationToken);
        await using var lease = await LockInstanceAsync(instanceId, cancellationToken);
        return await ReadLockedAsync(directory, cancellationToken);
    }

    public async Task<InstanceMetadata> CreateAsync(InstanceCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(request.DirectoryName);
        ValidateName(request.DirectoryName + ".json");
        ValidateName(request.MinecraftVersionId);
        ValidateEditable(request.Editable);
        if (request.IsolationPolicy != IsolationPolicy.PerInstance)
            throw new InstanceValidationException("仅支持各实例独立的隔离策略。");

        await using var lease = await LockAsync("root", cancellationToken);
        string directory = Child(_versions, request.DirectoryName);
        if (Directory.EnumerateFileSystemEntries(Extended(_versions))
            .Any(x => string.Equals(Path.GetFileName(Normal(x)), request.DirectoryName, StringComparison.OrdinalIgnoreCase)))
            throw new InstanceConflictException("实例目录已经存在。");

        cancellationToken.ThrowIfCancellationRequested();
        foreach (string relative in new[] { "", "mods", "saves", "config", "logs", ".launcher/patches", ".launcher/work" })
            EnsureDirectory(relative.Length == 0 ? directory : Child(directory, relative));

        var now = DateTimeOffset.UtcNow;
        var metadata = new InstanceMetadata(1, new InstanceId(Guid.NewGuid()), request.DirectoryName,
            request.MinecraftVersionId, IsolationPolicy.PerInstance, request.Editable, null, null,
            Array.Empty<Guid>(), new InstanceRevision(""), now, now);
        metadata = metadata with { Revision = await ComputeRevisionAsync(directory, metadata, null, cancellationToken) };
        await AtomicBytesAsync(Child(directory, "instance.json"), Serialize(metadata), cancellationToken);
        return metadata;
    }

    public async Task<InstanceMetadata> UpdateAsync(InstanceId instanceId, InstanceDisplayUpdate update,
        InstanceRevision expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        string directory = await FindDirectoryAsync(instanceId, cancellationToken);
        await using var lease = await LockInstanceAsync(instanceId, cancellationToken);
        var metadata = await ReadLockedAsync(directory, cancellationToken);
        CheckRevision(metadata, expectedRevision);
        var editable = metadata.Editable with { DisplayName = update.DisplayName, Description = update.Description };
        ValidateEditable(editable);
        return await CommitLockedAsync(directory, metadata with { Editable = editable }, new(), cancellationToken);
    }

    public async Task DeleteAsync(InstanceDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var rootLease = await LockAsync("root", cancellationToken);
        string directory = await FindDirectoryAsync(request.InstanceId, cancellationToken);
        await using var instanceLease = await LockInstanceAsync(request.InstanceId, cancellationToken);
        var metadata = await ReadLockedAsync(directory, cancellationToken);
        CheckRevision(metadata, request.ExpectedRevision);

        if (request.Mode == InstanceDeleteMode.MetadataOnly)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(Extended(Child(directory, "instance.json")));
            return;
        }
        if (request.Mode != InstanceDeleteMode.RecycleInstanceDirectory)
            throw new ArgumentOutOfRangeException(nameof(request));

        string saves = Child(directory, "saves");
        var names = new List<string>();
        if (Directory.Exists(Extended(saves)))
        {
            CheckNoReparse(saves);
            foreach (string world in Directory.EnumerateDirectories(Extended(saves)))
            {
                CheckNoReparse(world);
                if (File.Exists(Extended(Child(world, "level.dat"))) || File.Exists(Extended(Child(world, "level.dat_old"))))
                    names.Add(Path.GetFileName(Normal(world)));
            }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        string message = $"将回收实例“{metadata.Editable.DisplayName}”的整个目录，包括模组、存档和配置。";
        if (names.Count > 0) message += "将删除的存档：" + string.Join("、", names) + "。";
        if (!request.UserDataDeletionConfirmed)
        {
            var error = new UserConfirmationRequiredException(message);
            error.Data["SaveNames"] = names.ToArray();
            error.Data["SaveDirectory"] = Extended(saves);
            throw error;
        }
        Trace.TraceWarning(message);
        CheckTree(directory);
        cancellationToken.ThrowIfCancellationRequested();
        await _recycle(ShellParsingPath(directory), cancellationToken);
    }

    public async Task<InstancePaths> ResolvePathsAsync(InstanceId instanceId, CancellationToken cancellationToken)
    {
        var metadata = await GetAsync(instanceId, cancellationToken);
        return Paths(metadata);
    }

    public Task<InstanceMetadata> ReadMetadataAsync(InstanceId instanceId, CancellationToken cancellationToken) =>
        GetAsync(instanceId, cancellationToken);

    public async Task<InstanceMetadata> WriteMetadataAsync(InstanceId instanceId, InstanceEditableMetadata editableMetadata,
        InstanceRevision expectedRevision, CancellationToken cancellationToken)
    {
        ValidateEditable(editableMetadata);
        string directory = await FindDirectoryAsync(instanceId, cancellationToken);
        await using var lease = await LockInstanceAsync(instanceId, cancellationToken);
        var metadata = await ReadLockedAsync(directory, cancellationToken);
        CheckRevision(metadata, expectedRevision);
        return await CommitLockedAsync(directory, metadata with { Editable = editableMetadata }, new(), cancellationToken);
    }

    internal InstancePaths Paths(InstanceMetadata metadata)
    {
        ValidateName(metadata.DirectoryName);
        string directory = Child(_versions, metadata.DirectoryName);
        CheckNoReparse(directory);
        return new InstancePaths(Extended(_root), Extended(directory), Extended(Child(directory, "instance.json")),
            Extended(Child(directory, metadata.DirectoryName + ".json")), Extended(Child(directory, metadata.DirectoryName + ".jar")),
            Extended(Child(directory, ".launcher/base.json")), Extended(Child(directory, ".launcher/patches")),
            Extended(Child(directory, ".launcher/work")), Extended(Child(directory, "mods")), Extended(Child(directory, "saves")),
            Extended(Child(_root, "assets")), Extended(Child(_root, "libraries")));
    }

    internal async Task<string> FindDirectoryAsync(InstanceId id, CancellationToken token)
    {
        if (id.Value == Guid.Empty) throw new InstanceValidationException("实例标识为空。");
        string? found = null;
        foreach (string entry in Directory.EnumerateDirectories(Extended(_versions)))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(Extended(entry)) & FileAttributes.ReparsePoint) != 0) continue;
            string directory = Normal(entry);
            if (!File.Exists(Extended(Child(directory, "instance.json")))) continue;
            InstanceMetadata metadata;
            try { metadata = await ReadRawAsync(directory, token); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (metadata.Id != id) continue;
            if (found is not null) throw new InstanceConflictException("实例标识重复。");
            found = directory;
        }
        return found ?? throw new InstanceNotFoundException("实例不存在。");
    }

    internal Task<FileStream> LockInstanceAsync(InstanceId id, CancellationToken token) => LockAsync(id.Value.ToString("N"), token);
    internal Task<FileStream> LockLibrariesAsync(CancellationToken token) => LockAsync("libraries", token);
    internal Task<FileStream> LockInstallerAsync(string sha1, CancellationToken token) => LockAsync("installer-" + sha1, token);

    private async Task<FileStream> LockAsync(string key, CancellationToken token)
    {
        string path = Child(_locks, key + ".lock");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CheckNoReparse(path);
            try { return new FileStream(Extended(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (timer.Elapsed >= _lockTimeout) throw new InstanceConflictException("实例正在被其他操作使用。", error);
                await Task.Delay(50, token);
            }
        }
    }

    internal async Task<InstanceMetadata> ReadLockedAsync(string directory, CancellationToken token)
    {
        await RecoverLockedAsync(directory, token);
        var metadata = await ReadRawAsync(directory, token);
        return metadata with { Revision = await ComputeRevisionAsync(directory, metadata, null, token) };
    }

    private static async Task<InstanceMetadata> ReadRawAsync(string directory, CancellationToken token)
    {
        CheckNoReparse(directory);
        var metadata = JsonSerializer.Deserialize<InstanceMetadata>(
            await ReadBytesAsync(Child(directory, "instance.json"), 4 * 1024 * 1024, token), JsonOptions)
            ?? throw new JsonException("instance.json 内容为空。");
        ValidateName(metadata.DirectoryName);
        ValidateName(metadata.DirectoryName + ".json");
        ValidateName(metadata.MinecraftVersionId);
        ValidateEditable(metadata.Editable);
        if (metadata.SchemaVersion != 1 || metadata.Id.Value == Guid.Empty ||
            metadata.IsolationPolicy != IsolationPolicy.PerInstance ||
            !string.Equals(metadata.DirectoryName, Path.GetFileName(Normal(directory)), StringComparison.Ordinal))
            throw new InstanceValidationException("实例元数据与目录不一致。");
        if (metadata.ActivePatchIds is null || metadata.ActivePatchIds.Count > 128 ||
            metadata.ActivePatchIds.Distinct().Count() != metadata.ActivePatchIds.Count ||
            metadata.ActivePatchIds.Any(x => x == Guid.Empty) ||
            (metadata.InstalledLoader is null) != (metadata.LoaderPatchId is null) ||
            (metadata.LoaderPatchId is Guid patch && !metadata.ActivePatchIds.Contains(patch)))
            throw new InstanceValidationException("实例 patch 引用不合法。");
        return metadata;
    }

    internal static void CheckRevision(InstanceMetadata metadata, InstanceRevision expected)
    {
        if (string.IsNullOrEmpty(expected.Value) || metadata.Revision != expected)
            throw new InstanceConflictException("实例内容已变化，请重新读取实例。");
    }

    internal async Task<InstanceMetadata> CommitLockedAsync(string directory, InstanceMetadata metadata,
        Dictionary<string, byte[]> documents, CancellationToken token)
    {
        if (metadata.ActivePatchIds.Count > 128)
            throw new InstanceValidationException("生效 patch 数量超过上限。");
        metadata = metadata with { UpdatedAt = DateTimeOffset.UtcNow };
        foreach (var item in documents)
        {
            ValidateManagedPath(directory, item.Key);
            if (item.Key.Equals("instance.json", StringComparison.OrdinalIgnoreCase) || item.Value.Length > 16 * 1024 * 1024)
                throw new InstanceValidationException("事务文档不合法。");
        }
        metadata = metadata with { Revision = await ComputeRevisionAsync(directory, metadata, documents, token) };
        var files = new JsonArray();
        foreach (var item in documents.Append(new KeyValuePair<string, byte[]>("instance.json", Serialize(metadata))))
        {
            string path = Child(directory, item.Key);
            files.Add(new JsonObject
            {
                ["path"] = item.Key,
                ["before"] = File.Exists(Extended(path)) ? await Sha256FileAsync(path, token) : null,
                ["after"] = Convert.ToHexString(SHA256.HashData(item.Value)),
                ["content"] = Convert.ToBase64String(item.Value)
            });
        }
        token.ThrowIfCancellationRequested();
        string journal = Child(directory, ".launcher/transaction.json");
        await AtomicBytesAsync(journal, Serialize(new JsonObject { ["format"] = 1, ["files"] = files }), token);
        // 提交开始后完成整组文档，不在文件间响应取消。
        await RecoverLockedAsync(directory, CancellationToken.None);
        return metadata;
    }

    internal static async Task RecoverLockedAsync(string directory, CancellationToken token)
    {
        string journal = Child(directory, ".launcher/transaction.json");
        if (!File.Exists(Extended(journal))) return;
        var document = await ReadObjectAsync(journal, token, 64 * 1024 * 1024);
        if (document["format"]?.GetValue<int>() != 1 || document["files"] is not JsonArray files || files.Count is < 1 or > 132)
            throw new IOException("实例事务记录不合法。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in files)
        {
            string relative = node?["path"]?.GetValue<string>() ?? throw new IOException("事务路径缺失。");
            ValidateManagedPath(directory, relative);
            if (!seen.Add(relative.Replace('/', '\\'))) throw new IOException("事务路径重复。");
            string path = Child(directory, relative);
            string? current = File.Exists(Extended(path)) ? await Sha256FileAsync(path, token) : null;
            string? before = node?["before"]?.GetValue<string>();
            string after = node?["after"]?.GetValue<string>() ?? throw new IOException("事务摘要缺失。");
            byte[] content = Convert.FromBase64String(node?["content"]?.GetValue<string>() ?? "");
            if (!Convert.ToHexString(SHA256.HashData(content)).Equals(after, StringComparison.OrdinalIgnoreCase))
                throw new IOException("事务内容摘要不一致。");
            if (string.Equals(current, after, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(current, before, StringComparison.OrdinalIgnoreCase))
                throw new InstanceConflictException("受管文件与事务记录不一致。");
            await AtomicBytesAsync(path, content, CancellationToken.None);
        }
        File.Delete(Extended(journal));
    }

    private static async Task<InstanceRevision> ComputeRevisionAsync(string directory, InstanceMetadata metadata,
        IReadOnlyDictionary<string, byte[]>? overlay, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Serialize(metadata with { Revision = new InstanceRevision("") }));
        var paths = new[] { metadata.DirectoryName + ".json", ".launcher/base.json" }
            .Concat(metadata.ActivePatchIds.Select(x => $".launcher/patches/{x:N}.json"))
            .OrderBy(x => x, StringComparer.Ordinal);
        foreach (string relative in paths)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes("\0" + relative + "\0"));
            if (overlay is not null && overlay.TryGetValue(relative, out byte[]? bytes))
                hash.AppendData(SHA256.HashData(bytes));
            else
            {
                string path = Child(directory, relative);
                if (File.Exists(Extended(path))) hash.AppendData(Convert.FromHexString(await Sha256FileAsync(path, token)));
                else hash.AppendData(new byte[] { 0 });
            }
        }
        return new InstanceRevision(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void ValidateManagedPath(string directory, string relative)
    {
        string value = relative.Replace('\\', '/');
        bool valid = value == "instance.json" || value == Path.GetFileName(Normal(directory)) + ".json" ||
            value == ".launcher/base.json" ||
            (value.StartsWith(".launcher/patches/", StringComparison.Ordinal) && value.EndsWith(".json", StringComparison.Ordinal) &&
             Guid.TryParseExact(value[18..^5], "N", out _));
        if (!valid) throw new InstanceValidationException("事务路径不属于受管文档。");
        _ = Child(directory, relative);
    }

    private static void ValidateEditable(InstanceEditableMetadata editable)
    {
        ArgumentNullException.ThrowIfNull(editable);
        if (string.IsNullOrWhiteSpace(editable.DisplayName) || editable.DisplayName.Length > 256 ||
            editable.Description?.Length > 8192 || editable.DisplayName.Any(char.IsControl))
            throw new InstanceValidationException("实例显示信息不合法。");
        if (!string.IsNullOrWhiteSpace(editable.JavaExecutable)) _ = Normal(editable.JavaExecutable);
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InstanceValidationException("名称不是有效的 Windows 文件名。");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new InstanceValidationException("名称是 Windows 保留名称。");
    }

    internal static string Normal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            throw new InstanceValidationException("路径必须是完整的 Windows 文件系统路径。");
        string value = Path.GetFullPath(path);
        if (value.AsSpan(Path.GetPathRoot(value)!.Length).Contains(':'))
            throw new InstanceValidationException("路径不能包含备用数据流。");
        return Path.TrimEndingDirectorySeparator(value);
    }

    internal static string ShellParsingPath(string path) => Normal(path);

    internal static string Extended(string path)
    {
        string value = Normal(path);
        return value.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + value[2..] : @"\\?\" + value;
    }

    internal static string Child(string parent, string relative)
    {
        parent = Normal(parent);
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
            throw new InstanceValidationException("相对路径不合法。");
        string[] parts = relative.Replace('/', '\\').Split('\\');
        foreach (string part in parts) ValidateName(part);
        string result = Normal(Path.Combine(parent, Path.Combine(parts)));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(parent) + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InstanceValidationException("路径超出目录边界。");
        CheckNoReparse(result);
        return result;
    }

    internal static void CheckNoReparse(string path)
    {
        string? current = Normal(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(Extended(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new InstanceValidationException("受管路径不能包含重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static void EnsureDirectory(string path)
    {
        CheckNoReparse(path);
        Directory.CreateDirectory(Extended(path));
        CheckNoReparse(path);
    }

    internal static void CheckTree(string path)
    {
        CheckNoReparse(path);
        foreach (string child in Directory.EnumerateFileSystemEntries(Extended(path)))
        {
            CheckNoReparse(child);
            if ((File.GetAttributes(Extended(child)) & FileAttributes.Directory) != 0) CheckTree(child);
        }
    }

    internal static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    internal static async Task<byte[]> ReadBytesAsync(string path, int maximum, CancellationToken token)
    {
        CheckNoReparse(path);
        await using var input = new FileStream(Extended(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("文件超过大小上限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static async Task<JsonObject> ReadObjectAsync(string path, CancellationToken token, int maximum = 16 * 1024 * 1024) =>
        JsonNode.Parse(await ReadBytesAsync(path, maximum, token)) as JsonObject ?? throw new JsonException("JSON 根节点必须是对象。");

    internal static Task AtomicBytesAsync(string path, byte[] bytes, CancellationToken token) =>
        AtomicWriteAsync(path, async (output, ct) => await output.WriteAsync(bytes, ct), token);

    internal static async Task AtomicWriteAsync(string path, Func<FileStream, CancellationToken, Task> writer, CancellationToken token)
    {
        path = Normal(path);
        EnsureDirectory(Path.GetDirectoryName(path)!);
        CheckNoReparse(path);
        string temporary = Child(Path.GetDirectoryName(path)!, ".tmp." + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = new FileStream(Extended(temporary), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writer(output, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            CheckNoReparse(path);
            if (File.Exists(Extended(path))) File.Replace(Extended(temporary), Extended(path), null, true);
            else File.Move(Extended(temporary), Extended(path));
        }
        finally
        {
            if (File.Exists(Extended(temporary))) File.Delete(Extended(temporary));
        }
    }

    internal static async Task<string> Sha256FileAsync(string path, CancellationToken token)
    {
        CheckNoReparse(path);
        await using var input = new FileStream(Extended(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }

    internal static Task RecycleAsync(string directory, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? operation = null;
            IShellItem? item = null;
            try
            {
                token.ThrowIfCancellationRequested();
                operation = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
                var fileOperation = (IFileOperation)operation;
                Marshal.ThrowExceptionForHR(fileOperation.SetOperationFlags(0x0004 | 0x0010 | 0x0040 | 0x0400 | 0x00080000 | 0x00100000));
                Guid iid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(directory, 0, ref iid, out item));
                Marshal.ThrowExceptionForHR(fileOperation.DeleteItem(item, 0));
                token.ThrowIfCancellationRequested();
                Marshal.ThrowExceptionForHR(fileOperation.PerformOperations());
                Marshal.ThrowExceptionForHR(fileOperation.GetAnyOperationsAborted(out bool aborted));
                if (aborted) throw new IOException("回收操作已取消。");
                RequireRecycled(directory);
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
            finally
            {
                if (item is not null) Marshal.FinalReleaseComObject(item);
                if (operation is not null) Marshal.FinalReleaseComObject(operation);
            }
        }) { IsBackground = true, Name = "SCI recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static void RequireRecycled(string path)
    {
        try { _ = File.GetAttributes(Extended(path)); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("文件或目录未完成回收。");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindingContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        [PreserveSig] int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
        [PreserveSig] int MoveItems(nint items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
        [PreserveSig] int CopyItems(nint items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, nint sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, nint sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
