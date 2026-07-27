using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace Envoy;

public sealed class StorageService
{
    private readonly string _root;
    private readonly string _files;
    private readonly string _uploads;
    private readonly string _messagesPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ChatMessage> _messages = [];
    private readonly Dictionary<Guid, UploadState> _uploadStates = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StorageService()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Envoy");
        _files = Path.Combine(_root, "files");
        _uploads = Path.Combine(_root, "uploads");
        _messagesPath = Path.Combine(_root, "messages.json");
        Directory.CreateDirectory(_files); Directory.CreateDirectory(_uploads);
        Load();
    }

    public IReadOnlyList<ChatMessage> History() => _messages.OrderBy(x => x.SentAt).ToList();
    public long StorageBytes => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    public string FilePath(Guid id) => Path.Combine(_files, id.ToString("N"));
    private string StatePath(Guid id) => Path.Combine(_uploads, id + ".json");
    private string PartPath(Guid id) => Path.Combine(_uploads, id + ".part");

    public async Task<ChatMessage> AddTextAsync(string sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("消息不能为空。");
        var message = new ChatMessage(Guid.NewGuid(), CleanName(sender), DateTimeOffset.UtcNow, text.Trim()[..Math.Min(text.Trim().Length, 4000)], null);
        await _gate.WaitAsync(); try { _messages.Add(message); await SaveAsync(); } finally { _gate.Release(); }
        return message;
    }

    public async Task<CreateUploadResponse> CreateUploadAsync(UploadRequest request)
    {
        if (request.Length <= 0) throw new InvalidOperationException("文件不能为空。");
        var drive = new DriveInfo(Path.GetPathRoot(_root)!);
        if (drive.AvailableFreeSpace < Math.Min(request.Length, 1024L * 1024 * 1024)) throw new IOException("磁盘可用空间不足，无法开始上传。");
        const int chunkSize = 4 * 1024 * 1024;
        var count = checked((int)((request.Length + chunkSize - 1) / chunkSize));
        var state = new UploadState(Guid.NewGuid(), Path.GetFileName(request.Name), request.ContentType ?? "application/octet-stream", request.Length, CleanName(request.Sender), request.Sha256, chunkSize, count, new bool[count], DateTimeOffset.UtcNow);
        await _gate.WaitAsync(); try { _uploadStates[state.Id] = state; await WriteStateAsync(state); } finally { _gate.Release(); }
        return new(state.Id, chunkSize, count);
    }

    public UploadState GetUpload(Guid id) => _uploadStates.TryGetValue(id, out var state) ? state : throw new KeyNotFoundException("上传任务不存在或已过期。");
    public int[] MissingChunks(Guid id) => GetUpload(id).Received.Select((done, index) => (done, index)).Where(x => !x.done).Select(x => x.index).ToArray();

    public async Task<bool> WriteChunkAsync(Guid id, int index, Stream body, long length)
    {
        await _gate.WaitAsync();
        try
        {
            var state = GetUpload(id);
            if (index < 0 || index >= state.ChunkCount) throw new InvalidOperationException("分块编号无效。");
            var expected = index == state.ChunkCount - 1 ? state.Length - (long)index * state.ChunkSize : state.ChunkSize;
            if (length != expected) throw new InvalidOperationException("分块大小不正确。");
            await using var file = new FileStream(PartPath(id), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            file.SetLength(state.Length); file.Seek((long)index * state.ChunkSize, SeekOrigin.Begin);
            await body.CopyToAsync(file);
            state.Received[index] = true; await WriteStateAsync(state);
            return state.Received.All(x => x);
        }
        finally { _gate.Release(); }
    }

    public async Task<ChatMessage> CompleteUploadAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var state = GetUpload(id);
            if (state.Received.Any(x => !x)) throw new InvalidOperationException("仍有未上传的分块。");
            var path = PartPath(id); var hash = await HashAsync(path);
            if (!string.IsNullOrWhiteSpace(state.Sha256) && !hash.Equals(state.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("文件校验失败，请重新上传。");
            File.Move(path, FilePath(id), true); File.Delete(StatePath(id)); _uploadStates.Remove(id);
            var file = new FileAttachment(id, state.Name, state.ContentType, state.Length, hash, DateTimeOffset.UtcNow.AddDays(7));
            var message = new ChatMessage(Guid.NewGuid(), state.Sender, DateTimeOffset.UtcNow, null, file);
            _messages.Add(message); await SaveAsync(); return message;
        }
        finally { _gate.Release(); }
    }

    public async Task CleanupAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            var expired = _messages.Where(x => x.File?.ExpiresAt <= DateTimeOffset.UtcNow).ToList();
            _messages = _messages.Except(expired).ToList();
            foreach (var file in Directory.EnumerateFiles(_uploads)) if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) File.Delete(file);
            foreach (var msg in expired) if (msg.File is not null && File.Exists(FilePath(msg.File.UploadId))) File.Delete(FilePath(msg.File.UploadId));
            await SaveAsync();
        }
        finally { _gate.Release(); }
    }

    private void Load()
    {
        if (File.Exists(_messagesPath)) _messages = JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(_messagesPath), Json) ?? [];
        foreach (var path in Directory.EnumerateFiles(_uploads, "*.json")) { var state = JsonSerializer.Deserialize<UploadState>(File.ReadAllText(path), Json); if (state is not null) _uploadStates[state.Id] = state; }
    }
    private Task SaveAsync() => File.WriteAllTextAsync(_messagesPath, JsonSerializer.Serialize(_messages, Json));
    private Task WriteStateAsync(UploadState state) => File.WriteAllTextAsync(StatePath(state.Id), JsonSerializer.Serialize(state, Json));
    private static async Task<string> HashAsync(string path) { await using var f = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant(); }
    private static string CleanName(string name) => string.IsNullOrWhiteSpace(name) ? "访客" : name.Trim()[..Math.Min(name.Trim().Length, 40)];
}
