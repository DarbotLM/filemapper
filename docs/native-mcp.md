# Native read-only MCP

Run `filemapper mcp --database <existing-filemapper.db>` over standard input/output. The database must already exist. The MCP branch runs before initialization, migrations, scan coordination, installer admission, or abandoned-job recovery. It rejects all launch options except one optional `--database` path. The process uses the launching OS identity; client metadata is not an authorization claim.

Every legacy query connection opens the main SQLite database with `SqliteOpenMode.ReadOnly`, with pooling disabled. Semantic and route readers also open read-only transactions. Read-only handles reject initialization and writer/admission acquisition before creating lock files. MCP exposes no SQL, export destination, repository execution, installer credentials, or action executor.

This protects persistent FileMapper records, not a claim of zero filesystem activity. SQLite may need WAL/SHM coordination files and connection-local TEMP storage. The existing snapshot diff uses private TEMP tables, so `PRAGMA query_only` is deliberately absent: that pragma would disable valid comparisons as well as persistent writes. A read-only main connection still rejects writes to the main database. Use a supported consistent SQLite backup for offline copies; do not copy an active main file while omitting committed WAL content.

## Protocol versions

The current protocol is **2026-07-28**. Every current request carries `params._meta` with a string `io.modelcontextprotocol/protocolVersion` equal to that version and an object `io.modelcontextprotocol/clientCapabilities`. Optional client identity is display data. `server/discover` returns capabilities, supported versions and assembly-derived server version; current results include `resultType: "complete"` and server metadata. Unsupported per-request versions return `-32022` with `supported` and `requested`. Missing required current metadata returns `-32602`. See the [current protocol metadata and errors](https://modelcontextprotocol.io/specification/2026-07-28/basic) and [version compatibility rules](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning).

Legacy **2025-11-25** and **2024-11-05** clients can instead send `initialize` with `protocolVersion`, `capabilities`, and `clientInfo` containing `name` and `version`, followed by `notifications/initialized`. An unsupported legacy requested version selects `2025-11-25`; the client must decide whether it supports that version. Legacy tools remain unavailable until the initialized notification. A metadata-free pre-initialization `ping` is treated as a legacy ping. Modern requests never inherit identity, capabilities or version from a legacy handshake. No subscriptions, sampling, elicitation, roots, server requests, or continuation state are advertised. See [legacy initialization](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle).

Example current discovery message (send the JSON compactly on one line):

```json
{"jsonrpc":"2.0","id":"discovery-1","method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
```

The fixed tool catalog has no pagination cursor. Tool argument schemas use a local JSON Schema 2020-12 vocabulary; validation executes the same schema definitions that `tools/list` advertises. It never resolves remote schemas. Object arguments reject unknown properties, required/null/type errors, excessive lengths and out-of-range values. Integers are checked against their **exact JSON number token** before extraction; `1.0` and `1e0` are integral, but arbitrarily small nonzero fractions and rounded decimals are rejected. Extensible protocol `_meta` and capability maps are not interpreted as tool arguments.

## Tools and evidence bounds

| Tool | Inputs and bound |
|---|---|
| `filemapper.search` | `snapshotId`, `search`; at most 100 nodes |
| `filemapper.context` | `snapshotId`, `search`, optional `budget` 256–100000 bytes (default 16000); [ranked recorded connections and evidence](context-workspace.md), not inferred execution paths |
| `filemapper.rank` | `snapshotId`, optional `by`: logical/allocated/age; at most 50 files |
| `filemapper.diff` | `before`, `after`, optional `offset` and `limit` 1–500 (default 100); includes total/truncation and partial-scan interpretation |
| `filemapper.route` | `snapshotId`, `from`, `to`, optional `maxDepth` 0–64 (default 12); native route limits/completeness preserved |
| `filemapper.explain` | `snapshotId`, `subjectId`; at most 100 observations |
| `filemapper.finding` | `snapshotId`, `findingId`; saved ordered witness, assumptions and supporting evidence |
| `filemapper.semantic.objects` | `snapshotId`, optional `kind`, `search`, `offset`, `limit` 1–500 (default 50); full native SOM objects and source references |
| `filemapper.semantic.patterns` | `before`, `after`, optional `limit` 1–50; first 500 file candidates per snapshot; identity/path-pattern limits remain explicit |
| `filemapper.semantic.challenge` | `snapshotId`, optional `round` 1–3; deterministic native challenge from eligible observed filenames and matching filesystem evidence |
| `filemapper.semantic.replay` | `snapshotId`, `round`, `expectedChallengeHash`, `moves`; server regenerates the challenge and verifies the hash before replay |
| `filemapper.learning.model` | `learnerId`, optional `checkpointHash`; immutable model and fitted parameters; zero updates is untrained |
| `filemapper.learning.vocabulary` | `learnerId`, optional `search`, `offset`, `limit` 1–100; distinct source observations and encounter counts |
| `filemapper.learning.occurrences` | `learnerId`, optional `snapshotId`, `sessionId`, `offset`, `limit` 1–50; original source ranges and separate capture/context history |
| `filemapper.spatial.arenas` | optional `offset`, `limit` 1–50; locally saved immutable arena summaries |
| `filemapper.spatial.geometry` | `baseShape`, `variant`; actual XYZ cells and proper orientations |
| `filemapper.spatial.replay` | `arenaHash`, up to128 `moves`; saved frozen arena, verified state, legal commands and next-piece orientations |
| `filemapper.spatial.computer` | same saved-arena replay inputs; one deterministic bounded native suggestion, never attributed to Astra/SOL |

The catalog contains18 tools. Spatial moves use `orientationId`, `x`, `z`, `layerId` and `expectedPriorStateHash`. X/Z schema bounds are0–11; the frozen board imposes its tighter actual limits. Saved arenas are created explicitly through the authorized local CLI/HTTP interface. MCP cannot create learners, train, observe, save arenas or save replay artifacts. New read APIs do not initialize their optional schemas. Arena hashes bind their saved source/model/rule content; a competitor label is not proof of model authorship.

Identifiers are bounded to 256 Unicode scalars, search to 512, and semantic kind to 128. Offsets are nonnegative Int32. Replay moves contain only `rotation` 0–3, `column` 0–11, and a lowercase 64-digit `expectedPriorStateHash`; actual shape fit, current state, game-over and deck-length rules are enforced by the native game. The transport accepts at most 512 moves, and the generated deck imposes its smaller actual limit. Client-provided challenge/evidence/score/weights are rejected. A hash binds content; it is not independent proof that source evidence is true.

Successful tool content contains the exact native result serialized as JSON text. Semantic objects, pattern comparisons, challenges and replay use `SemanticWorkspace`/`SemanticGame`, rather than independent MCP scoring or projections. Pattern similarity is not identity or inferred movement; semantic game associations retain their recorded interpretation. File/repository strings remain untrusted data, not instructions.

Tool payload serialization stops at 512 KiB and returns `isError: true` with a narrowing suggestion, rather than silently truncating structured evidence. Complete datasets are available through native CLI/HTTP export; MCP deliberately exposes no filesystem export. Full finding witnesses can therefore exceed the MCP payload limit and require another interface.

## Framing, errors and cancellation

Input is strict UTF-8, one JSON-RPC object per newline. Fixed buffers enforce a 1 MiB **byte** limit before allocating a complete line. At byte limit+1 an error is emitted without waiting for a newline; the remainder of that oversized frame is discarded until the next newline. CRLF works, and a final unterminated frame is parsed before EOF shutdown. Arrays/batches are rejected (`-32600`), consistent with the [single-message stdio framing](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio). Recursive duplicate properties are rejected. JSON depth is capped at 64. Outgoing compact JSON lines are limited to 2 MiB, excluding their delimiter.

IDs are non-null strings up to 256 UTF-16 code units or plain decimal integer tokens up to 256 characters. Fractional/exponent ID spellings are rejected; accepted integer IDs are echoed without Int64/double rounding. An ID cannot be reused while a request remains in flight. Completed IDs are not retained indefinitely; legacy clients remain responsible for their version's session-unique ID rule. Valid notifications produce no reply, and malformed/unknown cancellation notifications are ignored.

Malformed JSON/UTF-8 returns `-32700`; invalid envelopes/batches return `-32600`; unknown methods return `-32601`; malformed method parameters or unknown tools return `-32602`; unexpected execution faults return `-32603` without exception internals. Tool argument validation, missing observed data, stale challenge/state and game-rule failures return a successful JSON-RPC envelope with `isError: true`, following the [tool error distinction](https://modelcontextprotocol.io/specification/2026-07-28/server/tools). Local capacity and final response-size errors use application codes 1001 and 1002, outside MCP's reserved error range.

At most eight tool requests are pending, and at most two execute concurrently. Transport reading continues during execution so `notifications/cancelled` can cancel an in-flight ID. Cancellation acceptance is serialized with response output; a response already being written may win that race, but no response begins after cancellation is accepted. Each tool has a 60-second cooperative cancellation budget. Native synchronous SQLite CPU work and some legacy materialization are **not preempted** by that token; SQLite busy timeouts only bound lock waits. Page/output limits do not imply a fixed bound on database work or memory for every legacy comparison. Use process isolation/resource limits if analyzing adversarially huge stored databases.

Output-gate acquisition and response writes have a five-second deadline, including an explicit bound on awaiting OS pipe operations that may ignore cancellation. A stalled or partially written output frame terminates the transport; the server does not append an error to that damaged frame.

EOF or Ctrl+C stops admission, cancels pending work, suppresses late responses, and allows up to six seconds for graceful worker completion before the CLI exits. The server neither mutates stored jobs nor attempts to recover them. These behaviors follow the [stdio cancellation model](https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/cancellation).

## Validation

The native C# author harness exercises a new controlled fixture, never a live or sealed benchmark database. Its 83 checks cover all seven legacy tools; all four semantic tools against direct native-engine results; modern discovery and both legacy handshakes; exact numeric parsing; forged replay input; recursive duplicates; malformed envelopes; arbitrary-precision ID echo; strict UTF-8; exact/oversized/non-BMP byte framing and recovery; bounded output; missing-database rejection; orphan-job preservation; read-only-file access; main-database write rejection; and unchanged main-database SHA-256 before/after. The first fixture attempts exposed missing synthetic witness/evidence setup, and an earlier completed run exposed the TEMP-table/query_only incompatibility; those results were retained and the corrected run passed 83/83. A subsequent focused 10-check native suite passed after the ID-retirement and output-backpressure fixes: it covers 500 immediate ID reuses, the eight-pending limit, in-flight duplicate rejection, cancellation during a database lock, absent late replies, malformed-notification silence, and stalled-output shutdown in about five seconds. Independent review is recorded separately; these checks make no additional throughput or production-load guarantee.
