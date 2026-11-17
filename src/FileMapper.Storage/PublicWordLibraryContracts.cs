using FileMapper.Harvest;

namespace FileMapper.Storage;

/// <summary>Provider evidence must independently establish public visibility for this exact repository at observation time.</summary>
public sealed record PublicRepositoryEvidence(string CaptureId, string ProviderStableId, string RepositoryUrl,
    string Visibility, DateTimeOffset ObservedAt, string EvidenceUri, string EvidenceSha256);
public sealed record PublicWordLibraryReceipt(string EditionId, string DatabasePath, long EnumeratedEntries,
    long AdmittedDocuments, long ExcludedEntries, long WordOccurrences, DateTimeOffset PublishedAt);
public sealed record PublicWordLibraryOptions(long MaximumEditionBytes = 8L * 1024 * 1024 * 1024, long MinimumFreeBytes = 1024L * 1024 * 1024);


public sealed record PublicHeadEvidence(string ReceiptSha256, string StdoutSha256, DateTimeOffset ObservedAt);
