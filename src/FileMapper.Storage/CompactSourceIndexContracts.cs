using FileMapper.Harvest;

namespace FileMapper.Storage;

public sealed record CompactSourceIndexInput(string CaptureId, string? ExtractionId = null);
public sealed record CompactSourceIndexBuildOptions(long MaximumDatabaseBytes = 2L * 1024 * 1024 * 1024,
    long MinimumFreeBytes = 1024L * 1024 * 1024, long MaximumEntries = 2_000_000,
    long MaximumUnits = 500_000_000, int MaximumCaptures = 2000, string? ResumeDatabasePath = null);
public sealed record CompactSourceIndexBuildProgress(string Phase, string StagingDatabasePath,
    long Captures, long Entries, long Lexicons, long Units, long Words, long DatabaseBytes);
public sealed record CompactSourceIndexBuildReceipt(string Schema, string DatabasePath, string DatabaseSha256,
    string InputCommitment, long DatabaseBytes, long Captures, long Entries, long Lexicons, long Units, long Words,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

// The immutable extraction format is owned by Harvest/Engine. These internal readers mirror its serialized contract,
// without adding an Engine -> Storage -> Engine project dependency or changing any corpus identity.
internal sealed record CompactExtractionCoverage(long Entries, long CapturedDocuments, long CompleteDocuments,
    long IncompleteDocuments, long UnavailableEntries, long Occurrences, long WordOccurrences, long UniqueLexicons);
internal sealed record CompactExtractionManifest(string Schema, string CaptureId, SourceLexiconPolicy Policy,
    SourceBlobDescriptor Entries, CompactExtractionCoverage Coverage, string State, string Scope);
internal sealed record CompactExtractionEntry(string EntryId, string? DocumentId, string? SourceSha256,
    string CaptureStatus, string LexicalStatus, string? OutputSha256, string? CanonicalNdjsonSha256,
    long Occurrences, long WordOccurrences, bool Complete);
