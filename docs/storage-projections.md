# Native storage projections

FileMapper persists completed-snapshot storage aggregates in the same SQLite database as their source entries. Model `filesystem-storage-projection/3` covers extension, owner and age facets, ordinary-stream duplicate groups and member paths, plus versioned directory metrics. Queries retain exact totals and pagination across the full admitted snapshot or requested subtree. There is no entry-prefix cutoff.

This implementation is part of the production scaling campaign. Constructing a projection does not establish the ten-million-node performance gate. Cold preparation, subsequent queries, process memory and storage growth require separate measurements.

## Scope and values

Facet scopes follow recorded `parent_path` relationships from the requested path. They use snapshot observations without reading the current filesystem. The requested path must exist in the snapshot. Each physical file or alternate-stream identity contributes logical bytes once; path counts retain all observed aliases in that scope.

| Projection | Identity and uncertainty rules |
| --- | --- |
| Extension / `type` | Case-normalized final extension. A leading-dot filename without another dot has no extension. Aliases with different extensions produce `(multiple extensions)`. Alternate streams have their own bucket. |
| Owner | Every alias must agree on a non-null owner to use that bucket. All missing owners produce `(unknown owner)`; partial or conflicting ownership produces `(mixed or unknown owners)`. |
| Age | Newest observed alias modification time, relative to snapshot start. Future and unknown dates have explicit buckets. This reference time does not advance with the wall clock. |
| Allocation | Known only when every alias has a value and all agree. A group's allocated total is null if any identity is unknown. Its known subtotal includes only identities with known allocation. |
| Metadata conflicts | Logical-size, known-allocation, owner, timestamp or kind disagreements mark affected facet groups and the entire breakdown incomplete, including conflicts outside the requested page. |
| Directory metrics | Scope-local deduplication includes separate alternate-stream identities. Logical or known-allocation disagreement independently marks the result incomplete, including on older snapshots whose recorded status says complete. |

Scope-local grouping matters. A hardlinked identity observed as `a/x.txt` and `b/y.bin` belongs to `(multiple extensions)` at their common ancestor, `.txt` under `a`, and `.bin` under `b`. A global facet label cannot replace either child scope's label.

Directory exclusive allocation requires complete snapshot coverage and a single valid `skip` or `reference` reparse policy. Unknown allocation, fallback identities, nonpositive/inconsistent link counts or more observed names than recorded links make the estimate unknown. Identities with additional names outside the requested scope contribute zero to its exclusive total. Logical/allocation contradictions suppress the estimate.

Duplicate grouping uses existing `entries.hash` observations. Every file alias of an identity must have the same non-null hash and logical size; a group needs at least two physical identities. This reports `sha256-unnamed-stream-at-observation` evidence. [Staged content analysis](staged-hashing.md) has separate runs, evidence and content-defined chunks.

A numeric duplicate reclaimability estimate additionally requires known allocations, positive consistent link counts with exactly that many observed names, no fallback identities, no alternate stream under any member alias, complete snapshot coverage, and the single valid safe reparse policy. It subtracts the largest identity allocation from the group sum. This estimate assumes removal of every redundant identity's names while retaining the largest-allocation identity; it does not authorize a file action. Missing or ambiguous policy makes savings unknown.

## Preparation and reads

The native `MapStore` API exposes:

~~~csharp
StorageProjectionPreparation PrepareStorageProjections(
    string snapshotId, string? path = null,
    CancellationToken cancellationToken = default);
~~~

The default path is the snapshot root. An explicit call prepares all three facets for that exact scope and global duplicate data for the snapshot. Its receipt includes `ModelVersion`, `FacetsBuilt`, `DuplicatesBuilt`, exact `FacetRows`, `DuplicateGroups` and `DuplicateMembers`, and the preparation start/finish interval. Repeating valid preparation reports both build flags as false.

Preparation requires a writable store and acquires the cross-process writer lease and maintenance admission. Call it after releasing a scan's writer lease; that lease is not reentrant. It rejects unfinished snapshots. `InitializeProjections()` installs schema and supporting indexes only; it does not prepare every retained snapshot.

~~~csharp
var store = new MapStore(existingDatabasePath);
var prepared = store.PrepareStorageProjections(snapshotId, cancellationToken: token);
var facets = store.GetStorageBreakdown(snapshotId, "owner", offset: 0, limit: 100,
    cancellationToken: token);
var groups = store.GetDuplicateGroups(snapshotId, offset: 0, limit: 100,
    memberLimit: 1000, cancellationToken: token);
~~~

`GetStorageBreakdown`, `GetDuplicateGroups`, `GetDuplicateMembers`, `GetDirectoryMetrics` and `CountHashedFiles` accept an optional final cancellation token. Existing pagination remains: storage pages admit limits 1–10,000, and the initial duplicate member preview has its own bound. `GetDuplicateMembers` exposes all subsequent members with the full total. Managed result construction is bounded by those caller-selected page sizes.

| Store and source state | Read behavior |
| --- | --- |
| Valid prepared completed snapshot | Indexed pages with stored full totals. Counts, page rows, current snapshot metadata and policy share one SQLite read transaction. |
| Writable completed snapshot, missing projection | The first facet or duplicate query attempts cold preparation for that projection. Facet preparation builds all three dimensions for the exact scope. Directory metrics prepare only the requested scope. |
| Writer or maintenance owns the store | An ordinary query computes live when preparation ownership is unavailable. An explicit preparation request reports the admission failure. |
| Running, paused, cancelled or failed snapshot | Live queries over a consistent read transaction; no persistent completed-snapshot result is selected. |
| Read-only store | Reads a valid projection or computes live with file-backed connection-local temporary tables. It creates no projection schema or writer/maintenance lock files. SQLite may still use normal read coordination sidecars. |

Only preparation admission contention is an optional fallback. SQL, schema, malformed-data and preparation failures propagate; they are not converted into empty groups or a successful cached answer.

## Publication, invalidation and lifecycle

Preparation uses one immediate SQLite transaction. It reads completed source state inside that transaction, builds the rows and publishes valid heads last. Heads bind snapshot ID, original start/finish timestamps, exact scope where applicable, and model version. Reads require the current snapshot to remain completed and all bindings to match. Failure or cancellation before commit rolls back the transaction, so partial projections stay invisible.

Entry insert/update/delete triggers invalidate the affected snapshot's heads. Changes to root, start/finish timestamps, status or completeness also invalidate them. This defends migrated databases and writes outside the normal coordinator; it does not make arbitrary external edits to authoritative snapshot summaries supported.

Policy changes invalidate directory payloads because their exclusive estimates depend on policy. Duplicate groups retain policy-independent candidate savings: each read applies current policy and completeness in its own transaction. Lookup requires exactly one `scan.options` observation with matching snapshot, subject, predicate and root. Multiple legacy rows produce unknown policy. A partial index supports the check after projection schema installation; building it is cold migration work.

The old `directory_metrics` cache is retained but not read by this implementation. Draft model-2 heads are also ineligible for model-3 reads, preventing old cached estimates from bypassing current semantics.

Snapshot lifecycle rules support these guarantees:

- Entries can be ingested only while the snapshot is running. Checkpoints and frontier writes require a running scan job and running snapshot.
- Completed filesystem nodes and saved scan policies cannot be overwritten. Separate derived graph analysis remains supported and refreshes completeness/error metadata.
- Scan completion publishes snapshot totals, terminal snapshot status and terminal job status atomically. A pending frontier prevents completion; an accepted stop request is considered in that transaction.
- Repeating the same terminal transition retains its timestamp. Completed snapshots cannot reopen as running scans. Interrupted or stopped unfinished scans resume their retained frontier with validated options.
- Recovery preserves completed snapshots from older versions' separate snapshot/job publication window.
- Snapshot allocation totals use the all-alias-known-and-equal rule. Logical or known-allocation contradictions mark completion incomplete.

## Resource and cancellation boundaries

Preparation aggregates in native SQLite with file-backed temporary relations. Connections set a main page-cache target of 16 MiB, a temporary page-cache target of 8 MiB, and one SQLite auxiliary worker thread. These are configuration targets, **not a hard limit on total process memory**. SQL sort buffers, connection state, result pages, runtime allocations, WAL, temporary files and the OS file cache have separate costs. Working set/private bytes and disk growth still require actual measurement.

Persistent duplicate membership stores only paths belonging to qualifying duplicate groups. Facets store requested scope aggregates; they do not copy every source entry into every ancestor. Separate subtree preparation incurs its actual traversal/grouping cost. Deep pagination retains normal SQL offset cost.

Cancellation uses a SQLite progress callback plus `sqlite3_interrupt`, with token checks around publication, rather than the provider's no-op `SqliteCommand.Cancel`. Cancellation is cooperative and has no instantaneous wall-clock guarantee. If it races after commit, the caller may observe cancellation while the complete projection remains durable; this does not imply partial publication.

## Validation checkpoint and timing protocol

Candidate 03 passed the combined native build and the independent finite correctness oracle: **248 checks passed, zero failed**. That scope covered lifecycle guards, hand-calculated subtree/facet/member results, read-only and contended live fallback, prepared queries, ambiguous policies, allocation/link uncertainty, invalidation, and interruption/rollback/retry during an actual SQLite write transaction on a 50,000-record synthetic fixture. The independent report SHA-256 is `0cb71ba86711e9e1e0dc0ca9199efdf2706acdd311f9b9ec1b524908edfd3ce6`. This is correctness evidence, not a scale or latency verdict.

Candidate 02's retained review found two real live-fallback errors: SQL concatenation formed `ASSELECT` while creating temporary facet and duplicate-group tables. Candidate 03 adds separators and includes strict policy ambiguity handling, model-3 binding, indexed policy lookup and independent directory allocation-conflict detection. Earlier failed evidence remains part of that round.

Use the [storage acceptance protocol](../benchmarks/README.md) to distinguish population and exact node counts; cold schema/index/projection preparation; first and repeated prepared queries; live/read-only fallback on unprepared data; and memory, disk, cancellation and correctness receipts for the exact candidate bytes.

A fresh process does not make the OS file cache cold. A read-only handle may read projections prepared earlier. A fast repeated page or unfinished large population does not close the ten-million-node gate, reference-hardware acceptance, incremental refresh gate or campaign quests.
