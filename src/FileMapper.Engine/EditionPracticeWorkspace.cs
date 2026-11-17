using System.Collections.Immutable;
using System.Text.Json;
using FileMapper.Semantics;
using FileMapper.Domain;
using FileMapper.Storage;
using Microsoft.Data.Sqlite;
namespace FileMapper.Engine;
public sealed record EditionPracticeRequest(string EditionSha256, int WordLimit, int PathLimit, int CandidateLimit);
public sealed record EditionPracticeWordSource(string PieceId, string DocumentId, string OccurrenceId, string Path, string PathBytes, string SourceSha256, long ByteStart, long ByteLength, long Line, long Column, string Spelling, string LexicalStatus, bool Complete);
public sealed record EditionPracticeArena(string Schema, EditionPracticeRequest Request, WordLibraryEdition Edition, SpatialChallenge Challenge, ComputerDeckSummary? DeckSummary, ImmutableArray<EditionPracticeWordSource> WordSources, string Scope);
public sealed record SavedEditionPracticeArena(string ArtifactHash, DateTimeOffset StoredAt, EditionPracticeArena Arena);
public sealed record EditionPracticeEpisode(string Schema, string ArenaHash, string Competitor, string Attribution, ImmutableArray<SpatialMove> Moves, SpatialState FinalState);
public sealed class EditionPracticeWorkspace(MapStore store, WordLibraryWorkspace library)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public SavedEditionPracticeArena Freeze(EditionPracticeRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return library.WithPinnedEdition(request.EditionSha256, (pin, edition, ct) =>
        {
            var reason = WordLibraryWorkspace.PracticeIneligibility(pin, ct);
            if (reason is not null) throw new ArgumentException(reason);
            var policy = new ComputerDeckPolicy(request.WordLimit, request.PathLimit, request.CandidateLimit);
            ComputerFrozenChallenge frozen;
            try { frozen = ComputerChallengeWorkspace.Freeze(new([pin.Path], 0, request.WordLimit, "edition-practice-v1", null, policy), ct); }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (ct.IsCancellationRequested && e.SqliteErrorCode == 9) { throw new OperationCanceledException("Practice freeze canceled.", e, ct); }
            if (frozen.Editions.Length != 1 || frozen.Editions[0].FileHash != pin.Sha256) throw new InvalidDataException("Frozen edition differs from catalog pin.");
            WordLibraryWorkspace.CheckPath(pin.Path);
            var sources = ReadWords(pin.Path, frozen.Challenge, ct);
            WordLibraryWorkspace.CheckPath(pin.Path);
            var arena = new EditionPracticeArena("filemapper.edition-practice-arena/1", request, edition, frozen.Challenge, frozen.DeckSummary, sources,
                "Practice exposure: selected source must be treated as exposed for future evaluation. No held-out or semantic learning claim. Native beam is not a trained expert.");
            var payload = CanonicalJson.Serialize(arena); var hash = CanonicalJson.HashUtf8(payload); ct.ThrowIfCancellationRequested();
            WordLibraryWorkspace.CheckPath(pin.Path);
            var saved = store.SaveSemanticArtifact(hash, "edition-practice-arena/1", null, payload,
                CanonicalJson.Serialize(new { EditionSha256 = edition.Sha256, arena.Challenge.ChallengeHash, Pieces = arena.Challenge.Deck.Length }), ct);
            return new SavedEditionPracticeArena(hash, saved.StoredAt, arena);
        }, token);
    }
    private static ImmutableArray<EditionPracticeWordSource> ReadWords(string path, SpatialChallenge challenge, CancellationToken token)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = new Uri(path).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); var handle = db.Handle!;
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
        using var registration = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle));
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT path,path_bytes,source_sha256,byte_start,byte_length,line,column_no,spelling,lexical_status,complete FROM words WHERE document_id=$d AND occurrence_id=$o";
            var d=cmd.Parameters.Add("$d", SqliteType.Text); var o=cmd.Parameters.Add("$o", SqliteType.Text);
            var result = ImmutableArray.CreateBuilder<EditionPracticeWordSource>();
            foreach (var piece in challenge.Deck.Where(p => p.UnitKind == "word"))
            {
                token.ThrowIfCancellationRequested(); d.Value=piece.SourceNodeId; o.Value=piece.ObservationId;
                using var r=cmd.ExecuteReader();
                if (!r.Read()) throw new InvalidDataException("Frozen word source is missing from pinned edition.");
                result.Add(new(piece.Id,piece.SourceNodeId,piece.ObservationId,r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetString(7),r.GetString(8),r.GetInt64(9)!=0));
            }
            return result.ToImmutable();
        }
        catch (SqliteException e) when (token.IsCancellationRequested && e.SqliteErrorCode == 9) { throw new OperationCanceledException("Practice source read canceled.", e, token); }
        finally { registration.Dispose(); SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null!, null); }
    }
    public SavedEditionPracticeArena Load(string hash)
    {
        var artifact = store.GetSemanticArtifact(hash) ?? throw new KeyNotFoundException("Practice arena not found.");
        if (artifact.Kind != "edition-practice-arena/1") throw new ArgumentException("Artifact is not a practice arena.");
        var arena = JsonSerializer.Deserialize<EditionPracticeArena>(artifact.Payload, Json) ?? throw new InvalidDataException("Invalid practice arena.");
        if (arena.Schema != "filemapper.edition-practice-arena/1") throw new InvalidDataException("Unsupported practice arena.");
        _ = SpatialGame.Start(arena.Challenge); return new(hash, artifact.StoredAt, arena);
    }
    public SpatialArenaView Replay(SpatialReplayRequest request, CancellationToken token = default)
    {
        if (request.Moves is null || request.Moves.Length > SpatialGame.MaximumDeckLength) throw new ArgumentException("A replay accepts at most 128 moves.");
        var state = SpatialGame.Replay(Load(request.ArenaHash).Arena.Challenge, request.Moves, token);
        var orientations = state.IsOver ? ImmutableArray<SpatialOrientation>.Empty : SpatialGameGeometry.Orientations(state.Challenge.Deck[state.NextPieceIndex].BaseShape, state.Challenge.Deck[state.NextPieceIndex].GeometryVariant);
        return new(request.ArenaHash, state, SpatialGame.LegalMoves(state, token), orientations);
    }
    public SpatialDecision Choose(SpatialReplayRequest request, SpatialSearchBudget? budget, CancellationToken token = default) => SpatialOpponent.ChooseMove(Replay(request, token).State, budget, token);
    public SemanticArtifact SaveEpisode(SpatialReplayRequest request, string competitor, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(competitor) || competitor.Length > 128 || competitor.Any(char.IsControl)) throw new ArgumentException("A competitor label is required (up to 128 characters).");
        var state = Replay(request, token).State;
        var episode = new EditionPracticeEpisode("filemapper.edition-practice-episode/1", request.ArenaHash, competitor, "Caller-declared competitor; replay verifies rules, not model identity or semantic accuracy.", request.Moves.ToImmutableArray(), state);
        var payload = CanonicalJson.Serialize(episode); token.ThrowIfCancellationRequested();
        return store.SaveSemanticArtifact(CanonicalJson.HashUtf8(payload), "edition-practice-episode/1", request.ArenaHash, payload,
            CanonicalJson.Serialize(new { Competitor = competitor, state.Score, state.NextPieceIndex, state.IsOver, state.StateHash }), token);
    }
}
