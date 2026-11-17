# Evidence-aware task context

`context-graph/v1` selects recorded graph metadata for a literal task query. The CLI, authenticated HTTP, read-only MCP and `Get-FileMapperContext` use one native engine and one pinned SQLite read transaction. It does not train the pattern learner, execute repository code, or open current source files as substitutes for historical evidence.

```powershell
filemapper context <snapshot-id> --search 'AuthController authorization' --budget 40000
Get-FileMapperContext -SnapshotId <snapshot-id> -Search 'AuthController authorization' -ByteBudget 40000
```

The HTTP route is `GET /api/snapshots/{id}/context?search=...&budget=40000`; the MCP tool is `filemapper.context`. The budget is 256–100,000 **UTF-8 bytes for the complete compact context JSON**, excluding CLI newline or HTTP/MCP framing. This is not a model-token budget. Invalid budgets are rejected rather than enlarged silently. If the snapshot's minimal truthful envelope cannot fit, the request fails explicitly.

## Selection and explanations

Queries accept at most 512 Unicode code points, with no control characters. An exact node ID/name/path and the first eight distinct letter/digit/underscore terms select up to 64 seed nodes. Wildcards are literal. SQLite candidate case folding is ASCII; application scoring also recognizes ordinal case-insensitive matches within those candidates. Empty queries return no inferred task context.

The response exposes `modelVersion`, snapshot/completeness, byte budget, truncation reasons and `items`. Each item now contains `node`, integer `score`, named `components`, `reasons`, a self-contained `connectionPath`, and source `observations`. This replaces the old unversioned array of bare `MapNode` objects; clients should select `items[].node` for identity/name/path. Node properties and file content are outside this projection.

Direct seeds receive 100 points. Exact ID contributes 500; exact name/path 120; each name term 20 and each path term 5. A discovered neighbor receives half its parent's score, a recorded relationship contribution (ownership/tests 35, modification/change 30, generation/dependency 25, other connections 10), and a five-point penalty per hop. A test/spec-like name adds eight points with an explicit heuristic explanation. Components sum to the returned score. Each node appears once, its best discovered connection is retained, and ties use ordinal node ID. Cycles do not earn additional rewards.

At most 12 nodes per hop are expanded for two hops, with 32 incident edges per direction/node and 256 candidate nodes total. `CONTAINS` is excluded from expansion to avoid turning a shared root into task relevance. Recorded edge direction and evidence remain intact while traversal may follow either direction. Connectivity does not establish execution flow, taint, verified ownership, test coverage, or content equality. An edge with a missing endpoint/evidence is omitted and disclosed.

Up to three latest recorded subject observations are included. Observation values are explicit 512-character previews with a truncation flag; original evidence IDs, source URI, time, confidence, completeness, collector version and recorded hash remain available. A recent observation is not automatically a recent commit. No missing relationship, label or context dimension is invented.

The complete envelope is measured while packing whole items. A large item can be omitted while a later smaller item fits; the response discloses the byte limit. With a very small budget, only a truthful empty envelope and `byte-budget` reason may fit. Selection bounds and omitted evidence are separate from the byte limit. Returned units are **graph node identities**, not distinct physical files and not independently validated relevant results.

## Cancellation and acceptance boundary

Caller cancellation reaches native search and traversal. A SQLite progress callback checks cancellation during CPU-bound statements as well as between returned rows. The callback only checks the token and does not modify the connection, following [SQLite's progress-handler contract](https://www.sqlite.org/c3ref/progress_handler.html). Literal search can still inspect a full snapshot; result bounds are not a ten-million-node latency guarantee.

The new graph selection requires separate relevance evaluation. The historical learning bakeoff and its task-aware native-fact adapter are unchanged and do not validate this product planner. The specification's top-20 precision target, exact model-token budgeting, full context completeness and broader production gates remain open until separately measured.
