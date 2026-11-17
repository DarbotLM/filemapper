using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Semantics;
using Microsoft.Data.Sqlite;

namespace FileMapper.Engine;

/// <summary>A derived read catalog of sealed native analyses. It never opens a source corpus or filesystem snapshot writer.</summary>
public sealed partial class SourceSemanticCatalog
{
    private readonly bool readOnly;
    public string RootDirectory { get; }
    public string DatabasePath { get; }
    private const int RegistryId = 0x534d4341, ProjectionId = 0x534d5052;
    private const string ProjectionName = "catalog-index.db", SealName = "catalog-seal.json", ExpandedName = "catalog-lexical.ndjson";
    private sealed record ProjectionSeal(string Schema, string AnalysisId, string IndexSha256, long IndexBytes,
        string ExpandedSha256, long ExpandedBytes, long ExpandedRecords);
    private sealed record RegistryRow(string Id, string SealHash, DateTimeOffset ImportedAt, long Bytes);

    public SourceSemanticCatalog(string existingServiceDataDirectory, bool readOnly = false)
    {
        var root = SourceHarvestConnectionStore.NormalizePath(existingServiceDataDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("An existing service data directory is required.");
        RootDirectory = Path.Combine(root, "source-semantics"); DatabasePath = Path.Combine(RootDirectory, "catalog.db"); this.readOnly = readOnly;
        SourceHarvestConnectionStore.RequireOrdinaryPath(RootDirectory); SourceHarvestConnectionStore.RequireOrdinaryPath(DatabasePath);
    }

    public async Task<SourceSemanticCatalogAnalysis> ImportAsync(string sealedAnalysisDirectory, string expectedAnalysisId,
        CancellationToken cancellationToken = default)
    {
        if (readOnly) throw new InvalidOperationException("The semantic catalog is read-only.");
        HashId(expectedAnalysisId); var token = cancellationToken; token.ThrowIfCancellationRequested();
        using var source = new HeldDirectory(sealedAnalysisDirectory);
        using var sourceManifest = source.Open("manifest.json"); var manifestBytes = BoundedRead(sourceManifest, MaximumManifestBytes);
        var manifest = ValidateManifest(manifestBytes, expectedAnalysisId);
        if (Related(source.Path, RootDirectory))
        {
            var relative = Path.GetRelativePath(RootDirectory, source.Path).Split(Path.DirectorySeparatorChar);
            if (relative.Length != 3 || relative[0] != "runs" || !relative[1].StartsWith("source-job-", StringComparison.Ordinal) ||
                relative[1].Length != 43 || relative[1][11..].Any(c => !char.IsAsciiHexDigit(c)) || relative[2] != "analysis")
                throw new ArgumentException("Imported originals must be outside managed storage or an exact owned runs/source-job-{32hex}/analysis output.");
        }
        Directory.CreateDirectory(RootDirectory); Directory.CreateDirectory(Path.Combine(RootDirectory, "analyses"));
        using var root = new HeldDirectory(RootDirectory);
        var leasePath = Path.Combine(RootDirectory, "catalog.writer.lock"); SourceHarvestConnectionStore.RequireOrdinaryPath(leasePath);
        using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ValidateHandle(lease.SafeFileHandle, leasePath, directory: false);
        var stage = Path.Combine(RootDirectory, ".import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(stage, "manifest.json"), manifestBytes, token);
            var files = new Dictionary<string, FileStream>(StringComparer.Ordinal);
            try
            {
                foreach (var artifact in manifest.Artifacts)
                {
                    token.ThrowIfCancellationRequested(); var input = source.Open(artifact.Path); files.Add(artifact.Path, input);
                    if (input.Length != artifact.ByteLength) throw new InvalidDataException("Artifact byte length differs: " + artifact.Path);
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using var output = new FileStream(Path.Combine(stage, artifact.Path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    var buffer = new byte[65536]; long count = 0; int n;
                    while ((n = await input.ReadAsync(buffer, token)) > 0) { count += n; if (count > artifact.ByteLength) throw new InvalidDataException("Artifact grew during import."); digest.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), token); }
                    if (count != artifact.ByteLength || Convert.ToHexStringLower(digest.GetHashAndReset()) != artifact.Sha256) throw new InvalidDataException("Artifact hash differs: " + artifact.Path);
                    await output.FlushAsync(token); output.Flush(true); input.Position = 0;
                }
                var seal = await BuildProjectionAsync(stage, manifest, expectedAnalysisId, files, token);
                var sealBytes = Canonical(seal); await File.WriteAllBytesAsync(Path.Combine(stage, SealName), sealBytes, token);
                token.ThrowIfCancellationRequested();
                var final = Path.Combine(RootDirectory, "analyses", expectedAnalysisId);
                using var registry = OpenRegistryWriter(); using var cancellation = new CancellationScope(registry, token);
                var existing = RegistryFind(registry, expectedAnalysisId);
                if (Directory.Exists(final))
                {
                    // A completed orphan is still not visible. A retry must independently regenerate the same projection,
                    // then verify every retained byte before registration; a directory/row alone is never sufficient.
                    using var managed = new HeldDirectory(final); using var sealedFile = managed.Open(SealName);
                    var managedSealBytes = BoundedRead(sealedFile, MaximumManifestBytes);
                    if (!managedSealBytes.AsSpan().SequenceEqual(sealBytes)) throw new InvalidDataException("Existing analysis projection differs from the verified import.");
                    if (existing is not null && existing.SealHash != Hash(sealBytes)) throw new InvalidDataException("Registered projection seal differs.");
                    await VerifyManagedAsync(managed, manifestBytes, manifest, seal, token);
                    Directory.Delete(stage, recursive: true);
                }
                else
                {
                    if (existing is not null) throw new InvalidDataException("Registered immutable analysis directory is missing.");
                    SourceHarvestConnectionStore.RequireOrdinaryPath(final); Directory.Move(stage, final);
                }
                token.ThrowIfCancellationRequested();
                var importedAt = existing?.ImportedAt ?? DateTimeOffset.UtcNow;
                var sealedBytes = checked(manifestBytes.LongLength + manifest.Artifacts.Sum(a => a.ByteLength));
                cancellation.Run(() =>
                {
                    using var transaction = registry.BeginTransaction();
                    Execute(registry, "INSERT INTO analyses(id,seal_hash,imported_at,sealed_bytes) VALUES($id,$seal,$at,$bytes) ON CONFLICT(id) DO NOTHING",
                        ("$id", expectedAnalysisId), ("$seal", Hash(sealBytes)), ("$at", importedAt.ToString("O")), ("$bytes", sealedBytes));
                    token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
                });
                return new(expectedAnalysisId, manifest, importedAt, sealedBytes);
            }
            finally { foreach (var file in files.Values) file.Dispose(); }
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 9 && token.IsCancellationRequested)
        { throw new OperationCanceledException("Semantic catalog import canceled; unregistered staging is retained.", error, token); }
        // Failed stages intentionally remain unregistered and inspectable; no source/corpus artifact is deleted.
    }

    private static bool Related(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static SourceSemanticManifest ValidateManifest(byte[] bytes, string expectedId)
    {
        if (Hash(bytes) != expectedId) throw new InvalidDataException("Manifest bytes differ from the expected analysis SHA-256.");
        var manifest = Parse<SourceSemanticManifest>(bytes);
        if (manifest.Schema != SourceSemanticWorkspace.Schema || manifest.State != "sealed-partial-semantic-analysis" || manifest.Artifacts is null || manifest.Coverage is null || manifest.Options is null)
            throw new InvalidDataException("Unsupported or unsealed native semantic manifest.");
        HashId(manifest.CaptureId); HashId(manifest.ExtractionId); HashId(manifest.PolicySha256); HashId(manifest.RequestSha256); HashId(manifest.InputSetSha256); HashId(manifest.SemanticInputSetSha256);
        if (manifest.Artifacts.Count is < 13 or > 2013 || manifest.StartedAt > manifest.ParsedAt || manifest.ParsedAt > manifest.FinishedAt || manifest.CaptureStartedAt > manifest.CaptureFinishedAt)
            throw new InvalidDataException("Manifest count or timestamp admission bounds failed.");
        var names = new HashSet<string>(StringComparer.Ordinal); long total = bytes.LongLength, records = 0;
        foreach (var a in manifest.Artifacts)
        {
            if (a is null || !ArtifactName(a.Path) || !names.Add(a.Path) || a.ByteLength < 0 || a.Records < 0) throw new InvalidDataException("Unsupported, duplicate or invalid artifact descriptor.");
            HashId(a.Sha256); total = checked(total + a.ByteLength); records = checked(records + a.Records);
            if (total > MaximumSealedBytes || records > MaximumRecords) throw new InvalidDataException("Catalog sealed-byte or aggregate-record admission limit exceeded.");
        }
        if (!FixedArtifacts.IsSubsetOf(names) || total > manifest.Options.MaximumOutputBytes) throw new InvalidDataException("Native artifact set or declared output bound differs.");
        var c = manifest.Coverage;
        if (c.Nodes < 0 || c.Edges < 0 || c.Observations < 0 || c.Findings < 0 || c.SelectedEntries < 0 || c.SelectedEntries > c.CaptureEntries ||
            c.ExactNodeSpans < 0 || c.UnavailableNodeSpans < 0 || c.ExactNodeSpans + c.UnavailableNodeSpans != c.Nodes || c.LexicalLinks < 0)
            throw new InvalidDataException("Invalid native coverage.");
        return manifest;
    }
    private SqliteConnection OpenRegistryWriter()
    {
        var existed = File.Exists(DatabasePath); var db = Connect(DatabasePath, writer: true);
        try
        {
            if (existed) CheckDatabase(db, RegistryId);
            else Execute(db, $"PRAGMA application_id={RegistryId}; PRAGMA user_version=1; CREATE TABLE analyses(id TEXT PRIMARY KEY,seal_hash TEXT NOT NULL,imported_at TEXT NOT NULL,sealed_bytes INTEGER NOT NULL) STRICT;");
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static void CheckDatabase(SqliteConnection db, int applicationId)
    { if (Scalar(db, "PRAGMA application_id") != applicationId || Scalar(db, "PRAGMA user_version") != 1) throw new InvalidDataException("Unsupported semantic catalog SQLite format."); }
    private static RegistryRow? RegistryFind(SqliteConnection db, string id)
    {
        using var command = Command(db, "SELECT id,seal_hash,imported_at,sealed_bytes FROM analyses WHERE id=$id", ("$id", id)); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(3)) : null;
    }
    private static async Task VerifyManagedAsync(HeldDirectory managed, byte[] manifestBytes, SourceSemanticManifest manifest, ProjectionSeal seal, CancellationToken token)
    {
        using (var file = managed.Open("manifest.json")) if (!BoundedRead(file, MaximumManifestBytes).AsSpan().SequenceEqual(manifestBytes)) throw new InvalidDataException("Managed manifest differs.");
        foreach (var a in manifest.Artifacts)
        { using var file = managed.Open(a.Path); if (file.Length != a.ByteLength || await HashStreamAsync(file, token) != a.Sha256) throw new InvalidDataException("Managed original differs: " + a.Path); }
        foreach (var (name, size, hash) in new[] { (ProjectionName, seal.IndexBytes, seal.IndexSha256), (ExpandedName, seal.ExpandedBytes, seal.ExpandedSha256) })
        { using var file = managed.Open(name); if (file.Length != size || await HashStreamAsync(file, token) != hash) throw new InvalidDataException("Managed projection differs: " + name); }
    }
}
