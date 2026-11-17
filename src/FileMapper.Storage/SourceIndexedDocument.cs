using FileMapper.Harvest;

namespace FileMapper.Storage;

/// <summary>A retained-entry selector, not an assertion that decoding or semantic admission will succeed.</summary>
public sealed record SourceIndexedDocument(string EntryId, string? DocumentId, string RepositoryPath,
    string? SourceSha256, long? SourceBytes, string? Language, string CaptureStatus,
    string? LexicalStatus, bool? LexicallyComplete, bool SourceCandidate)
{
    internal static SourceIndexedDocument From(CapturedSourceEntry entry, string? lexicalStatus, bool? complete)
    {
        var extension = Path.GetExtension(entry.Entry.RepositoryPath).ToLowerInvariant();
        var language = extension == ".cs" ? "C#" : extension is ".ps1" or ".psm1" or ".psd1" ? "PowerShell" : null;
        return new(entry.Entry.EntryId, entry.DocumentId, entry.Entry.RepositoryPath, entry.Blob?.Sha256,
            entry.Blob?.ByteLength, language, entry.Status, lexicalStatus, complete,
            language is not null && entry.Status == "captured" && entry.DocumentId is not null && entry.Blob is not null &&
            entry.Entry.Mode is "100644" or "100755" && entry.Entry.ObjectType == "blob" && entry.Entry.PathEncoding == "utf-8");
    }

    // Both index formats retain the same canonical capture-entry record. Parameters remain bound values.
    internal const string Filter = """
        AND json_extract(e.entry_content,'$.entry.repositoryPath') LIKE $q ESCAPE char(92)
        AND ($supported=0 OR (e.status='captured' AND e.document_id IS NOT NULL
          AND json_extract(e.entry_content,'$.blob.sha256') IS NOT NULL
          AND json_extract(e.entry_content,'$.entry.mode') IN ('100644','100755')
          AND json_extract(e.entry_content,'$.entry.objectType')='blob'
          AND json_extract(e.entry_content,'$.entry.pathEncoding')='utf-8'
          AND (lower(json_extract(e.entry_content,'$.entry.repositoryPath')) LIKE '%.cs'
            OR lower(json_extract(e.entry_content,'$.entry.repositoryPath')) LIKE '%.ps1'
            OR lower(json_extract(e.entry_content,'$.entry.repositoryPath')) LIKE '%.psm1'
            OR lower(json_extract(e.entry_content,'$.entry.repositoryPath')) LIKE '%.psd1')))
        """;
}
