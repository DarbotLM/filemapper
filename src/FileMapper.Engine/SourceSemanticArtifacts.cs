using System.Security.Cryptography;
using System.Text;
using FileMapper.Semantics;

namespace FileMapper.Engine;

internal sealed class SourceSemanticArtifacts(string root, long maximumBytes)
{
    private long bytes;
    private readonly List<SourceSemanticArtifact> artifacts = [];
    public IReadOnlyList<SourceSemanticArtifact> Items => artifacts;
    public long TotalBytes => bytes;
    public string PathFor(string name)
    {
        if (name.Length > 160 || name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || name.StartsWith('.'))
            throw new ArgumentException("Artifact name must be an internal plain filename.");
        return SourceSemanticInputReader.Ordinary(Path.Combine(root, name));
    }
    public RecordWriter Open(string name) => new(this, name);
    public async Task WriteAsync<T>(string name, IEnumerable<T> values, CancellationToken token)
    { await using var writer = Open(name); foreach (var value in values) await writer.AddAsync(value, token); await writer.CompleteAsync(token); }
    public async Task RegisterGeneratedAsync(string name, long records, CancellationToken token)
    {
        var path = PathFor(name); var length = new FileInfo(path).Length;
        Reserve(length); await RegisterAsync(name, length, records, token);
    }
    private void Reserve(long count)
    {
        if (count < 0 || count > maximumBytes - bytes) throw new IOException("Semantic artifact bytes exceeded the explicit publication ceiling; the stage is not sealed.");
        bytes += count;
    }
    private async Task RegisterAsync(string name, long length, long records, CancellationToken token)
    {
        if (artifacts.Any(a => a.Path == name)) throw new InvalidOperationException("An immutable artifact was registered twice.");
        await using var input = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        artifacts.Add(new(name, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)), length, records));
    }
    internal sealed class RecordWriter : IAsyncDisposable
    {
        private readonly SourceSemanticArtifacts owner;
        private readonly string name;
        private readonly FileStream stream;
        private long records;
        private bool completed, disposed;
        public RecordWriter(SourceSemanticArtifacts owner, string name)
        { this.owner = owner; this.name = name; stream = new(owner.PathFor(name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true); }
        public async Task AddAsync<T>(T value, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed || completed, this); token.ThrowIfCancellationRequested();
            var record = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(value) + "\n");
            if (record.Length > 16 * 1024 * 1024) throw new IOException("A semantic record exceeds 16 MiB.");
            owner.Reserve(record.Length); await stream.WriteAsync(record, token); records++;
        }
        public async Task CompleteAsync(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed) throw new InvalidOperationException("Artifact already completed.");
            await stream.FlushAsync(token); stream.Flush(true); var length = stream.Length;
            await stream.DisposeAsync(); disposed = true;
            await owner.RegisterAsync(name, length, records, token); completed = true;
        }
        public async ValueTask DisposeAsync() { if (!disposed) { disposed = true; await stream.DisposeAsync(); } }
    }
}
