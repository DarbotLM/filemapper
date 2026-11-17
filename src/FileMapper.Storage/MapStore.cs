using System.Globalization;
using System.Text.Json;
using FileMapper.Domain;
using Microsoft.Data.Sqlite;

namespace FileMapper.Storage;

/// <summary>Snapshot-scoped durable graph. Every batch commits atomically; incomplete snapshots retain their evidence.</summary>
public sealed partial class MapStore(string databasePath, string? installerToken = null, bool readOnly = false)
{
    public string DatabasePath { get; } = ResolveDatabase(databasePath, readOnly);
    public bool IsReadOnly { get; } = readOnly;
    private static string ResolveDatabase(string path, bool readOnly)
    {
        path = System.IO.Path.GetFullPath(path);
        if (readOnly && !File.Exists(path)) throw new FileNotFoundException("An existing database is required for read-only access.", path);
        return path;
    }
    private void RequireWritable()
    {
        if (IsReadOnly) throw new InvalidOperationException("This database handle is read-only.");
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath,
            Mode = IsReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = !IsReadOnly, DefaultTimeout = IsReadOnly ? 5 : 30 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // ReadOnly protects the main database. Do not set query_only: existing comparison queries
        // materialize connection-local TEMP tables, which query_only would also prohibit.
        command.CommandText = IsReadOnly ? "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
            : "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        RequireWritable();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DatabasePath)!);
        if(File.Exists(DatabasePath))
        using(var existing=Open())
        using(var probe=Command(existing,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('schema_versions','snapshots','nodes','entries','edges','observations','findings')"))
            if((long)probe.ExecuteScalar()! == 7)
            {
                using var version=Command(existing,"SELECT COUNT(*) FROM schema_versions WHERE version=1");
                if((long)version.ExecuteScalar()! == 1)return;
            }
        using var ownership=AcquireWriter(allowInstallerSession:true);
        using var db = Open();
        using(var journal=Command(db,"PRAGMA journal_mode=WAL;"))journal.ExecuteNonQuery();
        using var migration=db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction=migration;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_versions(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
            INSERT OR IGNORE INTO schema_versions VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            CREATE TABLE IF NOT EXISTS snapshots(
              id TEXT PRIMARY KEY, root TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
              status TEXT NOT NULL, completeness TEXT NOT NULL, entry_count INTEGER NOT NULL DEFAULT 0,
              file_count INTEGER NOT NULL DEFAULT 0, directory_count INTEGER NOT NULL DEFAULT 0,
              logical_bytes INTEGER NOT NULL DEFAULT 0, allocated_bytes INTEGER, error_count INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS nodes(snapshot_id TEXT NOT NULL REFERENCES snapshots(id), id TEXT NOT NULL,
              kind TEXT NOT NULL, name TEXT NOT NULL, path TEXT, properties TEXT NOT NULL, PRIMARY KEY(snapshot_id,id));
            CREATE INDEX IF NOT EXISTS ix_nodes_kind ON nodes(snapshot_id,kind,name,id);
            CREATE TABLE IF NOT EXISTS entries(snapshot_id TEXT NOT NULL REFERENCES snapshots(id),path TEXT NOT NULL,
              parent_path TEXT,node_id TEXT NOT NULL,name TEXT NOT NULL,kind TEXT NOT NULL,
              logical_bytes INTEGER NOT NULL CHECK(logical_bytes>=0),allocated_bytes INTEGER CHECK(allocated_bytes>=0),
              modified_at TEXT NOT NULL,attributes TEXT NOT NULL,link_count INTEGER NOT NULL,owner TEXT,hash TEXT,
              PRIMARY KEY(snapshot_id,path));
            CREATE INDEX IF NOT EXISTS ix_entries_parent ON entries(snapshot_id,parent_path,kind,name,path);
            CREATE INDEX IF NOT EXISTS ix_entries_identity ON entries(snapshot_id,node_id);
            CREATE INDEX IF NOT EXISTS ix_entries_size ON entries(snapshot_id,kind,logical_bytes DESC,path);
            CREATE INDEX IF NOT EXISTS ix_entries_allocated ON entries(snapshot_id,kind,allocated_bytes DESC,path);
            CREATE INDEX IF NOT EXISTS ix_entries_age ON entries(snapshot_id,kind,modified_at,path);
            CREATE INDEX IF NOT EXISTS ix_entries_path_fold ON entries(snapshot_id,path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_entries_hash ON entries(snapshot_id,hash,logical_bytes);
            CREATE TABLE IF NOT EXISTS edges(snapshot_id TEXT NOT NULL REFERENCES snapshots(id),id TEXT NOT NULL,
              from_id TEXT NOT NULL,to_id TEXT NOT NULL,kind TEXT NOT NULL,evidence_id TEXT NOT NULL,
              confidence REAL NOT NULL,properties TEXT,PRIMARY KEY(snapshot_id,id));
            CREATE INDEX IF NOT EXISTS ix_edges_from ON edges(snapshot_id,from_id,kind,to_id);
            CREATE INDEX IF NOT EXISTS ix_edges_to ON edges(snapshot_id,to_id,kind,from_id);
            CREATE TABLE IF NOT EXISTS observations(snapshot_id TEXT NOT NULL REFERENCES snapshots(id),id TEXT NOT NULL,
              subject_id TEXT NOT NULL,predicate TEXT NOT NULL,error_code TEXT,completeness TEXT NOT NULL,
              observed_at TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(snapshot_id,id));
            CREATE INDEX IF NOT EXISTS ix_observations_subject ON observations(snapshot_id,subject_id,id);
            CREATE TABLE IF NOT EXISTS findings(snapshot_id TEXT NOT NULL REFERENCES snapshots(id),id TEXT NOT NULL,
              rule_id TEXT NOT NULL,severity TEXT NOT NULL,path TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(snapshot_id,id));
            CREATE INDEX IF NOT EXISTS ix_findings_rule ON findings(snapshot_id,rule_id,severity,id);
            """;
        cmd.ExecuteNonQuery();
        migration.Commit();
    }

    public Snapshot CreateSnapshot(string root)
    {
        var snapshot = new Snapshot(Guid.NewGuid().ToString("N"), System.IO.Path.GetFullPath(root), DateTimeOffset.UtcNow, null, "running", "incomplete", 0, 0, 0, 0, null, 0);
        using var db = Open();
        using var cmd = Command(db, "INSERT INTO snapshots(id,root,started_at,status,completeness) VALUES($id,$root,$time,'running','incomplete')", ("$id",snapshot.Id),("$root",snapshot.Root),("$time",snapshot.StartedAt.ToString("O")));
        cmd.ExecuteNonQuery();
        return snapshot;
    }

    public void Ingest(string snapshotId, CollectionBatch batch)
    {
        RequireWritable();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var state = ValidateSnapshotIngestion(db, transaction, snapshotId, batch.Entries.Count != 0, batch.Observations, batch.Nodes);
        IngestGraph(db,transaction,snapshotId,batch.Nodes,batch.Edges,batch.Observations);
        using var entry = Command(db,"""
            INSERT INTO entries VALUES($s,$p,$pp,$id,$n,$k,$l,$a,$m,$at,$lc,$o,$h)
            ON CONFLICT(snapshot_id,path) DO UPDATE SET parent_path=excluded.parent_path,node_id=excluded.node_id,
            name=excluded.name,kind=excluded.kind,logical_bytes=excluded.logical_bytes,allocated_bytes=excluded.allocated_bytes,
            modified_at=excluded.modified_at,attributes=excluded.attributes,link_count=excluded.link_count,owner=excluded.owner,hash=excluded.hash
            """);
        entry.Transaction = transaction;
        foreach(var item in batch.Entries)
        {
            if(item.SnapshotId != snapshotId) throw new ArgumentException("Entry belongs to a different snapshot.",nameof(batch));
            Set(entry,("$s",snapshotId),("$p",item.Path),("$pp",item.ParentPath),("$id",item.NodeId),("$n",item.Name),("$k",item.Kind),
                ("$l",item.LogicalBytes),("$a",item.AllocatedBytes),("$m",item.ModifiedAt.ToString("O")),("$at",item.Attributes),("$lc",item.LinkCount),("$o",item.Owner),("$h",item.Hash));
            entry.ExecuteNonQuery();
        }
        if (state != "running" && batch.Observations.Count != 0) RefreshObservationState(db, transaction, snapshotId);
        transaction.Commit();
    }

    private static void IngestGraph(SqliteConnection db, SqliteTransaction tx,string snapshotId,IReadOnlyList<MapNode> nodes,IReadOnlyList<MapEdge> edges,IReadOnlyList<Evidence> observations)
    {
        using var node = Command(db,"INSERT INTO nodes VALUES($s,$id,$k,$n,$p,$props) ON CONFLICT(snapshot_id,id) DO UPDATE SET kind=excluded.kind,name=excluded.name,path=excluded.path,properties=excluded.properties WHERE nodes.kind='ExternalReference' OR excluded.kind<>'ExternalReference'");
        node.Transaction=tx;
        foreach(var item in nodes)
        {
            Set(node,("$s",snapshotId),("$id",item.Id),("$k",item.Kind),("$n",item.Name),("$p",item.Path),("$props",JsonSerializer.Serialize(item.Properties,Json)));
            node.ExecuteNonQuery();
        }
        using var edge=Command(db,"INSERT INTO edges VALUES($s,$id,$f,$t,$k,$e,$c,$p) ON CONFLICT(snapshot_id,id) DO NOTHING");
        edge.Transaction=tx;
        foreach(var item in edges)
        {
            Set(edge,("$s",snapshotId),("$id",item.Id),("$f",item.FromId),("$t",item.ToId),("$k",item.Kind),("$e",item.EvidenceId),("$c",item.Confidence),("$p",item.Properties is null ? null : JsonSerializer.Serialize(item.Properties,Json)));
            edge.ExecuteNonQuery();
        }
        using var observation=Command(db,"INSERT INTO observations VALUES($s,$id,$subject,$p,$error,$complete,$time,$payload) ON CONFLICT(snapshot_id,id) DO NOTHING");
        observation.Transaction=tx;
        foreach(var item in observations)
        {
            if(item.SnapshotId!=snapshotId) throw new ArgumentException("Observation belongs to a different snapshot.");
            Set(observation,("$s",snapshotId),("$id",item.Id),("$subject",item.SubjectId),("$p",item.Predicate),("$error",item.ErrorCode),("$complete",item.Completeness),("$time",item.ObservedAt.ToString("O")),("$payload",JsonSerializer.Serialize(item,Json)));
            observation.ExecuteNonQuery();
        }
    }

    public void FinishSnapshot(string id,string status="completed")
    {
        RequireWritable();
        if(status is not ("completed" or "cancelled" or "failed" or "paused")) throw new ArgumentException("Invalid terminal snapshot status.",nameof(status));
        using var db=Open();
        using var tx=db.BeginTransaction();
        if (HasScanJob(db, tx, id)) throw new InvalidOperationException("Finalize a scan through FinishScanJob so its job and snapshot remain atomic.");
        FinishSnapshotCore(db, tx, id, status, DateTimeOffset.UtcNow);
        tx.Commit();
    }

    public IReadOnlyList<Snapshot> ListSnapshots()
    {
        using var db=Open(); using var cmd=Command(db,"SELECT * FROM snapshots ORDER BY started_at DESC,id DESC");
        using var reader=cmd.ExecuteReader(); var result=new List<Snapshot>();
        while(reader.Read()) result.Add(ReadSnapshot(reader)); return result;
    }
    public Snapshot? GetSnapshot(string id)
    {
        using var db=Open(); using var cmd=Command(db,"SELECT * FROM snapshots WHERE id=$id",("$id",id));
        using var reader=cmd.ExecuteReader(); return reader.Read() ? ReadSnapshot(reader) : null;
    }
    public Page<FileEntry> GetEntries(string id,string? parent=null,string? search=null,int offset=0,int limit=100,CancellationToken cancellationToken=default)
    {
        ValidatePage(offset,limit);
        var where="snapshot_id=$s";
        if(parent is not null) where+=" AND parent_path=$p";
        if(!string.IsNullOrEmpty(search)) where+=" AND (name LIKE $q ESCAPE '!' OR path LIKE $q ESCAPE '!')";
        using var db=Open();
        using var cancellation=new SourceSqliteCancellation(db,cancellationToken);
        return cancellation.Run(() =>
        {
            using var tx=db.BeginTransaction(deferred:true);
            using var count=Command(db,"SELECT COUNT(*) FROM entries WHERE "+where,("$s",id),("$p",parent),("$q",Like(search)));count.Transaction=tx;
            var total=(long)count.ExecuteScalar()!;
            using var cmd=Command(db,"SELECT * FROM entries WHERE "+where+" ORDER BY CASE kind WHEN 'Directory' THEN 0 ELSE 1 END,name COLLATE NOCASE,path LIMIT $l OFFSET $o",("$s",id),("$p",parent),("$q",Like(search)),("$l",limit),("$o",offset));cmd.Transaction=tx;
            using var reader=cmd.ExecuteReader();var result=new List<FileEntry>();
            while(reader.Read()) result.Add(ReadEntry(reader));return new Page<FileEntry>(result,total,offset,limit);
        });
    }
    /// <summary>A forward-only database cursor, without OFFSET rescans or an in-memory projection cap.</summary>
    public IEnumerable<FileEntry> StreamEntries(string snapshotId,CancellationToken cancellationToken=default)
    {
        using var db=Open();using var cmd=Command(db,"SELECT * FROM entries WHERE snapshot_id=$s ORDER BY path",("$s",snapshotId));
        using var reader=cmd.ExecuteReader();
        while(reader.Read()) { cancellationToken.ThrowIfCancellationRequested();yield return ReadEntry(reader); }
    }
    /// <summary>Ranks distinct file identities; retains a deterministic representative path for hardlinks.</summary>
    public IReadOnlyList<FileEntry> RankEntries(string snapshotId,string by,int limit=100,CancellationToken cancellationToken=default)
    {
        ValidatePage(0,limit);
        var order=by switch { "logical"=>"e.logical_bytes DESC", "allocated"=>"e.allocated_bytes DESC", "age"=>"e.modified_at ASC", _=>throw new ArgumentException("Ranking must be logical, allocated, or age.",nameof(by)) };
        using var db=Open();using var cancellation=new SourceSqliteCancellation(db,cancellationToken);
        return cancellation.Run<IReadOnlyList<FileEntry>>(() =>
        {
        using var cmd=Command(db,"SELECT e.* FROM entries e WHERE e.snapshot_id=$s AND e.kind='File'"+
            (by=="allocated"?" AND e.allocated_bytes IS NOT NULL":"")+
            " AND e.path=(SELECT MIN(other.path) FROM entries other INDEXED BY ix_entries_identity WHERE other.snapshot_id=e.snapshot_id AND other.node_id=e.node_id) ORDER BY "+order+",e.path LIMIT $l",("$s",snapshotId),("$l",limit));
        using var reader=cmd.ExecuteReader();var result=new List<FileEntry>();
        while(reader.Read())result.Add(ReadEntry(reader));return result;
        });
    }
    public Page<MapNode> GetNodes(string id,string? kind=null,string? search=null,int offset=0,int limit=100)
    {
        ValidatePage(offset,limit);
        var where="snapshot_id=$s";
        if(kind is not null) where+=" AND kind=$k";
        if(!string.IsNullOrEmpty(search)) where+=" AND (name LIKE $q ESCAPE '!' OR path LIKE $q ESCAPE '!')";
        using var db=Open();
        using var count=Command(db,"SELECT COUNT(*) FROM nodes WHERE "+where,("$s",id),("$k",kind),("$q",Like(search)));
        var total=(long)count.ExecuteScalar()!;
        using var cmd=Command(db,"SELECT * FROM nodes WHERE "+where+" ORDER BY kind,name,id LIMIT $l OFFSET $o",("$s",id),("$k",kind),("$q",Like(search)),("$l",limit),("$o",offset));
        using var reader=cmd.ExecuteReader();var result=new List<MapNode>();
        while(reader.Read()) result.Add(ReadNode(reader));return new(result,total,offset,limit);
    }
    public MapNode? GetNode(string snapshotId,string nodeId)
    {
        using var db=Open();using var cmd=Command(db,"SELECT * FROM nodes WHERE snapshot_id=$s AND id=$id",("$s",snapshotId),("$id",nodeId));
        using var reader=cmd.ExecuteReader();return reader.Read()?ReadNode(reader):null;
    }
    public IReadOnlyList<MapEdge> GetEdges(string id,string? nodeId=null,int limit=200)
    {
        ValidatePage(0,limit);
        using var db=Open();using var cmd=Command(db,"SELECT * FROM edges WHERE snapshot_id=$s"+(nodeId is null ? "" : " AND (from_id=$n OR to_id=$n)")+" ORDER BY kind,from_id,to_id,id LIMIT $l",("$s",id),("$n",nodeId),("$l",limit));
        using var reader=cmd.ExecuteReader();var result=new List<MapEdge>();
        while(reader.Read()) result.Add(new(reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetDouble(6),reader.IsDBNull(7)?null:JsonSerializer.Deserialize<Dictionary<string,object?>>(reader.GetString(7),Json)));
        return result;
    }
    public IReadOnlyList<Evidence> GetObservations(string id,string? subjectId=null,int limit=100)
    {
        ValidatePage(0,limit);
        using var db=Open();using var cmd=Command(db,"SELECT payload FROM observations WHERE snapshot_id=$s"+(subjectId is null?"":" AND subject_id=$n")+" ORDER BY observed_at,id LIMIT $l",("$s",id),("$n",subjectId),("$l",limit));
        using var reader=cmd.ExecuteReader();var result=new List<Evidence>();
        while(reader.Read()) result.Add(JsonSerializer.Deserialize<Evidence>(reader.GetString(0),Json)!);return result;
    }
    public ScanOptions? GetScanOptions(string snapshotId)
    {
        using var db=Open();using var tx=db.BeginTransaction(deferred:true);
        return ProjectionScanOptions(db, tx, snapshotId);
    }
    public void SaveAnalysis(string id,AnalysisResult analysis)
    {
        RequireWritable();
        using var db=Open();using var tx=db.BeginTransaction();
        ValidateSnapshotIngestion(db, tx, id, false, analysis.Observations, analysis.Nodes);
        IngestGraph(db,tx,id,analysis.Nodes,analysis.Edges,analysis.Observations);
        LinkSourceLocations(db,tx,id,analysis);
        ValidateFindingWitnesses(db,tx,id,analysis.Findings);
        using var cmd=Command(db,"INSERT INTO findings VALUES($s,$id,$r,$severity,$path,$payload) ON CONFLICT(snapshot_id,id) DO UPDATE SET payload=excluded.payload");cmd.Transaction=tx;
        foreach(var finding in analysis.Findings)
        {
            Set(cmd,("$s",id),("$id",finding.Id),("$r",finding.RuleId),("$severity",finding.Severity),("$path",finding.Path),("$payload",JsonSerializer.Serialize(finding,Json)));cmd.ExecuteNonQuery();
        }
        using var completeness=Command(db,"""
            UPDATE snapshots SET
              completeness=CASE WHEN EXISTS(SELECT 1 FROM observations WHERE snapshot_id=$s AND completeness<>'complete') THEN 'incomplete' ELSE completeness END,
              error_count=(SELECT COUNT(*) FROM observations WHERE snapshot_id=$s AND error_code IS NOT NULL)
            WHERE id=$s
            """,("$s",id));
        completeness.Transaction=tx;completeness.ExecuteNonQuery();
        tx.Commit();
    }
    private static void LinkSourceLocations(SqliteConnection db,SqliteTransaction tx,string snapshotId,AnalysisResult analysis)
    {
        using var lookup=Command(db,"SELECT path,node_id FROM entries WHERE snapshot_id=$s AND path=$p"+(OperatingSystem.IsWindows()?" COLLATE NOCASE":"")+" AND kind='File' LIMIT 1");lookup.Transaction=tx;
        foreach(var source in analysis.Nodes.Where(n=>n.Kind=="SourceDocument" && n.Path is not null))
        {
            Set(lookup,("$s",snapshotId),("$p",source.Path));
            string path;string physicalId;
            using(var reader=lookup.ExecuteReader()) {if(!reader.Read()) continue;path=reader.GetString(0);physicalId=reader.GetString(1);}
            var locationId=Identity.For("location",OperatingSystem.IsWindows()?path.ToUpperInvariant():path);
            var fact=new Evidence(Identity.For("observation",snapshotId+"|source-location|"+source.Id),snapshotId,source.Id,"source.location",path,"location-linker",path,DateTimeOffset.UtcNow,.5);
            var edge=new MapEdge(Identity.For("edge",source.Id+"|LOCATED_AT|"+locationId),source.Id,locationId,"LOCATED_AT",fact.Id,.5,new() {["match"]="snapshot-path-only",["physicalNodeId"]=physicalId,["contentIdentityVerified"]=false});
            var location=new MapNode(locationId,"Location",System.IO.Path.GetFileName(path),path,new() {["evidenceId"]=fact.Id});
            IngestGraph(db,tx,snapshotId,[location],[edge],[fact]);
        }
    }
    public IReadOnlyList<Finding> GetFindings(string id)
    {
        using var db=Open();using var cmd=Command(db,"SELECT payload FROM findings WHERE snapshot_id=$s ORDER BY severity,rule_id,path,id",("$s",id));
        using var reader=cmd.ExecuteReader();var result=new List<Finding>();
        while(reader.Read()) result.Add(JsonSerializer.Deserialize<Finding>(reader.GetString(0),Json)!);return result;
    }
    private static string Like(string? s)=>"%"+(s??"").Replace("!","!!",StringComparison.Ordinal).Replace("%","!%",StringComparison.Ordinal).Replace("_","!_",StringComparison.Ordinal)+"%";
    private static void ValidatePage(int offset,int limit) { if(offset<0 || limit<1 || limit>10000) throw new ArgumentOutOfRangeException(nameof(limit),"Offset must be nonnegative; limit must be 1–10000."); }
    private static SqliteCommand Command(SqliteConnection db,string sql,params (string,object?)[] values) { var cmd=db.CreateCommand();cmd.CommandText=sql;Set(cmd,values);return cmd; }
    private static void Set(SqliteCommand cmd,params (string,object?)[] values) { foreach(var (key,value) in values) { if(cmd.Parameters.Contains(key)) cmd.Parameters[key].Value=value??DBNull.Value; else cmd.Parameters.AddWithValue(key,value??DBNull.Value); } }
    private static DateTimeOffset Date(string s)=>DateTimeOffset.Parse(s,CultureInfo.InvariantCulture);
    private static Snapshot ReadSnapshot(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),Date(r.GetString(2)),r.IsDBNull(3)?null:Date(r.GetString(3)),r.GetString(4),r.GetString(5),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9),r.IsDBNull(10)?null:r.GetInt64(10),r.GetInt64(11));
    private static FileEntry ReadEntry(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6),r.IsDBNull(7)?null:r.GetInt64(7),Date(r.GetString(8)),r.GetString(9),r.GetInt64(10),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetString(12));
    private static MapNode ReadNode(SqliteDataReader r)=>new(r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),JsonSerializer.Deserialize<Dictionary<string,object?>>(r.GetString(5),Json)!);
}
