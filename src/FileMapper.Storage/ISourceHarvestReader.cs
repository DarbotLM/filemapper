namespace FileMapper.Storage;

/// <summary>Read-only source queries shared by the ingestion index and immutable compact projections.</summary>
public interface ISourceHarvestReader
{
    string DatabasePath { get; }
    bool IsReadOnly { get; }
    SourceIndexPage<SourceIndexedCapture> ListCaptures(int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
    SourceIndexCounts Counts(string captureId, CancellationToken cancellationToken = default);
    SourceIndexPage<SourceIndexedDocument> Documents(string captureId, string? search = null, bool supportedOnly = false, int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
    SourceIndexPage<SourceTermCount> Terms(string captureId, string? search, int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
    SourceIndexPage<SourceIndexedOccurrence> Occurrences(string captureId, string termId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
}
