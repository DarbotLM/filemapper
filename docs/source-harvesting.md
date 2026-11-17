# Pinned source harvesting and word shapes

The source harvester retains Git source bytes and exact lexical occurrences in a separate workspace. It does not modify filesystem snapshots, historical bakeoffs, the existing semantic object model, or learner weights. C# performs capture, decoding, indexing and replay; PowerShell orchestrates runs. Repository source is data and is never executed by the harvester.

## Capture a repository revision

Prepare an owned bare repository with an exact HTTPS GitHub origin and a full commit ID. Resolve the numeric GitHub repository ID and retain the inventory response separately. A request file has this form; replace the example values with the actual pinned evidence:

```json
{
  "providerStableId": "1280010446",
  "canonicalUri": "https://github.com/DarbotLM/mdkg",
  "displayName": "DarbotLM/mdkg",
  "bareRepository": "D:\\source-corpus\\mdkg.git",
  "commit": "bab73043ef4ce291b2814d53253aadf00ea26b9f",
  "ref": "refs/heads/main",
  "inventorySha256": "<sha256-of-retained-inventory>",
  "policy": {
    "maximumBlobBytes": 16777216,
    "maximumCapturedBytes": 4294967296,
    "maximumTreeEntries": 2000000,
    "materializeBlobs": false,
    "fetchTimeoutSeconds": 1800
  }
}
```

```powershell
filemapper harvest capture --request .\capture.json --workspace D:\source-corpus\evidence
filemapper harvest extract <capture-sha256> --workspace D:\source-corpus\evidence
```

For a corpus that will use the compact projection, add `--defer-index` to `extract`:

```powershell
filemapper harvest extract <capture-sha256> --defer-index --workspace D:\source-corpus\evidence
```

This writes the same immutable lexical artifacts and extraction manifest without opening or changing the ingestion SQLite index. The returned `queryIndex` describes this invocation only: writes are deferred and existing query coverage is not evaluated. Retained cached lexicons are checked against fresh decoding of their raw source before reuse. Keep the returned capture and extraction IDs and explicitly build a compact projection from them. Until that projection is selected, ingestion-backed queries keep their previous coverage. The default `extract` behavior and receipt remain compatible; `capture` does not accept `--defer-index`.

`materializeBlobs: false` performs local reads only. Setting it to true explicitly enables a bounded filtered fetch of the pinned commit from the verified origin. The Git filter is a per-object threshold, not an aggregate network-byte cap. Retention limits apply independently. Repository-local Git configuration has a narrow owned-clone allowlist; additional HTTP, credential, include and URL rewrite settings are rejected before fetching. Global/system Git configuration, interactive prompts, hooks, maintenance, submodules and implicit lazy fetch are disabled for these child operations. This is not isolation from a malicious process already running as the same OS user or from all host/network configuration.

Capture manifests commit exact NUL-delimited tree bytes, entry order, raw path bytes, Git object identities, source SHA-256 objects, policy and coverage. Symlink targets are retained as text. Gitlinks remain references. Recognized LFS pointers are distinguished from their external payloads; payloads are not fetched by this collector. Missing objects, exclusions and errors remain explicit. A sealed capture is complete only for its declared policy and enumerated revision.

## Query and replay

```powershell
filemapper harvest list --offset 0 --limit 100 --workspace D:\source-corpus\evidence
filemapper harvest counts <capture-sha256> --workspace D:\source-corpus\evidence
filemapper harvest terms <capture-sha256> --search Flags --limit 24 --workspace D:\source-corpus\evidence
filemapper harvest occurrences <capture-sha256> --term source-term:<sha256> --limit 50 --workspace D:\source-corpus\evidence
filemapper harvest verify <capture-sha256> --workspace D:\source-corpus\evidence
filemapper harvest replay <extraction-sha256> --workspace D:\source-corpus\evidence
```

Query pages accept a nonnegative offset and a limit of 1–500. Output is JSON on stdout; collection/build progress is JSON on stderr. Cancellation propagates through source reads and queries. Read commands require an existing workspace and index.

Term search is a literal substring filter; `Flags` can return both `flags` and `fileflags`. Inspect the returned normalized term/term ID before requesting its occurrence page. Unknown, duplicate or operation-incompatible CLI flags fail before creating a workspace.

`verify` checks retained capture objects. `replay` freshly decodes the retained raw source under the pinned lexical policy, compares canonical NDJSON and stored gzip hashes, and writes a new verification-attempt receipt. Replay does not trust query rows as source evidence, modify existing corpus artifacts, or train a model. A changed runtime/Unicode policy is rejected rather than presented as an identical replay.

UTF-8 and BOM-qualified UTF-16 retain exact raw-byte positions, UTF-16 coordinates, line and column. Ordered lexical artifacts also retain punctuation, whitespace, controls and raw decode markers. Invalid encoding, binary/unsupported input and bounded token fragments are explicit partial states. Partial-prefix import requires proof against the actual raw source, not a self-asserted source hash. Query counts distinguish captured, processed, complete, partial, erroneous, excluded and unavailable documents.

Term identity uses `source-term/nfkc-lower/1`; source occurrence identity includes the source hash, lexical policy, original all-unit ordinal, kind and byte interval. The same content can have multiple repository/path document associations. These identities do not replace the older lexical or semantic model identities.

## Compact read projection

The ingestion SQLite index remains available for compatibility. An immutable compact index can be rebuilt from explicitly pinned capture/extraction receipts without reading the ingestion index. Supply a request file:

```json
{
  "inputs": [
    { "captureId": "<capture-sha256>", "extractionId": "<extraction-sha256>" },
    { "captureId": "<other-capture-sha256>" }
  ],
  "options": {
    "maximumDatabaseBytes": 2147483648,
    "minimumFreeBytes": 1073741824,
    "maximumEntries": 2000000,
    "maximumUnits": 500000000,
    "maximumCaptures": 2000
  }
}
```

```powershell
filemapper harvest compact --input .\compact-inputs.json --destination D:\source-corpus\source-01.db --workspace D:\source-corpus\evidence
filemapper harvest terms <capture-sha256> --search Flags --compact-index D:\source-corpus\source-01.db --workspace D:\source-corpus\evidence
```

An omitted extraction ID explicitly retains that capture's denominator with no lexical processing claim. Each build verifies sealed manifests, raw CAS bytes, gzip bytes, every lexical unit and aggregate coverage. Integer dictionaries and word postings replace repeated per-unit JSON; original all-unit ordinals and exact public occurrence identities remain intact. Full ordered units remain in the immutable gzip corpus.

The compact build receipt's unit/word counts describe unique source/policy lexicons. Per-capture counts describe document-associated occurrences, so duplicate-content paths contribute separately. These totals can differ legitimately and should not be compared without their denominators.

Builds use a separate staging database, bounded transactions, a database page cap and free-space checks. Cancellation or failure leaves a diagnostic and unpublished staging database. Explicit recovery supplies `options.resumeDatabasePath` with the exact original inputs, destination and limits. A per-stage lease prevents simultaneous resumes. Publication flushes and renames within the destination directory without overwriting an existing file. Journal bytes and concurrent host writes are not included in the database-size cap. This mechanism does not claim power-loss durability certification.

Compact snapshots do not automatically refresh when new captures arrive. Choose the index explicitly. Capture and default extraction use the ingestion index; `extract --defer-index` retains corpus evidence for a later compact build. A wrong-format, incomplete or missing explicitly configured compact database fails instead of silently falling back.

## Source word UI and HTTP

Save a source selection for the existing service data directory to reopen it on later launches:

```powershell
$sourceServiceData = Join-Path $env:LOCALAPPDATA 'FileMapper/data'
filemapper harvest connect --service-data $sourceServiceData --workspace D:\source-corpus\evidence --compact-index D:\source-corpus\source-01.db
filemapper harvest connection --service-data $sourceServiceData
```

Use the data directory already created by FileMapper or its installer. Omit `--compact-index` to select the existing ingestion index. The connect command validates the selection, writes a bounded canonical `source-connection.json` atomically, and returns its identity. It does not restart the service. The connection command reads saved configuration; it does not claim that a running service has activated it. Neither command initializes a corpus, map database or learner.

Restart the service to activate the saved selection. The normal installed launcher reads it through the service's data directory. Explicit `--harvest-workspace` or `--harvest-compact-index` arguments override the whole saved selection. With neither an override nor saved selection, the service uses `sources` under its data directory and can show an empty source view until an ingestion index exists. A malformed saved selection fails startup before credentials or the map database are initialized. An explicit compact-index-only startup can still query that projection when the default raw-corpus directory is absent; the UI reports raw-corpus availability separately.

Saved compact selections commit the exact database SHA-256 and byte length. Activation verifies those bytes and the completed native format, rejects SQLite sidecars and WAL-format files, and holds a read-only sharing guard for the service session. Windows prevents ordinary writers and replacement while that guard is held. This is not a cross-platform hostile-process or power-loss guarantee. Ingestion selections support an active WAL database using ordinary read-only SQLite queries; SQLite may coordinate shared-memory files, but connection validation performs no SQL mutations, migrations or checkpoints.

The source view shows the actual active mode, selected snapshot hash or saved connection identity, and authenticated path details. Refresh updates displayed data within that selection. Saving another connection shows a restart notice and does not silently switch the running service's source.

Start the service with `--harvest-workspace <directory>`. Optionally use `--harvest-compact-index <completed-database>` to select a fixed read projection. Both are startup configuration, not caller-supplied HTTP paths. The authenticated reader endpoints are:

- `GET /api/harvest/connection` — actual active selection and saved-selection/restart status
- `GET /api/harvest/captures?offset=0&limit=100`
- `GET /api/harvest/captures/{captureId}/counts`
- `GET /api/harvest/captures/{captureId}/terms?search=Flags&offset=0&limit=24`
- `GET /api/harvest/captures/{captureId}/occurrences?term={termId}&offset=0&limit=50`

The Source view stacks up to three selected captures and renders a page of 24 terms per capture. It provides camera rotation, flipcards and an occurrence inspector. Vertical spacing is ordinal; displayed timestamps are actual capture times. Connections mean equal normalized terms across selected captures. Nine lexical feature bits choose the shape and color; collisions do not establish object identity, semantic equivalence, translation, execution, or learned relevance.

## Current evidence and gaps

Saved source connections passed 130 independent Engine/CLI checks. A separate Service run passed 76 assertions across ten fresh processes, including authenticated status, saved versus active selections, explicit overrides and rejection before credentials or map initialization. Seven browser observations cover the displayed snapshot commitment, pending restart notice, identical re-save, active paths, spatial layers and cleared selection state. These counts describe bounded checks, not a coverage percentage or production certification.

Deferred extraction passed 69 independent checks covering index isolation, eleven hostile-cache variants, partial-result parity, cancellation/retry, CLI behavior and a subsequent compact projection. A separate author check found byte-identical default CLI output before and after the change on the same copied MDKG fixture. Cancellation retains diagnostic and reusable immutable artifacts; this does not establish rollback, crash certification or resistance to a malicious concurrent process with the same OS identity.

One fixed compact snapshot contains 17 captures, 24,082 entries and 392,043 unique-lexicon word occurrences in 60,375,040 bytes. Every reconstructed word payload matched the retained corpus. This is 49.1724% smaller than the previous compact build of exactly those inputs; it is not an estate-wide storage benchmark or a comparison against an ingestion database with a different capture set.

The fresh public MDKG challenge used one F# file and 57 independently anchored cards. Actual GPT 6 Astra and GPT 6.1 SOL arms each completed 12/12 finite, cued tasks over three rounds, with 41 correct per-task selections covering 36 distinct occurrences. FileMapper joined all 57 lexical anchors. Native F# semantic grouping remains unsupported, with no semantic score, broad winner or learning-gain claim.

Separate original-component checks exercised a small supplied graph in FileMapper and darbot-memory-mcp. Both retained 16 nodes, 24 edges and 32 supplied observation records across ordinary process restart, with explicitly recorded neutral-export losses. Their query interfaces differ. Original MDKG failed to reopen its written temporal container because its descriptor writer/reader orders differ; planned seek checks were not reached. These are bounded component observations, not full application parity or large-scale durability evidence.

Public inventory, access-scoped private discovery, source capture, lexical extraction, language parsing, runtime session observation, and training are distinct stages. The current source layer does not harvest all branches/history, infer every command/translation/adapter, or automatically feed the learner. Private evidence remains local and must not be included in public release/report payloads.
