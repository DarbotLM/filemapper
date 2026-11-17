using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

public sealed partial class MapStore
{
    private const int LearningDocumentLimit = 2 * 1024 * 1024;
    private const long LearningVocabularyLimit = 1_000_000;
    private const long LearningSourceLimit = 100_000_000;
    private static readonly UTF8Encoding LearningUtf8 = new(false, true);
    private static readonly string[] LearningTables = ["pl_heads", "pl_weights", "pl_checkpoints", "pl_epochs", "pl_groups", "pl_events",
        "pl_vocabulary", "pl_sources", "pl_occurrences", "pl_contexts", "pl_encounters", "pl_event_terms"];

    /// <summary>Explicit additive migration. Read methods never call this or acquire writer admission.</summary>
    public void InitializePatternLearning()
    {
        RequireWritable();
        if (!File.Exists(DatabasePath)) throw new InvalidOperationException("Initialize the FileMapper database explicitly before adding learning storage.");
        using var ownership = AcquireWriter(); using var db = Open();
        using (var baseSchema = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_versions'"))
            if ((long)baseSchema.ExecuteScalar()! != 1) throw new InvalidOperationException("Base FileMapper schema is absent.");
        using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pl_heads(learner_id TEXT PRIMARY KEY,sequence INTEGER NOT NULL,epoch_id TEXT NOT NULL,
              checkpoint_hash TEXT NOT NULL,weights_hash TEXT NOT NULL,audit_hash TEXT NOT NULL,genesis_checkpoint_hash TEXT NOT NULL,
              genesis_audit_hash TEXT NOT NULL,vocabulary_count INTEGER NOT NULL DEFAULT 0,source_count INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS pl_weights(hash TEXT PRIMARY KEY,sha TEXT NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS pl_checkpoints(learner_id TEXT NOT NULL,hash TEXT NOT NULL,sha TEXT NOT NULL,json TEXT NOT NULL,
              sequence INTEGER NOT NULL,epoch_id TEXT NOT NULL,weights_hash TEXT NOT NULL,audit_hash TEXT NOT NULL,
              PRIMARY KEY(learner_id,hash),FOREIGN KEY(weights_hash) REFERENCES pl_weights(hash));
            CREATE TABLE IF NOT EXISTS pl_epochs(learner_id TEXT NOT NULL,epoch_id TEXT NOT NULL,cohort_id TEXT NOT NULL,
              policy_hash TEXT NOT NULL,start_sequence INTEGER NOT NULL,end_sequence INTEGER,parent_hash TEXT,hash TEXT NOT NULL,
              sha TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(learner_id,epoch_id));
            CREATE TABLE IF NOT EXISTS pl_groups(learner_id TEXT NOT NULL,cohort_id TEXT NOT NULL,group_key TEXT NOT NULL,
              partition TEXT NOT NULL,assignment_hash TEXT NOT NULL,PRIMARY KEY(learner_id,cohort_id,group_key));
            CREATE TABLE IF NOT EXISTS pl_events(learner_id TEXT NOT NULL,sequence INTEGER NOT NULL,event_id TEXT NOT NULL,
              event_hash TEXT NOT NULL,sha TEXT NOT NULL,json TEXT NOT NULL,previous_audit TEXT NOT NULL,audit_hash TEXT NOT NULL,
              checkpoint_hash TEXT NOT NULL,projection_hash TEXT NOT NULL,PRIMARY KEY(learner_id,sequence),UNIQUE(learner_id,event_id));
            CREATE TABLE IF NOT EXISTS pl_vocabulary(learner_id TEXT NOT NULL,term_hash TEXT NOT NULL,category TEXT NOT NULL,token TEXT NOT NULL,
              encounter_count INTEGER NOT NULL,source_count INTEGER NOT NULL,first_sequence INTEGER NOT NULL,last_sequence INTEGER NOT NULL,
              PRIMARY KEY(learner_id,term_hash));
            CREATE INDEX IF NOT EXISTS ix_pl_vocab_category ON pl_vocabulary(learner_id,category,term_hash);
            CREATE TABLE IF NOT EXISTS pl_sources(learner_id TEXT NOT NULL,source_observation_id TEXT NOT NULL,pattern_hash TEXT NOT NULL,
              som_id TEXT NOT NULL,source_node_id TEXT NOT NULL,snapshot_id TEXT NOT NULL,observed_from TEXT,observed_to TEXT,
              observed_from_ticks INTEGER,observed_to_ticks INTEGER,PRIMARY KEY(learner_id,source_observation_id,pattern_hash));
            CREATE INDEX IF NOT EXISTS ix_pl_sources_som ON pl_sources(learner_id,som_id,pattern_hash);
            CREATE INDEX IF NOT EXISTS ix_pl_sources_snapshot ON pl_sources(learner_id,snapshot_id,source_observation_id);
            CREATE INDEX IF NOT EXISTS ix_pl_sources_time ON pl_sources(learner_id,observed_from_ticks,observed_to_ticks);
            CREATE TABLE IF NOT EXISTS pl_occurrences(learner_id TEXT NOT NULL,occurrence_id TEXT NOT NULL,session_key TEXT NOT NULL,
              source_observation_id TEXT NOT NULL,pattern_hash TEXT NOT NULL,PRIMARY KEY(learner_id,occurrence_id),
              UNIQUE(learner_id,session_key,source_observation_id,pattern_hash));
            CREATE TABLE IF NOT EXISTS pl_contexts(hash TEXT PRIMARY KEY,sha TEXT NOT NULL,json TEXT NOT NULL,
              workspace_id TEXT,repository_id TEXT,task_id TEXT,terminal_id TEXT,computer_id TEXT,network_id TEXT,session_id TEXT);
            CREATE INDEX IF NOT EXISTS ix_pl_context_workspace ON pl_contexts(workspace_id);
            CREATE INDEX IF NOT EXISTS ix_pl_context_repository ON pl_contexts(repository_id);
            CREATE INDEX IF NOT EXISTS ix_pl_context_task ON pl_contexts(task_id);
            CREATE INDEX IF NOT EXISTS ix_pl_context_terminal ON pl_contexts(terminal_id);
            CREATE INDEX IF NOT EXISTS ix_pl_context_computer ON pl_contexts(computer_id);
            CREATE INDEX IF NOT EXISTS ix_pl_context_network ON pl_contexts(network_id);
            CREATE INDEX IF NOT EXISTS ix_pl_context_session ON pl_contexts(session_id);
            CREATE TABLE IF NOT EXISTS pl_encounters(learner_id TEXT NOT NULL,event_id TEXT NOT NULL,sequence INTEGER NOT NULL,
              occurrence_id TEXT NOT NULL,context_hash TEXT NOT NULL,captured_at TEXT NOT NULL,captured_ticks INTEGER NOT NULL,
              PRIMARY KEY(learner_id,event_id,occurrence_id),FOREIGN KEY(context_hash) REFERENCES pl_contexts(hash));
            CREATE INDEX IF NOT EXISTS ix_pl_encounters_sequence ON pl_encounters(learner_id,sequence,occurrence_id);
            CREATE INDEX IF NOT EXISTS ix_pl_encounters_context ON pl_encounters(learner_id,context_hash,captured_ticks);
            CREATE INDEX IF NOT EXISTS ix_pl_encounters_time ON pl_encounters(learner_id,captured_ticks,occurrence_id);
            CREATE TABLE IF NOT EXISTS pl_event_terms(learner_id TEXT NOT NULL,sequence INTEGER NOT NULL,term_hash TEXT NOT NULL,
              count INTEGER NOT NULL,PRIMARY KEY(learner_id,sequence,term_hash));
            """;
        command.ExecuteNonQuery(); tx.Commit();
    }

    public PatternLearningStoreHead CreatePatternLearner(string learnerId, PatternLearningStoreEpoch epoch,
        PatternLearningStoreDocument genesisCheckpoint, PatternLearningStoreDocument weights, string weightsHash, string auditHash)
    {
        RequireWritable(); LearningText(learnerId, 256, "learnerId"); LearningHash(weightsHash); LearningHash(auditHash);
        ValidateLearningDocument(genesisCheckpoint); ValidateLearningDocument(weights); ValidateLearningEpoch(epoch);
        if (epoch.LearnerId != learnerId || epoch.StartSequence != 1 || epoch.ParentCheckpointHash is not null || weights.Hash != weightsHash)
            throw new ArgumentException("Genesis epoch or weights commitment does not match the learner.");
        using var checkpointJson = JsonDocument.Parse(genesisCheckpoint.Json); var checkpoint = checkpointJson.RootElement;
        RequireLearningString(checkpoint, "weightsHash", weightsHash); RequireLearningString(checkpoint, "auditHash", auditHash);
        RequireLearningString(checkpoint.GetProperty("epoch"), "epochId", epoch.EpochId);
        RequireLearningString(checkpoint, "partitionPolicyHash", epoch.PartitionPolicyHash);
        RequireLearningString(checkpoint.GetProperty("partitionPolicy"), "cohortId", epoch.CohortId);
        if (checkpoint.GetProperty("sequence").GetInt64() != 0 || checkpoint.GetProperty("parentCheckpointHash").ValueKind != JsonValueKind.Null)
            throw new ArgumentException("Genesis checkpoint must have sequence zero and no parent.");
        using var ownership = AcquireWriter(); using var db = Open(); RequireLearningSchema(db); using var tx = db.BeginTransaction();
        if (LearningHead(db, tx, learnerId) is not null) throw new InvalidOperationException("Learner already exists; use its current committed head.");
        InsertLearningWeights(db, tx, weights); InsertLearningEpoch(db, tx, epoch);
        var head = new PatternLearningStoreHead(learnerId, 0, epoch.EpochId, genesisCheckpoint.Hash, weightsHash, auditHash);
        InsertLearningCheckpoint(db, tx, head, genesisCheckpoint);
        LearningExecute(db, tx, "INSERT INTO pl_heads(learner_id,sequence,epoch_id,checkpoint_hash,weights_hash,audit_hash,genesis_checkpoint_hash,genesis_audit_hash) VALUES($l,0,$e,$c,$w,$a,$c,$a)",
            ("$l", learnerId), ("$e", epoch.EpochId), ("$c", genesisCheckpoint.Hash), ("$w", weightsHash), ("$a", auditHash));
        tx.Commit(); return head;
    }
    public PatternLearningStoreHead? GetPatternLearningHead(string learnerId)
    {
        LearningText(learnerId, 256, "learnerId"); using var db = OpenLearningRead(); return db is not null && LearningSchema(db) ? LearningHead(db, null, learnerId) : null;
    }
    public PatternLearningStoreDocument? GetPatternLearningCheckpoint(string learnerId, string checkpointHash)
    {
        LearningText(learnerId, 256, "learnerId"); LearningHash(checkpointHash); using var db = OpenLearningRead();
        return db is not null && LearningSchema(db) ? LearningDocument(db, null, "SELECT hash,sha,json FROM pl_checkpoints WHERE learner_id=$l AND hash=$h", ("$l", learnerId), ("$h", checkpointHash)) : null;
    }
    public PatternLearningStoreDocument? GetPatternLearningWeights(string learnerId, string weightsHash)
    {
        LearningText(learnerId, 256, "learnerId"); LearningHash(weightsHash); using var db = OpenLearningRead();
        return db is not null && LearningSchema(db) ? LearningDocument(db, null, "SELECT hash,sha,json FROM pl_weights WHERE hash=$h AND EXISTS(SELECT 1 FROM pl_checkpoints WHERE learner_id=$l AND weights_hash=$h)", ("$l", learnerId), ("$h", weightsHash)) : null;
    }

    public PatternLearningStoreReceipt CommitPatternLearning(PatternLearningStoreCommit value)
    {
        RequireWritable(); ArgumentNullException.ThrowIfNull(value); LearningText(value.LearnerId, 256, "learnerId"); LearningText(value.EventId, 256, "eventId");
        LearningHash(value.EventHash); LearningHash(value.EventJsonSha256); LearningHash(value.WeightsHash); LearningHash(value.AuditHash);
        ValidateLearningDocument(new(value.EventHash, value.EventJsonSha256, value.EventJson)); ValidateLearningDocument(value.Checkpoint); ValidateLearningEpoch(value.Epoch);
        if (value.Weights is not null) { ValidateLearningDocument(value.Weights); if (value.Weights.Hash != value.WeightsHash) throw new ArgumentException("Weights hash mismatch."); }
        if (value.ExpectedHead is null || value.ExpectedHead.LearnerId != value.LearnerId || value.Epoch.LearnerId != value.LearnerId || value.Group is null ||
            value.Group.CohortId != value.Epoch.CohortId || value.Group.Partition is not ("train" or "validation" or "test")) throw new ArgumentException("Learning commit scope or group is invalid.");
        LearningText(value.Group.GroupKey, 512, "groupKey"); LearningHash(value.Group.AssignmentHash);
        if (value.Terms is null || value.Occurrences is null || value.Terms.Length > 1024 || value.Occurrences.Length > 1024)
            throw new ArgumentException("Learning projections are absent or exceed 1024 rows per event.");
        var termIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in value.Terms)
        {
            if (term is null) throw new ArgumentException("Null vocabulary term.");
            LearningHash(term.TermHash); LearningText(term.Category, 64, "category"); LearningText(term.Token, 4096, "token");
            if (term.Count is < 1 or > 1024 || !termIds.Add(term.TermHash) || term.TermHash != LearningTermHash(term.Category, term.Token)) throw new ArgumentException("Duplicate, incorrectly hashed or out-of-range term delta.");
        }
        var occurrenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in value.Occurrences)
        {
            ValidateLearningOccurrence(occurrence);
            if (!termIds.Contains(occurrence.PatternHash) || !occurrenceIds.Add(LearningOccurrenceId(occurrence)))
                throw new ArgumentException("Occurrence has no matching vocabulary term or is duplicated in the event.");
        }
        using var eventJson = JsonDocument.Parse(value.EventJson); using var checkpointJson = JsonDocument.Parse(value.Checkpoint.Json);
        var evt = eventJson.RootElement; var checkpoint = checkpointJson.RootElement;
        var sequence = evt.GetProperty("sequence").GetInt64(); var kind = evt.GetProperty("kind").GetString();
        if (sequence != checked(value.ExpectedHead.Sequence + 1) || sequence > 1_000_000_000 || kind is not ("observation" or "training") ||
            kind == "training" && (value.Group.Partition != "train" || value.Occurrences.Length != 0)) throw new ArgumentException("Sequence/kind is invalid, or training attempts to create source observations.");
        RequireLearningString(evt, "eventId", value.EventId); RequireLearningString(evt, "previousAuditHash", value.ExpectedHead.AuditHash);
        RequireLearningString(evt, "groupKey", value.Group.GroupKey); RequireLearningString(evt, "partition", value.Group.Partition);
        RequireLearningString(evt.GetProperty("epoch"), "epochId", value.Epoch.EpochId);
        RequireLearningString(checkpoint, "weightsHash", value.WeightsHash); RequireLearningString(checkpoint, "auditHash", value.AuditHash);
        RequireLearningString(checkpoint, "parentCheckpointHash", value.ExpectedHead.CheckpointHash); RequireLearningString(checkpoint, "lastEventHash", value.EventHash);
        RequireLearningString(checkpoint, "partitionPolicyHash", value.Epoch.PartitionPolicyHash); RequireLearningString(checkpoint.GetProperty("epoch"), "epochId", value.Epoch.EpochId);
        ValidateLearningPartition(checkpoint, value.Epoch.CohortId, value.Group);
        if (checkpoint.GetProperty("sequence").GetInt64() != sequence || value.AuditHash != LearningAuditHash(value.ExpectedHead.AuditHash, value.EventHash, sequence))
            throw new ArgumentException("Checkpoint sequence or event audit chain does not match the commit.");
        var projectionHash = LearningProjectionHash(value.Terms, value.Occurrences);
        using var ownership = AcquireWriter(); using var db = Open(); RequireLearningSchema(db); using var tx = db.BeginTransaction();
        using (var duplicate = LearningCommand(db, tx, "SELECT sequence,event_hash,sha,checkpoint_hash,audit_hash,projection_hash FROM pl_events WHERE learner_id=$l AND event_id=$id", ("$l", value.LearnerId), ("$id", value.EventId)))
        using (var reader = duplicate.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetString(1) != value.EventHash || reader.GetString(2) != value.EventJsonSha256 || reader.GetString(3) != value.Checkpoint.Hash || reader.GetString(4) != value.AuditHash || reader.GetString(5) != projectionHash)
                    throw new InvalidOperationException("Conflicting duplicate event; committed history is immutable.");
                return new(true, new(value.LearnerId, reader.GetInt64(0), value.Epoch.EpochId, value.Checkpoint.Hash, value.WeightsHash, value.AuditHash), value.EventId, value.EventHash);
            }
        var current = LearningHead(db, tx, value.LearnerId) ?? throw new KeyNotFoundException("Learner not found.");
        if (current != value.ExpectedHead) throw new InvalidOperationException("Learning head changed; reload and replay before committing.");
        _ = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_checkpoints WHERE learner_id=$l AND hash=$h", ("$l", value.LearnerId), ("$h", current.CheckpointHash))
            ?? throw new InvalidDataException("Current checkpoint is missing.");
        using (var oldEpoch = LearningCommand(db, tx, "SELECT cohort_id,policy_hash FROM pl_epochs WHERE learner_id=$l AND epoch_id=$e", ("$l", value.LearnerId), ("$e", current.EpochId)))
        using (var reader = oldEpoch.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != value.Epoch.CohortId || reader.GetString(1) != value.Epoch.PartitionPolicyHash)
                throw new InvalidOperationException("A learner lineage cannot relabel its sealed cohort; start new genesis weights for a new cohort.");
        if (value.Epoch.EpochId != current.EpochId && (value.Epoch.StartSequence != sequence || value.Epoch.ParentCheckpointHash != current.CheckpointHash))
            throw new ArgumentException("Successor epoch does not commit its exact parent/start boundary.");
        if (sequence < value.Epoch.StartSequence || sequence > value.Epoch.EndSequence) throw new ArgumentException("Commit lies outside the epoch.");
        InsertLearningEpoch(db, tx, value.Epoch); InsertLearningGroup(db, tx, value.LearnerId, value.Group);
        if (value.Weights is not null) InsertLearningWeights(db, tx, value.Weights);
        if (LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_weights WHERE hash=$h", ("$h", value.WeightsHash)) is null)
            throw new ArgumentException("Referenced weights have not been persisted.");
        if (kind == "observation" && value.WeightsHash != current.WeightsHash) throw new ArgumentException("Unlabeled observations cannot change supervised weights.");
        var head = new PatternLearningStoreHead(value.LearnerId, sequence, value.Epoch.EpochId, value.Checkpoint.Hash, value.WeightsHash, value.AuditHash);
        InsertLearningCheckpoint(db, tx, head, value.Checkpoint);
        LearningExecute(db, tx, "INSERT INTO pl_events VALUES($l,$s,$id,$h,$sha,$j,$p,$a,$c,$projection)",
            ("$l", value.LearnerId), ("$s", sequence), ("$id", value.EventId), ("$h", value.EventHash), ("$sha", value.EventJsonSha256), ("$j", value.EventJson),
            ("$p", current.AuditHash), ("$a", value.AuditHash), ("$c", value.Checkpoint.Hash), ("$projection", projectionHash));
        foreach (var term in value.Terms) InsertLearningTerm(db, tx, value.LearnerId, sequence, term);
        foreach (var occurrence in value.Occurrences) InsertLearningOccurrence(db, tx, value.LearnerId, value.EventId, sequence, occurrence);
        var changed = LearningExecute(db, tx, "UPDATE pl_heads SET sequence=$s,epoch_id=$e,checkpoint_hash=$c,weights_hash=$w,audit_hash=$a WHERE learner_id=$l AND sequence=$old AND checkpoint_hash=$parent",
            ("$s", sequence), ("$e", value.Epoch.EpochId), ("$c", value.Checkpoint.Hash), ("$w", value.WeightsHash), ("$a", value.AuditHash), ("$l", value.LearnerId), ("$old", current.Sequence), ("$parent", current.CheckpointHash));
        if (changed != 1) throw new InvalidOperationException("Learning compare-and-swap did not update one head.");
        tx.Commit(); return new(false, head, value.EventId, value.EventHash);
    }

    public Page<PatternLearningStoreHead> ListPatternLearners(int offset = 0, int limit = 100) => ReadPatternLearners(offset, limit);
    public Page<PatternLearningStoreHead> ReadPatternLearners(int offset = 0, int limit = 100)
    {
        LearningPage(offset, limit); using var db = OpenLearningRead();
        if (db is null || !LearningSchema(db)) return new([], 0, offset, limit);
        using var count = Command(db, "SELECT COUNT(*) FROM pl_heads"); var total = (long)count.ExecuteScalar()!;
        using var query = Command(db, "SELECT learner_id,sequence,epoch_id,checkpoint_hash,weights_hash,audit_hash FROM pl_heads ORDER BY learner_id LIMIT $limit OFFSET $offset", ("$limit", limit), ("$offset", offset));
        using var reader = query.ExecuteReader(); var items = new List<PatternLearningStoreHead>();
        while (reader.Read()) items.Add(ReadLearningHead(reader)); return new(items, total, offset, limit);
    }
    public PatternLearningStoredEvent? GetPatternLearningEvent(string learnerId, string eventId)
    {
        LearningText(learnerId, 256, "learnerId"); LearningText(eventId, 256, "eventId"); using var db = OpenLearningRead();
        if (db is null || !LearningSchema(db)) return null;
        using var query = Command(db, "SELECT sequence,event_id,event_hash,json,checkpoint_hash,audit_hash,sha FROM pl_events WHERE learner_id=$l AND event_id=$id", ("$l", learnerId), ("$id", eventId));
        using var reader = query.ExecuteReader(); return reader.Read() ? ReadLearningEvent(reader) : null;
    }
    public PatternLearningEventPage ReadPatternLearningEvents(string learnerId, int offset = 0, int limit = 100)
    {
        LearningText(learnerId, 256, "learnerId"); LearningPage(offset, limit); using var db = OpenLearningRead();
        if (db is null || !LearningSchema(db)) return new([], 0, offset, limit);
        using var count = Command(db, "SELECT COUNT(*) FROM pl_events WHERE learner_id=$l", ("$l", learnerId)); var total = (long)count.ExecuteScalar()!;
        using var query = Command(db, "SELECT sequence,event_id,event_hash,json,checkpoint_hash,audit_hash,sha FROM pl_events WHERE learner_id=$l ORDER BY sequence LIMIT $limit OFFSET $offset", ("$l", learnerId), ("$limit", limit), ("$offset", offset));
        using var reader = query.ExecuteReader(); var items = new List<PatternLearningStoredEvent>();
        while (reader.Read()) items.Add(ReadLearningEvent(reader)); return new(items.ToArray(), total, offset, limit);
    }
    public PatternLearningVocabularyPage ReadPatternLearningVocabulary(string learnerId, string? search = null,
        string? category = null, int offset = 0, int limit = 100)
    {
        LearningText(learnerId, 256, "learnerId"); LearningPage(offset, limit);
        if (search is not null && search.Length > 512 || category is not null && category.Length > 64) throw new ArgumentException("Vocabulary filter exceeds its bound.");
        using var db = OpenLearningRead(); if (db is null || !LearningSchema(db)) return new([], 0, offset, limit);
        var where = "learner_id=$l" + (category is null ? "" : " AND category=$category") + (string.IsNullOrEmpty(search) ? "" : " AND token LIKE $q ESCAPE '!'");
        var parameters = new (string, object?)[] { ("$l", learnerId), ("$category", category), ("$q", Like(search)), ("$limit", limit), ("$offset", offset) };
        using var count = Command(db, "SELECT COUNT(*) FROM pl_vocabulary WHERE " + where, parameters); var total = (long)count.ExecuteScalar()!;
        using var query = Command(db, "SELECT term_hash,category,token,encounter_count,source_count,first_sequence,last_sequence FROM pl_vocabulary WHERE " + where + " ORDER BY term_hash LIMIT $limit OFFSET $offset", parameters);
        using var reader = query.ExecuteReader(); var items = new List<PatternLearningVocabularyItem>();
        while (reader.Read())
        {
            var item = new PatternLearningVocabularyItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
            if (item.TermHash != LearningTermHash(item.Category, item.Token) || item.EncounterCount < 0 || item.SourceObservationCount < 0 || item.FirstSequence < 1 || item.LastSequence < item.FirstSequence)
                throw new InvalidDataException("Vocabulary row has corrupt content or counters.");
            items.Add(item);
        }
        return new(items.ToArray(), total, offset, limit);
    }

    private const string LearningOccurrenceFrom = """
        FROM pl_encounters e JOIN pl_occurrences o ON o.learner_id=e.learner_id AND o.occurrence_id=e.occurrence_id
        JOIN pl_sources s ON s.learner_id=o.learner_id AND s.source_observation_id=o.source_observation_id AND s.pattern_hash=o.pattern_hash
        JOIN pl_contexts c ON c.hash=e.context_hash
        """;
    private const string LearningOccurrenceColumns = "e.occurrence_id,e.event_id,e.sequence,s.source_observation_id,s.pattern_hash,s.som_id,s.source_node_id,s.snapshot_id,s.observed_from,s.observed_to,e.captured_at,c.session_id,c.hash,c.sha,c.json,c.workspace_id,c.repository_id,c.task_id,c.terminal_id,c.computer_id,c.network_id,e.captured_ticks,s.observed_from_ticks,s.observed_to_ticks";
    public PatternLearningOccurrencePage ReadPatternLearningOccurrences(string learnerId, PatternLearningOccurrenceQuery? query = null)
    {
        LearningText(learnerId, 256, "learnerId"); query ??= new(); LearningPage(query.Offset, query.Limit);
        if (query.From > query.To || query.TimeBasis is not ("observed" or "captured")) throw new ArgumentException("Invalid occurrence time bounds or basis.");
        var where = new List<string> { "e.learner_id=$l" }; var parameters = new List<(string, object?)> { ("$l", learnerId), ("$limit", query.Limit), ("$offset", query.Offset) };
        Add("s.pattern_hash", query.PatternHash, 64); Add("s.som_id", query.SomId, 256); Add("s.source_observation_id", query.SourceObservationId, 256);
        Add("s.snapshot_id", query.SnapshotId, 256); Add("c.session_id", query.SessionId, 512); Add("c.workspace_id", query.WorkspaceId, 512);
        Add("c.repository_id", query.RepositoryId, 512); Add("c.task_id", query.TaskId, 512); Add("c.terminal_id", query.TerminalId, 512);
        Add("c.computer_id", query.ComputerId, 512); Add("c.network_id", query.NetworkId, 512);
        if (query.UnknownDimension is not null)
        {
            if (!new[] { "workspace", "repository", "task", "terminal", "computer", "network", "session" }.Contains(query.UnknownDimension, StringComparer.Ordinal))
                throw new ArgumentException("Unknown context dimension filter.");
            where.Add("c." + query.UnknownDimension + "_id IS NULL");
        }
        if (query.From is { } from) { where.Add((query.TimeBasis == "captured" ? "e.captured_ticks" : "s.observed_to_ticks") + ">=$from"); parameters.Add(("$from", from.UtcTicks)); }
        if (query.To is { } to) { where.Add((query.TimeBasis == "captured" ? "e.captured_ticks" : "s.observed_from_ticks") + "<=$to"); parameters.Add(("$to", to.UtcTicks)); }
        using var db = OpenLearningRead(); if (db is null || !LearningSchema(db)) return new([], 0, 0, query.Offset, query.Limit);
        using var tx = db.BeginTransaction(deferred: true); var clause = " WHERE " + string.Join(" AND ", where); var values = parameters.ToArray();
        using var count = LearningCommand(db, tx, "SELECT COUNT(*),COUNT(DISTINCT e.occurrence_id) " + LearningOccurrenceFrom + clause, values);
        long total, sourceOccurrences; using (var reader = count.ExecuteReader()) { reader.Read(); total = reader.GetInt64(0); sourceOccurrences = reader.GetInt64(1); }
        using var command = LearningCommand(db, tx, "SELECT " + LearningOccurrenceColumns + " " + LearningOccurrenceFrom + clause + " ORDER BY e.sequence,e.occurrence_id LIMIT $limit OFFSET $offset", values);
        using var rows = command.ExecuteReader(); var items = new List<PatternLearningOccurrenceItem>();
        while (rows.Read()) items.Add(ReadLearningOccurrence(rows)); return new(items.ToArray(), total, sourceOccurrences, query.Offset, query.Limit);
        void Add(string column, string? value, int maximum)
        {
            if (value is null) return; LearningText(value, maximum, column); var parameter = "$p" + parameters.Count;
            where.Add(column + "=" + parameter); parameters.Add((parameter, value));
        }
    }

    public PatternLearningJournalVerification VerifyPatternLearningJournal(string learnerId, int maxEvents = 1000, CancellationToken cancellationToken = default)
    {
        LearningText(learnerId, 256, "learnerId"); if (maxEvents is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        cancellationToken.ThrowIfCancellationRequested(); using var db = OpenLearningRead();
        if (db is null || !LearningSchema(db)) return new(learnerId, 0, 0, false, false, null, ["Learning storage is absent."]);
        using var tx = db.BeginTransaction(deferred: true); var head = LearningHead(db, tx, learnerId);
        if (head is null) return new(learnerId, 0, 0, false, false, null, ["Learner is absent."]);
        using var count = LearningCommand(db, tx, "SELECT COUNT(*) FROM pl_events WHERE learner_id=$l", ("$l", learnerId)); var total = (long)count.ExecuteScalar()!;
        var problems = new List<string>(); long checkedEvents = 0; string previousCheckpoint = "", previousAudit = "", previousWeights = "", previousEpoch = "";
        long training = 0, observations = 0;
        try
        {
            using (var genesis = LearningCommand(db, tx, "SELECT genesis_checkpoint_hash,genesis_audit_hash FROM pl_heads WHERE learner_id=$l", ("$l", learnerId)))
            using (var reader = genesis.ExecuteReader()) { reader.Read(); previousCheckpoint = reader.GetString(0); previousAudit = reader.GetString(1); }
            var genesisDocument = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_checkpoints WHERE learner_id=$l AND hash=$h", ("$l", learnerId), ("$h", previousCheckpoint))
                ?? throw new InvalidDataException("Genesis checkpoint is missing.");
            using (var json = JsonDocument.Parse(genesisDocument.Json))
            {
                var value = json.RootElement; RequireLearningString(value, "auditHash", previousAudit);
                if (value.GetProperty("sequence").GetInt64() != 0) throw new InvalidDataException("Genesis sequence is not zero.");
                previousWeights = value.GetProperty("weightsHash").GetString()!;
                previousEpoch = value.GetProperty("epoch").GetProperty("epochId").GetString()!;
            }
            _ = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_weights WHERE hash=$h", ("$h", previousWeights)) ?? throw new InvalidDataException("Genesis weights are missing.");
            using var events = LearningCommand(db, tx, "SELECT sequence,event_id,event_hash,json,checkpoint_hash,audit_hash,sha,previous_audit,projection_hash FROM pl_events WHERE learner_id=$l ORDER BY sequence LIMIT $limit", ("$l", learnerId), ("$limit", maxEvents));
            using var rows = events.ExecuteReader();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested(); var evt = ReadLearningEvent(rows);
                if (evt.Sequence != checkedEvents + 1 || rows.GetString(7) != previousAudit || evt.AuditHash != LearningAuditHash(previousAudit, evt.EventHash, evt.Sequence))
                    throw new InvalidDataException("Event sequence/audit chain differs at " + evt.Sequence + ".");
                using var eventJson = JsonDocument.Parse(evt.EventJson); var eventValue = eventJson.RootElement;
                RequireLearningString(eventValue, "eventId", evt.EventId); RequireLearningString(eventValue, "previousAuditHash", previousAudit);
                if (eventValue.GetProperty("sequence").GetInt64() != evt.Sequence) throw new InvalidDataException("Event payload sequence differs.");
                var document = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_checkpoints WHERE learner_id=$l AND hash=$h", ("$l", learnerId), ("$h", evt.CheckpointHash))
                    ?? throw new InvalidDataException("Event checkpoint is missing.");
                using var checkpointJson = JsonDocument.Parse(document.Json); var checkpoint = checkpointJson.RootElement;
                RequireLearningString(checkpoint, "parentCheckpointHash", previousCheckpoint); RequireLearningString(checkpoint, "auditHash", evt.AuditHash); RequireLearningString(checkpoint, "lastEventHash", evt.EventHash);
                if (checkpoint.GetProperty("sequence").GetInt64() != evt.Sequence) throw new InvalidDataException("Checkpoint sequence differs.");
                var weights = checkpoint.GetProperty("weightsHash").GetString()!;
                _ = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_weights WHERE hash=$h", ("$h", weights)) ?? throw new InvalidDataException("Checkpoint weights are missing.");
                var kind = eventValue.GetProperty("kind").GetString();
                var epochId = checkpoint.GetProperty("epoch").GetProperty("epochId").GetString()!;
                var epochDocument = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_epochs WHERE learner_id=$l AND epoch_id=$e", ("$l", learnerId), ("$e", epochId))
                    ?? throw new InvalidDataException("Checkpoint epoch is missing.");
                if (LearningDigest(checkpoint.GetProperty("epoch").GetRawText()) != epochDocument.Hash ||
                    eventValue.GetProperty("epoch").GetProperty("epochId").GetString() != epochId)
                    throw new InvalidDataException("Epoch document differs from the event/checkpoint.");
                var cohort = checkpoint.GetProperty("partitionPolicy").GetProperty("cohortId").GetString()!;
                using (var groupQuery = LearningCommand(db, tx, "SELECT partition,assignment_hash FROM pl_groups WHERE learner_id=$l AND cohort_id=$c AND group_key=$g",
                    ("$l", learnerId), ("$c", cohort), ("$g", eventValue.GetProperty("groupKey").GetString())))
                using (var groupReader = groupQuery.ExecuteReader())
                {
                    if (!groupReader.Read()) throw new InvalidDataException("Sealed event group is missing.");
                    var group = new PatternLearningStoreGroup(cohort, eventValue.GetProperty("groupKey").GetString()!, groupReader.GetString(0), groupReader.GetString(1));
                    ValidateLearningPartition(checkpoint, cohort, group); RequireLearningString(eventValue, "partition", group.Partition);
                    if (kind == "training" && group.Partition != "train") throw new InvalidDataException("Held-out label was used for training.");
                }
                if (kind == "training") training++;
                else if (kind == "observation") { observations++; if (weights != previousWeights) throw new InvalidDataException("Unlabeled observation changed weights."); }
                else throw new InvalidDataException("Unknown learning event kind.");
                if (checkpoint.GetProperty("trainingUpdates").GetInt64() != training || checkpoint.GetProperty("observationEvents").GetInt64() != observations)
                    throw new InvalidDataException("Checkpoint event counters differ.");
                var terms = ReadLearningEventTerms(db, tx, learnerId, evt.Sequence);
                var occurrences = ReadLearningEventOccurrences(db, tx, learnerId, evt.EventId);
                if (kind == "training" && occurrences.Length != 0 || LearningProjectionHash(terms, occurrences) != rows.GetString(8))
                    throw new InvalidDataException("Learning vocabulary/context projection differs from its committed event.");
                previousCheckpoint = evt.CheckpointHash; previousAudit = evt.AuditHash; previousWeights = weights; previousEpoch = epochId; checkedEvents++;
            }
            if (checkedEvents == total)
            {
                if (head.Sequence != total || head.CheckpointHash != previousCheckpoint || head.AuditHash != previousAudit || head.WeightsHash != previousWeights || head.EpochId != previousEpoch)
                    throw new InvalidDataException("Head differs from the terminal journal commitments.");
                cancellationToken.ThrowIfCancellationRequested();
                using var counters = LearningCommand(db, tx, """
                    SELECT COUNT(*) FROM pl_vocabulary v LEFT JOIN
                      (SELECT term_hash,SUM(count) n,MIN(sequence) first_s,MAX(sequence) last_s FROM pl_event_terms WHERE learner_id=$l GROUP BY term_hash) t ON t.term_hash=v.term_hash
                    WHERE v.learner_id=$l AND (v.encounter_count<>COALESCE(t.n,0) OR v.first_sequence<>t.first_s OR v.last_sequence<>t.last_s OR
                      v.source_count<>(SELECT COUNT(*) FROM pl_sources s WHERE s.learner_id=$l AND s.pattern_hash=v.term_hash))
                    """, ("$l", learnerId));
                if ((long)counters.ExecuteScalar()! != 0) throw new InvalidDataException("Vocabulary aggregate counters differ from encounter/source history.");
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or SqliteException)
        { problems.Add(error.Message); }
        cancellationToken.ThrowIfCancellationRequested(); var truncated = checkedEvents < total;
        return new(learnerId, checkedEvents, total, truncated, !truncated && problems.Count == 0, head, problems.ToArray());
    }

    private static PatternLearningOccurrenceItem ReadLearningOccurrence(SqliteDataReader reader)
    {
        var context = new PatternLearningStoreDocument(reader.GetString(12), reader.GetString(13), reader.GetString(14)); var dimensions = LearningContext(context);
        for (var i = 0; i < 6; i++) if ((reader.IsDBNull(15 + i) ? null : reader.GetString(15 + i)) != dimensions[i]) throw new InvalidDataException("Indexed context differs from its source document.");
        if ((reader.IsDBNull(11) ? null : reader.GetString(11)) != dimensions[6]) throw new InvalidDataException("Indexed session differs from its source document.");
        var from = LearningDate(reader, 8); var to = LearningDate(reader, 9); var captured = LearningDate(reader, 10)!.Value;
        if (captured.UtcTicks != reader.GetInt64(21) || from?.UtcTicks != (reader.IsDBNull(22) ? null : (long?)reader.GetInt64(22)) || to?.UtcTicks != (reader.IsDBNull(23) ? null : (long?)reader.GetInt64(23)))
            throw new InvalidDataException("Indexed time differs from its original recorded range.");
        var result = new PatternLearningOccurrenceItem(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), from, to, captured, dimensions[6], context);
        if (LearningOccurrenceId(new(result.SourceObservationId, result.PatternHash, result.SomId, result.SourceNodeId, result.SnapshotId,
            from, to, captured, context)) != result.OccurrenceId) throw new InvalidDataException("Occurrence identity differs from its source/session commitment.");
        return result;
    }
    private static PatternLearningStoreTerm[] ReadLearningEventTerms(SqliteConnection db, SqliteTransaction tx, string learnerId, long sequence)
    {
        using var query = LearningCommand(db, tx, "SELECT t.term_hash,v.category,v.token,t.count FROM pl_event_terms t JOIN pl_vocabulary v ON v.learner_id=t.learner_id AND v.term_hash=t.term_hash WHERE t.learner_id=$l AND t.sequence=$s ORDER BY t.term_hash", ("$l", learnerId), ("$s", sequence));
        using var reader = query.ExecuteReader(); var result = new List<PatternLearningStoreTerm>();
        while (reader.Read()) { if (result.Count == 1024) throw new InvalidDataException("Event term projection exceeds its bound."); result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))); }
        return result.ToArray();
    }
    private static PatternLearningStoreOccurrence[] ReadLearningEventOccurrences(SqliteConnection db, SqliteTransaction tx, string learnerId, string eventId)
    {
        using var query = LearningCommand(db, tx, "SELECT " + LearningOccurrenceColumns + " " + LearningOccurrenceFrom + " WHERE e.learner_id=$l AND e.event_id=$e ORDER BY e.occurrence_id", ("$l", learnerId), ("$e", eventId));
        using var reader = query.ExecuteReader(); var result = new List<PatternLearningStoreOccurrence>();
        while (reader.Read())
        {
            if (result.Count == 1024) throw new InvalidDataException("Event occurrence projection exceeds its bound.");
            var item = ReadLearningOccurrence(reader); result.Add(new(item.SourceObservationId, item.PatternHash, item.SomId, item.SourceNodeId, item.SnapshotId, item.ObservedFrom, item.ObservedTo, item.CapturedAt, item.Context));
        }
        return result.ToArray();
    }

    private static SqliteCommand LearningCommand(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string, object?)[] values)
    { var command = Command(db, sql, values); command.Transaction = transaction; return command; }
    private static int LearningExecute(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] values)
    { using var command = LearningCommand(db, tx, sql, values); return command.ExecuteNonQuery(); }
    private SqliteConnection? OpenLearningRead()
    {
        if (!File.Exists(DatabasePath)) return null;
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    private static bool LearningSchema(SqliteConnection db)
    {
        using var query = Command(db, "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'pl_%'");
        using var reader = query.ExecuteReader(); var found = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) if (LearningTables.Contains(reader.GetString(0), StringComparer.Ordinal)) found.Add(reader.GetString(0));
        if (found.Count != 0 && found.Count != LearningTables.Length) throw new InvalidDataException("Learning schema is incomplete; no implicit repair is performed.");
        return found.Count == LearningTables.Length;
    }
    private static void RequireLearningSchema(SqliteConnection db)
    { if (!LearningSchema(db)) throw new InvalidOperationException("Pattern learning storage has not been explicitly initialized."); }
    private static void LearningPage(int offset, int limit)
    { if (offset < 0 || limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Learning pages allow nonnegative offset and limit 1–500."); }
    private static void LearningText(string? text, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) throw new ArgumentException(field + " is absent or exceeds its bound.");
        try { _ = LearningUtf8.GetByteCount(text); } catch (EncoderFallbackException error) { throw new ArgumentException(field + " contains invalid Unicode.", error); }
    }
    private static void LearningHash(string? hash)
    { if (hash is null || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("Expected lowercase SHA256 digest."); }
    private static string LearningDigest(string json) => Convert.ToHexStringLower(SHA256.HashData(LearningUtf8.GetBytes(json)));
    private static string LearningObjectHash(object value) => LearningDigest(JsonSerializer.Serialize(value, Json));
    private static void ValidateLearningDocument(PatternLearningStoreDocument document)
    {
        ArgumentNullException.ThrowIfNull(document); LearningHash(document.Hash); LearningHash(document.JsonSha256);
        if (document.Json is null || document.Json.Length > LearningDocumentLimit || LearningUtf8.GetByteCount(document.Json) > LearningDocumentLimit)
            throw new ArgumentException("Learning document exceeds 2 MiB.");
        if (document.Hash != document.JsonSha256 || LearningDigest(document.Json) != document.Hash) throw new InvalidDataException("Stored learning document failed its exact UTF-8 content hash.");
        using var json = JsonDocument.Parse(document.Json, new JsonDocumentOptions { MaxDepth = 64 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Learning documents must be JSON objects.");
        LearningUnique(json.RootElement);
    }
    private static void LearningUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate property in learning document."); LearningUnique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) LearningUnique(item);
    }
    private static void RequireLearningString(JsonElement value, string property, string expected)
    { if (!value.TryGetProperty(property, out var actual) || actual.ValueKind != JsonValueKind.String || actual.GetString() != expected) throw new ArgumentException("Learning document commitment differs at " + property + "."); }
    private static string LearningAuditHash(string previous, string hash, long sequence) => LearningObjectHash(new SortedDictionary<string, object>(StringComparer.Ordinal)
    { ["eventHash"] = hash, ["previousAuditHash"] = previous, ["schema"] = "filemapper.pattern-audit/1", ["sequence"] = sequence });
    private static string LearningTermHash(string category, string token) => LearningObjectHash(new SortedDictionary<string, object>(StringComparer.Ordinal)
    { ["category"] = category, ["schema"] = "filemapper.pattern-term/1", ["token"] = token });
    private static void ValidateLearningPartition(JsonElement checkpoint, string cohort, PatternLearningStoreGroup group)
    {
        var policy = checkpoint.GetProperty("partitionPolicy"); RequireLearningString(policy, "cohortId", cohort);
        var hash = LearningDigest(policy.GetRawText()); RequireLearningString(checkpoint, "partitionPolicyHash", hash);
        var assignment = LearningObjectHash(new SortedDictionary<string, object>(StringComparer.Ordinal)
        { ["groupKey"] = group.GroupKey, ["policyHash"] = hash, ["schema"] = "filemapper.pattern-group-assignment/1" });
        var train = policy.GetProperty("trainBuckets").GetInt32(); var validation = policy.GetProperty("validationBuckets").GetInt32(); var total = policy.GetProperty("totalBuckets").GetInt32();
        if (total != 10000 || train < 1 || validation < 1 || train > 9998 || validation >= total - train) throw new ArgumentException("Invalid sealed split policy.");
        var bucket = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(Convert.FromHexString(assignment)) % (ulong)total;
        var partition = bucket < (ulong)train ? "train" : bucket < (ulong)(train + validation) ? "validation" : "test";
        if (group.AssignmentHash != assignment || group.Partition != partition) throw new ArgumentException("Event group does not match its sealed deterministic partition.");
    }
    private static PatternLearningStoreHead ReadLearningHead(SqliteDataReader reader) => new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    private static PatternLearningStoreHead? LearningHead(SqliteConnection db, SqliteTransaction? tx, string learnerId)
    {
        using var query = LearningCommand(db, tx, "SELECT learner_id,sequence,epoch_id,checkpoint_hash,weights_hash,audit_hash FROM pl_heads WHERE learner_id=$l", ("$l", learnerId));
        using var reader = query.ExecuteReader(); return reader.Read() ? ReadLearningHead(reader) : null;
    }
    private static PatternLearningStoredEvent ReadLearningEvent(SqliteDataReader reader)
    {
        var result = new PatternLearningStoredEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
        ValidateLearningDocument(new(result.EventHash, reader.GetString(6), result.EventJson)); return result;
    }
    private static PatternLearningStoreDocument? LearningDocument(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        using var query = LearningCommand(db, tx, sql, values); using var reader = query.ExecuteReader();
        if (!reader.Read()) return null; var document = new PatternLearningStoreDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2)); ValidateLearningDocument(document); return document;
    }
    private static void InsertLearningWeights(SqliteConnection db, SqliteTransaction tx, PatternLearningStoreDocument weights)
    {
        var existing = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_weights WHERE hash=$h", ("$h", weights.Hash));
        if (existing is not null) { if (existing != weights) throw new InvalidDataException("Weights address is already occupied by different bytes."); return; }
        LearningExecute(db, tx, "INSERT INTO pl_weights VALUES($h,$s,$j)", ("$h", weights.Hash), ("$s", weights.JsonSha256), ("$j", weights.Json));
    }
    private static void InsertLearningCheckpoint(SqliteConnection db, SqliteTransaction tx, PatternLearningStoreHead head, PatternLearningStoreDocument checkpoint)
    {
        var existing = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_checkpoints WHERE learner_id=$l AND hash=$h", ("$l", head.LearnerId), ("$h", checkpoint.Hash));
        if (existing is not null) { if (existing != checkpoint) throw new InvalidDataException("Checkpoint address is occupied by different bytes."); return; }
        LearningExecute(db, tx, "INSERT INTO pl_checkpoints VALUES($l,$h,$sha,$j,$s,$e,$w,$a)", ("$l", head.LearnerId), ("$h", checkpoint.Hash), ("$sha", checkpoint.JsonSha256),
            ("$j", checkpoint.Json), ("$s", head.Sequence), ("$e", head.EpochId), ("$w", head.WeightsHash), ("$a", head.AuditHash));
    }
    private static void ValidateLearningEpoch(PatternLearningStoreEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch); LearningText(epoch.LearnerId, 256, "learnerId"); LearningText(epoch.EpochId, 256, "epochId"); LearningText(epoch.CohortId, 256, "cohortId");
        LearningHash(epoch.PartitionPolicyHash); ValidateLearningDocument(epoch.Document);
        if (epoch.StartSequence < 1 || epoch.EndSequence < epoch.StartSequence) throw new ArgumentException("Epoch has invalid boundaries.");
        if (epoch.ParentCheckpointHash is not null) LearningHash(epoch.ParentCheckpointHash);
        using var json = JsonDocument.Parse(epoch.Document.Json); var value = json.RootElement;
        RequireLearningString(value, "epochId", epoch.EpochId); RequireLearningString(value, "partitionPolicyHash", epoch.PartitionPolicyHash);
        if (value.GetProperty("startSequence").GetInt64() != epoch.StartSequence || NullableLearningLong(value.GetProperty("endSequence")) != epoch.EndSequence ||
            value.GetProperty("parentCheckpointHash").GetString() != epoch.ParentCheckpointHash) throw new ArgumentException("Epoch document differs from its registration.");
    }
    private static long? NullableLearningLong(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    private static void InsertLearningEpoch(SqliteConnection db, SqliteTransaction tx, PatternLearningStoreEpoch epoch)
    {
        var existing = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_epochs WHERE learner_id=$l AND epoch_id=$e", ("$l", epoch.LearnerId), ("$e", epoch.EpochId));
        if (existing is not null) { if (existing != epoch.Document) throw new InvalidOperationException("An epoch cannot be overwritten."); return; }
        LearningExecute(db, tx, "INSERT INTO pl_epochs VALUES($l,$e,$cohort,$policy,$start,$end,$parent,$h,$sha,$j)", ("$l", epoch.LearnerId), ("$e", epoch.EpochId),
            ("$cohort", epoch.CohortId), ("$policy", epoch.PartitionPolicyHash), ("$start", epoch.StartSequence), ("$end", epoch.EndSequence), ("$parent", epoch.ParentCheckpointHash),
            ("$h", epoch.Document.Hash), ("$sha", epoch.Document.JsonSha256), ("$j", epoch.Document.Json));
    }
    private static void InsertLearningGroup(SqliteConnection db, SqliteTransaction tx, string learnerId, PatternLearningStoreGroup group)
    {
        using var query = LearningCommand(db, tx, "SELECT partition,assignment_hash FROM pl_groups WHERE learner_id=$l AND cohort_id=$c AND group_key=$g", ("$l", learnerId), ("$c", group.CohortId), ("$g", group.GroupKey));
        using (var reader = query.ExecuteReader()) if (reader.Read())
        { if (reader.GetString(0) != group.Partition || reader.GetString(1) != group.AssignmentHash) throw new InvalidOperationException("Sealed group assignment cannot be changed."); return; }
        LearningExecute(db, tx, "INSERT INTO pl_groups VALUES($l,$c,$g,$p,$h)", ("$l", learnerId), ("$c", group.CohortId), ("$g", group.GroupKey), ("$p", group.Partition), ("$h", group.AssignmentHash));
    }
    private static void InsertLearningTerm(SqliteConnection db, SqliteTransaction tx, string learnerId, long sequence, PatternLearningStoreTerm term)
    {
        using var query = LearningCommand(db, tx, "SELECT category,token,encounter_count FROM pl_vocabulary WHERE learner_id=$l AND term_hash=$h", ("$l", learnerId), ("$h", term.TermHash));
        bool exists;
        using (var reader = query.ExecuteReader())
        {
            exists = reader.Read();
            if (exists && (reader.GetString(0) != term.Category || reader.GetString(1) != term.Token || reader.GetInt64(2) > 1_000_000_000_000 - term.Count))
                throw new InvalidDataException("Vocabulary hash collision, corrupt contents or counter bound reached.");
        }
        if (!exists)
        {
            var changed = LearningExecute(db, tx, "UPDATE pl_heads SET vocabulary_count=vocabulary_count+1 WHERE learner_id=$l AND vocabulary_count<$max", ("$l", learnerId), ("$max", LearningVocabularyLimit));
            if (changed != 1) throw new InvalidOperationException("Vocabulary reached its one-million-term limit.");
            LearningExecute(db, tx, "INSERT INTO pl_vocabulary VALUES($l,$h,$c,$t,$n,0,$s,$s)", ("$l", learnerId), ("$h", term.TermHash), ("$c", term.Category), ("$t", term.Token), ("$n", term.Count), ("$s", sequence));
        }
        else LearningExecute(db, tx, "UPDATE pl_vocabulary SET encounter_count=encounter_count+$n,last_sequence=$s WHERE learner_id=$l AND term_hash=$h", ("$l", learnerId), ("$h", term.TermHash), ("$n", term.Count), ("$s", sequence));
        LearningExecute(db, tx, "INSERT INTO pl_event_terms VALUES($l,$s,$h,$n)", ("$l", learnerId), ("$s", sequence), ("$h", term.TermHash), ("$n", term.Count));
    }
    private static string?[] LearningContext(PatternLearningStoreDocument context)
    {
        ValidateLearningDocument(context); if (LearningUtf8.GetByteCount(context.Json) > 16384) throw new ArgumentException("Occurrence context exceeds 16 KiB.");
        using var json = JsonDocument.Parse(context.Json); var result = new List<string?>();
        foreach (var key in new[] { "workspace", "repository", "task", "terminal", "computer", "network", "session" })
        {
            var value = json.RootElement.GetProperty(key); var status = value.GetProperty("status").GetString();
            var text = value.GetProperty("value").GetString(); var source = value.GetProperty("source").GetString();
            if (status == "unknown") { if (text is not null || source is not null) throw new ArgumentException("Unknown context contains an invented value/source."); }
            else if (status is "observed" or "declared") { LearningText(text, 512, key); LearningText(source, 1024, key + " source"); }
            else throw new ArgumentException("Invalid context status.");
            result.Add(text);
        }
        return result.ToArray();
    }
    private static void ValidateLearningOccurrence(PatternLearningStoreOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence); LearningText(occurrence.SourceObservationId, 256, "sourceObservationId"); LearningHash(occurrence.PatternHash);
        LearningText(occurrence.SomId, 256, "somId"); LearningText(occurrence.SourceNodeId, 256, "sourceNodeId"); LearningText(occurrence.SnapshotId, 256, "snapshotId");
        if (occurrence.ObservedFrom.HasValue && occurrence.ObservedTo.HasValue && occurrence.ObservedFrom > occurrence.ObservedTo) throw new ArgumentException("Occurrence observation range is reversed.");
        _ = LearningContext(occurrence.Context);
    }
    private static string LearningOccurrenceId(PatternLearningStoreOccurrence value) => LearningObjectHash(new
    { Schema = "filemapper.pattern-occurrence/1", Session = LearningContext(value.Context)[6], value.SourceObservationId, value.PatternHash });
    private static string LearningProjectionHash(IEnumerable<PatternLearningStoreTerm> terms, IEnumerable<PatternLearningStoreOccurrence> occurrences) => LearningObjectHash(new
    {
        Terms = terms.OrderBy(term => term.TermHash, StringComparer.Ordinal),
        Occurrences = occurrences.OrderBy(LearningOccurrenceId, StringComparer.Ordinal).Select(item => new
        { item.SourceObservationId, item.PatternHash, item.SomId, item.SourceNodeId, item.SnapshotId, item.ObservedFrom, item.ObservedTo, item.CapturedAt, ContextHash = item.Context.Hash })
    });
    private static void InsertLearningOccurrence(SqliteConnection db, SqliteTransaction tx, string learnerId, string eventId, long sequence, PatternLearningStoreOccurrence value)
    {
        var context = LearningContext(value.Context); var existing = LearningDocument(db, tx, "SELECT hash,sha,json FROM pl_contexts WHERE hash=$h", ("$h", value.Context.Hash));
        if (existing is null) LearningExecute(db, tx, "INSERT INTO pl_contexts VALUES($h,$sha,$j,$w,$r,$t,$terminal,$computer,$network,$session)",
            ("$h", value.Context.Hash), ("$sha", value.Context.JsonSha256), ("$j", value.Context.Json), ("$w", context[0]), ("$r", context[1]), ("$t", context[2]),
            ("$terminal", context[3]), ("$computer", context[4]), ("$network", context[5]), ("$session", context[6]));
        else if (existing != value.Context) throw new InvalidDataException("Context hash address contains different bytes.");
        var parameters = new (string, object?)[] { ("$l", learnerId), ("$o", value.SourceObservationId), ("$p", value.PatternHash), ("$som", value.SomId),
            ("$node", value.SourceNodeId), ("$snapshot", value.SnapshotId), ("$from", value.ObservedFrom?.ToString("O", CultureInfo.InvariantCulture)),
            ("$to", value.ObservedTo?.ToString("O", CultureInfo.InvariantCulture)), ("$fromTicks", value.ObservedFrom?.UtcTicks), ("$toTicks", value.ObservedTo?.UtcTicks) };
        using (var source = LearningCommand(db, tx, "SELECT som_id,source_node_id,snapshot_id,observed_from,observed_to FROM pl_sources WHERE learner_id=$l AND source_observation_id=$o AND pattern_hash=$p", parameters))
        using (var reader = source.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != value.SomId || reader.GetString(1) != value.SourceNodeId || reader.GetString(2) != value.SnapshotId ||
                    LearningDate(reader, 3) != value.ObservedFrom || LearningDate(reader, 4) != value.ObservedTo)
                    throw new InvalidOperationException("A source observation cannot change under the same identity.");
            }
            else
            {
                reader.Close();
                if (LearningExecute(db, tx, "UPDATE pl_heads SET source_count=source_count+1 WHERE learner_id=$l AND source_count<$max", ("$l", learnerId), ("$max", LearningSourceLimit)) != 1)
                    throw new InvalidOperationException("Learner reached its source-observation projection limit.");
                LearningExecute(db, tx, "INSERT INTO pl_sources VALUES($l,$o,$p,$som,$node,$snapshot,$from,$to,$fromTicks,$toTicks)", parameters);
                LearningExecute(db, tx, "UPDATE pl_vocabulary SET source_count=source_count+1 WHERE learner_id=$l AND term_hash=$p", ("$l", learnerId), ("$p", value.PatternHash));
            }
        }
        var occurrenceId = LearningOccurrenceId(value);
        LearningExecute(db, tx, "INSERT OR IGNORE INTO pl_occurrences VALUES($l,$id,$session,$o,$p)", ("$l", learnerId), ("$id", occurrenceId),
            ("$session", context[6] is null ? "unknown" : "known:" + context[6]), ("$o", value.SourceObservationId), ("$p", value.PatternHash));
        LearningExecute(db, tx, "INSERT INTO pl_encounters VALUES($l,$e,$s,$id,$c,$captured,$ticks)", ("$l", learnerId), ("$e", eventId), ("$s", sequence),
            ("$id", occurrenceId), ("$c", value.Context.Hash), ("$captured", value.CapturedAt.ToString("O", CultureInfo.InvariantCulture)), ("$ticks", value.CapturedAt.UtcTicks));
    }
    private static DateTimeOffset? LearningDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
}
