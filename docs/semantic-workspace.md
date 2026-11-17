# Semantic workspace

The Semantic model tab exposes recorded objects, lexical shapes, snapshot patterns, and a replayable placement game. This is an evidence projection, not a trained semantic classifier. Network, chip, terminal, task, computer and other dimensions remain unknown unless the source node explicitly records them.

## Opening the spatial workspace

The application opens on **Spatial atlas**. Its scene and camera presets come before the optional filters and observation details. The sidebar retains text labels in narrow panels. Overview remains available for storage metrics and the treemap; Semantic model provides the canonical object inspector and 9bit replay.

The top bar shows the running service version, verified from its health endpoint. A UI/service version difference displays **Build mismatch**; an unavailable health check displays **Build unavailable**. The sidebar includes both versions so an old tab or a different local instance can be identified.

The address retains the selected view and snapshot, for example `/?view=spatial&snapshot=<recorded-id>`. Reload and browser Back/Forward restore that selection. Credentials remain in browser session storage and are never added to these links. An unavailable snapshot produces an explicit message and an empty selection instead of silently showing another snapshot. Starting a new scan selects the new snapshot rather than retaining the prior link.

Spatial layers represent recorded snapshots. Their spacing is ordinal, not elapsed duration, and does not imply that an object moved between computers. Shape size does not encode storage size. Use the snapshot scope and completeness labels when interpreting the bounded projection.

Native semantic nodes use the backend's canonical shape, color, SOM ID, observation ID and observed time range. Each drawn instance also has a distinct view ID so one identity can appear in several layers. Hierarchy locations and snapshot summary glyphs label their view IDs explicitly; they do not invent native SOM identities. **Inspect native identity** retains the complete backend object, including word shapes, dimensions and hashes, when an exact source node match is available.

The atlas loads at most 12 root locations and 12 native semantic nodes per selected snapshot, plus its snapshot summary. The scene caption identifies this bounded sample. Filters only filter loaded objects; they do not query the entire drive. The full semantic export remains the interface for complete stored graph output.

## Identity and time

`somId` commits to the collector's node identity. A separate observation ID commits to its snapshot, raw source and observed time range. The reader computes UTC minima/maxima from actual observations. A snapshot's collection window is not substituted for a node's observation time. A read-only SQLite transaction pins all related reads, including two-snapshot comparison and complete export.

The pattern view considers at most the first 500 File nodes by ordinal ID in each snapshot and returns at most 50 pairs. Equal collector IDs are shown separately from lexical path matches. Path matches use root-relative slash-separated segments represented as NFKC lowercase token arrays. Case folding is a lexical feature, not a claim about filesystem identity. At least two path segments are required. Truncation is reported. Neither path similarity nor equal fallback IDs prove physical movement across machines or sessions.

## Observed challenge adapter

The server considers the first 500 File nodes by ID, then selects up to 12 distinct eligible filenames of two to four tokens. It requires matching complete `filesystem.metadata` evidence with the same snapshot, subject and exact observed source path. Only words retained by the object's bounded lexical model are eligible. Every token becomes a separate piece; its SOM endpoint is the full lexical word ID. Piece identity also commits to its source object and token position.

Filename patterns and same-source relationships describe word co-occurrence in these verified records. Path relationships describe exact parent-path equality of recorded filenames. These are grounded in stored metadata, not externally rechecked filesystem truth or proof of linguistic meaning. The adapter does not manufacture graph relationships, network topology, host identity, or trained weights. Every round resets learning; empirical co-clear counts never feed back into scoring.

The challenge dataset hash covers only the selected objects, evidence, snapshot and selection policy. It is not the full drive graph hash. Three rounds present the same selected evidence in original, deterministic hash and reverse deck orders; they are not independent held-out datasets. Both model competitors must receive identical challenge bytes, rules and resource limits per round.

`POST /api/semantic-game/replay` accepts only snapshot ID, round, expected challenge hash and moves. The service regenerates the challenge from its own database and rejects stale hashes. Client scores, evidence, associations and weights are never authoritative. Replay is pure computation available to the read role.

## Complete export

`filemapper semantic export <snapshot> --database <db> --destination <new-file.ndjson.gz>` streams every stored node as a semantic object, every edge, observation, file location and finding under one pinned read view. Edge endpoints and observation subjects are mapped to SOM IDs without dropping unknown or dangling references. Stored file hashes retain their original collection scope; metadata content hashes do not establish file-byte equality.

Each line is canonical UTF-8 JSON followed by LF. The SHA-256 content digest includes the header and all data records, excluding the footer. The footer declares completion, exact counts, kind counts, hashed byte length and digest. It has no wall-clock export timestamp, so the same pinned source and model can reproduce the content hash. The CLI separately computes a hash of the actual output file, which may be compressed. It creates a unique temporary file and moves it to a new destination only after completion; existing files are never overwritten.

The authenticated read endpoint `GET /api/snapshots/{id}/semantic-export?compression=none|gzip` exposes the same complete stream. `none` is the default. The snapshot is validated and pinned before response bytes are written. Gzip is the file format (`application/gzip`), without an HTTP `Content-Encoding` header; uncompressed output is `application/x-ndjson`. A 200 response alone does not establish a completed download: verify the final footer, counts, hashed byte length and SHA-256. A failed or cancelled transfer is aborted without a synthetic footer. Snapshot completeness remains independent of export completeness: a complete export can faithfully contain an incomplete scan.

Managed output is streamed. SQLite may materialize grouped observation ranges and sorting internally; no ten-million-object memory or latency claim follows from this design. Cancellation is cooperative between reads and writes; long SQLite operations are not promised a hard deadline.

## Native interfaces

- `semantic objects <snapshot>`: paged canonical semantic objects, optional kind/search filters.
- `semantic patterns <before> <after>`: bounded identity and lexical pattern comparison.
- `semantic challenge <snapshot> --round 1`: frozen input for a round.
- `semantic replay --challenge <json> --moves <json>`: offline deterministic replay; caller-supplied evidence is not authenticated.
- `semantic export <snapshot> --destination <new-file>`: complete stored snapshot stream.

The UI uses paged APIs and authenticated native replay. A browser JSON inspector is a presentation surface; use canonical exports to preserve exact numeric lexemes and recompute hashes.

The native [PowerShell module](../src/FileMapper.PowerShell/README.md) provides semantic object paging, snapshot comparison, challenge creation, replay and verified streaming export. Its `-All` enumeration performs separate paged requests; it is not an atomic snapshot export. Use `Export-FileMapperSemanticSnapshot` for the pinned, complete stored model. The exporter verifies the original decompressed bytes and gzip trailer before atomically publishing a new destination. Existing destinations remain untouched, and `-WhatIf` does not connect to the service or create a file.

The [stdio MCP server](native-mcp.md) exposes bounded read-only semantic tools alongside search, context, rank, diff, route and findings. It opens an existing database without schema initialization or scan recovery. Complete exports use the CLI or HTTP/PowerShell path; MCP response limits are explicit and must not be interpreted as full-drive coverage.

HTTP semantic replay accepts the exact camelCase request and move fields. Duplicate, unknown, missing and incorrectly typed properties are rejected. The server regenerates the stored challenge and requires its expected hash before replaying moves; the client cannot submit an authoritative score or replace observed evidence.
