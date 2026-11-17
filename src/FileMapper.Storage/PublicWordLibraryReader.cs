using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed record PublicWordEditionPin(string Path, string Sha256);
public sealed record PublicWordObservation(string EditionSha256, string EditionId, string RepositoryId, string Revision,
    string RepositoryUrl, string? RepositoryFamily, string ForkFamilyStatus, string RevisionFamily, string TermId,
    string NormalizedTerm, string Shape, string DocumentId, string Path, string PathBytes, string SourceSha256,
    string OccurrenceId, long ByteStart, long ByteLength, long Utf16Start, long Utf16Length, long Line, long Column,
    string Spelling, string LexicalStatus, bool Complete);

/// <summary>Queries caller-pinned immutable editions in supplied order without loading a corpus into memory.</summary>
public static class PublicWordLibraryReader
{
    public static async IAsyncEnumerable<PublicWordObservation> FindAsync(IEnumerable<PublicWordEditionPin> editions,
        string termId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editions);
        if (!termId.StartsWith("source-term:", StringComparison.Ordinal) || termId.Length != 76) throw new ArgumentException("Expected a source term ID.");
        foreach (var edition in editions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edition.Sha256.Length != 64 || edition.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("Expected a lowercase edition SHA-256.");
            await using (var stream = new FileStream(edition.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                if (actual != edition.Sha256) throw new InvalidDataException("Edition file does not match its pinned SHA-256.");
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(edition.Path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                db.Open();
                JsonElement Meta(string key)
                {
                    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM metadata WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key);
                    var text = cmd.ExecuteScalar() as string ?? throw new InvalidDataException("Edition metadata is incomplete.");
                    using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
                }
                if (Meta("schema").GetString() != "filemapper.public-word-library/4") throw new InvalidDataException("Unsupported word edition.");
                var capture = Meta("capture"); var id = Meta("editionId").GetString()!;
                var family = Meta("repositoryFamily"); var fork = Meta("forkFamilyStatus").GetString()!; var revisionFamily = Meta("revisionFamily").GetString()!;
                using var query = db.CreateCommand(); query.CommandText = "SELECT term_id,normalized_term,shape,document_id,path,path_bytes,source_sha256,occurrence_id,byte_start,byte_length,utf16_start,utf16_length,line,column_no,spelling,lexical_status,complete FROM words WHERE term_id=$term ORDER BY document_id,byte_start,occurrence_id"; query.Parameters.AddWithValue("$term", termId);
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new(edition.Sha256, id, capture.GetProperty("repositoryId").GetString()!, capture.GetProperty("commit").GetString()!, capture.GetProperty("canonicalUri").GetString()!, family.ValueKind == JsonValueKind.Null ? null : family.GetString(), fork, revisionFamily,
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetString(14), reader.GetString(15), reader.GetInt64(16) == 1);
                }
            }
        }
    }
}
