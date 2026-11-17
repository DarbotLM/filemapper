# Repeatable acceptance datasets

`FileMapper.Acceptance` exercises production collection, persistence and query APIs. It creates fixtures only in a new or empty caller-selected directory. Fixtures, databases, observations and `report.json` remain available for independent review. It never executes the source code being analyzed.

```powershell
dotnet run --project benchmarks/FileMapper.Acceptance -c Release -- filesystem --output artifacts/acceptance/filesystem
dotnet run --project benchmarks/FileMapper.Acceptance -c Release -- semantic --output artifacts/acceptance/semantic
dotnet run --project benchmarks/FileMapper.Acceptance -c Release -- storage --output artifacts/acceptance/storage --files 100000 --samples 30
```

Use distinct output directories for subsequent runs. `filesystem` requires Windows/NTFS and creates directory junctions, hardlinks, alternate streams, sparse/compressed files, a long path and an access-denied directory. The denial ACL is restored in `finally`. Never recursively remove retained link fixtures with a utility that follows reparse targets. The test uses observed-path exclusions to distinguish a noncyclic inside-root junction from its excluded direct path. These exclusions are not a canonical-target denylist.

`semantic` performs a hashed scan, immutable analysis, saved witness explanation and SARIF export for safe/vulnerable C# and PowerShell variants. It verifies source mutation rejection and atomic rollback of malformed routes, absent evidence, wrong spans and inconsistent truncation. The detailed language corpora remain under `src/FileMapper.Extract.CSharp/Acceptance` and `eng/PowerShell.Acceptance`.

`storage` generates deterministic synthetic identities and entries through public ingestion. File lengths repeat 1–4096 bytes; each 1,000-file directory and seven owner groups are deterministic. One percent receive explicitly synthetic hashes for duplicate grouping. No direct SQL inserts replace production ingestion. The independently computed truth includes logical bytes and the actual graph-node count: files + ceiling(files / 1,000) + 1. `--nodes 10000000` therefore requests **9,990,008 files, 9,991 directories plus the root**, exactly ten million nodes; `--files 10000000` instead yields 10,010,001 nodes. Unrepresentable exact node counts are rejected.

Population and query phases can be separated without running expensive facets first:

```powershell
# Run from the recorded source checkout; use the built acceptance DLL for timed work.
dotnet path/to/FileMapper.Acceptance.dll storage --output artifacts/storage-10m-population --nodes 10000000 --phases none --source-root .
# Retained data is read-only by default: no Initialize, migrations or persistent cache fills.
dotnet path/to/FileMapper.Acceptance.dll storage --output artifacts/storage-10m-readonly --nodes 10000000 --existing-database artifacts/storage-10m-population/storage.db --phases root-hierarchy,logical-ranking --samples 30 --warmups 1 --source-root .
# Optional cache-building experiment: use a preserved COPY, and explicitly disclose writes.
dotnet path/to/FileMapper.Acceptance.dll storage --output artifacts/storage-10m-cache --nodes 10000000 --existing-database artifacts/storage-10m-copy/storage.db --allow-cache-writes --phases root-aggregate,root-hierarchy,logical-ranking --samples 30 --source-root .
```

`--phases` accepts `all` (the backward-compatible default), `none`, or an ordered, nonduplicated subset of `root-aggregate,root-hierarchy,logical-ranking,duplicates,owner-breakdown`. `root-aggregate` makes one measured invocation; other selected phases retain a separate first invocation, `--warmups` (default 1), then `--samples` measured calls (default 30). One-sample diagnostic runs are allowed but do not establish a p95 gate. Omitted phases are unmeasured, not passing. The measured distribution retains chronological values; p95 uses nearest rank and the even median averages the middle two values. Validation and response digests run outside the stopwatch, but their effects on subsequent GC/cache state remain part of this benchmark protocol.

No invocation is described as OS-cold: database hashing and population warm file caches, and preceding phases can populate persistent query caches. A fresh process alone does not evict the OS cache. Read-only reuse can read previously populated caches and must be labelled accordingly. Explicit writable reuse may create indexes/cache tables or populate metrics; preserve the original database and its before/after fingerprint. SQLite read-only mode can use shared-memory coordination; it is not a claim that no sidecar bytes change. Use quiescent retained fixtures, not a concurrently changing production database.

The runner retains flushed `storage-events.ndjson` (100,000-file committed progress and phase completion), `storage-samples.ndjson` (each first/warmup/measured invocation, response digest and failure), and `storage-memory.ndjson` (UTC/elapsed time, phase, working set, private bytes, managed heap and CPU). Memory sampling defaults to 1,000 ms and accepts 100–5,000 ms; sampled peaks can miss short intervals, so the OS-reported peak is distinct. Logs are streamed, not accumulated for the lifetime of a ten-million-node ingestion. The report includes main database/WAL lengths and SHA-256 before/after, source checkout revision and dirty status when `--source-root` is provided, hashes of benchmark source files, actual FileMapper/SQLite binaries and the host executable. A benchmark overlay can make an otherwise frozen baseline checkout dirty: the recorded revision alone does not certify an unmodified product or reproducible build.

Cancellation is observed between synchronous public operations and during generation. The harness does not forcibly interrupt legacy SQL. Completed samples and progress survive cancellation/failure, and `report.json` records a failed suite; abrupt process termination may leave only the flushed logs. Such a run supplies neither a complete sample distribution nor a completed performance gate.

The retained historical 100,000-file database is 256,581,632 bytes (~244.7 MiB). A purely linear disk planning estimate is ~25.66 GB for ten million files before WAL, temporary work and copies; reserve at least 60–80 GB for a single evolving dataset, and additional space for preserved copies. This is not a storage-size guarantee: path lengths, schema/indexes and cache state affect size. The historical population timing was not retained, so it cannot justify a population-time estimate. Its completed warm 100k owner-facet p95 (~2.2 s) is a reason to select phases, not a linear runtime forecast. Use the new committed-progress records to estimate completion only after an actual run starts.

This benchmark does **not** measure native scan throughput or the separate metadata-scan memory target under 1.5 GB. It reports observed under-300 ms hierarchy/ranking p95, actual node count and sample count separately, leaving performance-gate acceptance false for independent review. Reference-machine certification, controlled background load, cold-cache protocol and incremental refresh require their own evidence. Historical reports remain unchanged.

## Reference-hardware report template

Copy this template into an execution report and fill measured values. Do not mark hardware certified because a run merely completed.

| Field | Observed value |
| --- | --- |
| Source revision / dirty changes | |
| OS / architecture / runtime / SDK | |
| CPU model / physical and logical cores | |
| Installed RAM / available RAM | |
| Storage model / interface / filesystem / free space | |
| Power mode / background workload / antivirus exclusions | |
| Dataset generator version / seed / real corpus description | |
| Actual files / identities / graph nodes / snapshots | |
| Cold cache method / warm-up / sample count | |
| Population duration / metadata scan entries per second | |
| Peak and steady-state memory / database size | |
| Hierarchy / ranking / duplicate / facet min, median, p95, max | |
| Incremental change size / refresh p95 | |
| Correctness checks / retained report and DB paths | |
| Reference hardware certified / reviewer / limitations | No, until verified |

Native allocation uses [Microsoft FILE_COMPRESSION_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_compression_info) for sparse/compressed streams on the same identity handle. SARIF witness export follows [OASIS SARIF 2.1 code flows](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html).
