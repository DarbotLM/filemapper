# Staged content analysis

Staged content analysis compares current file content against a finished filesystem snapshot's inventory. It can find full SHA-256 matches, probable matches from sampled bytes, and shared content-defined chunks. Each run retains its baseline, policy, read evidence, coverage and sealed result independently of the original scan. It does not change `FileEntry.Hash`, snapshot metadata, existing duplicate projections or historical reports, and it performs no deletion, replacement or hardlink action.

The current model is `staged-content/v1`. Content digests use **SHA-256**, including sample and chunk digests; CDC boundaries use a versioned Gear64 policy. This implementation does not use BLAKE3.

## Choose a mode

| Mode | Admission and reads | Results |
| --- | --- | --- |
| `exact` (default) | Admit distinct identities with another eligible identity of the same size; sample them; fully hash matching size/sample groups. | Exact full-hash groups. Unresolved sampled matches can remain probable if full reads cannot finish. |
| `probable` | Admit same-size candidates and read first/middle/last samples. | Probable sample groups; full content equality remains unverified. |
| `partial` | Admit every eligible identity and read the entire unnamed stream with CDC. | Shared-chunk pairs and any exact full-hash groups. No sampling stage. |
| `all` | Admit every eligible identity; sample, then fully hash and chunk every successfully sampled identity, including sample-unique files. | Exact groups, unresolved probable groups and shared-chunk pairs. |

Every mode applies the configured scope, minimum size and resource limits. “Every” above means admitted work that can pass native read validation within those limits. A metadata candidate is not evidence of content equality. An empty result is meaningful only with its mode and coverage.

## Scope and observation times

Start with an existing snapshot whose job finished successfully. The analysis freezes in-scope baseline file identities and paths in an atomic admission transaction. An optional absolute `scopePath` must lie within the snapshot root. Counts distinguish physical identities from path names: multiple hardlinks to one identity do not create multiple content copies.

Admission rejects inconsistent alias metadata, reparse entries and fallback identities that cannot support native identity checks. Minimum-size and, for `exact`/`probable`, unique-size exclusions are separate policy reasons. Identity and path caps admit a deterministic bounded prefix, retaining all in-scope baseline paths for each admitted identity. `admissionReasons`, `unsafeExcludedIdentities`, `policyExcludedIdentities` and `admissionTruncated` disclose these decisions.

Guarded content reads currently require Windows and ordinary local filesystem paths. UNC/device-prefixed paths, named alternate-stream paths, trailing-dot/space aliases and reparse paths are unsupported. Paths and aliases are checked through held native handles, within the baseline boundary. Native identity, length and modification time must match the baseline at open; held versions are checked before and after the read. Full reads following samples must match the sampled version. A mismatch records a failed or invalidated read, preserving the baseline snapshot.

The baseline's observation interval, the analysis run interval, each content-read interval and each cache-validation interval have different meanings. Files are read after the baseline scan and at different times. A run is not an atomic filesystem snapshot or a claim about current content after its recorded observations. Equality covers the unnamed data stream; it does not establish equality of alternate streams, ACLs or other metadata.

## CLI

The CLI opens an existing map database directly. Replace the snapshot identifier and example paths with your own. `start` and `resume` wait for their durable job and print typed JSON. Ctrl+C requests a pause; resume is explicit.

```powershell
$database = Join-Path $env:LOCALAPPDATA 'FileMapper/data/filemapper.db'
$snapshotId = '<finished-snapshot-id>'

$run = filemapper hashing start $snapshotId --database $database --mode exact --max-bytes 1073741824 --workers 2 | ConvertFrom-Json
filemapper hashing status $run.id --database $database

# These results become available after resultHash is present.
filemapper hashing groups $run.id --database $database --mode exact --offset 0 --limit 50
filemapper hashing evidence $run.id --database $database --offset 0 --limit 50
```

Use `--mode partial` for direct full-read CDC or `--mode all` for the combined pipeline. `--scope 'C:\Data\Archive'` limits analysis to a baseline subtree. `--bytes-per-second 52428800` sets a shared 50 MiB/s content-read admission rate; `0` disables rate throttling. The limiter permits an initial burst of at most 250 ms of quota (one byte at very low rates), shared across workers. It schedules read admission, not physical device traffic or a guaranteed sliding-window completion rate. `--no-cache` requires new content reads. A JSON request file is also accepted with `filemapper hashing start --request .\hashing-request.json --database $database`.

```powershell
filemapper hashing jobs --database $database --snapshot $snapshotId --limit 100
filemapper hashing pause '<run-id>' --database $database
filemapper hashing cancel '<run-id>' --database $database
filemapper hashing resume '<run-id>' --database $database
filemapper hashing members '<run-id>' --group '<group-id>' --database $database --offset 0 --limit 50
filemapper hashing overlaps '<run-id>' --database $database --offset 0 --limit 50
filemapper hashing paths '<run-id>' --node '<identity>' --database $database --offset 0 --limit 50
filemapper hashing observation '<evidence-id>' --database $database
filemapper hashing chunks '<evidence-id>' --database $database --offset 0 --limit 100
```

`filemapper hashing --help` lists all controls. A successful CLI exit means the job reached `completed`; inspect `completeness` and `coverage` before interpreting its result.

## PowerShell module

The native module calls the local service. Starting or controlling work requires a scanner or operator credential; a reader credential can query it. Start returns the accepted run without waiting for completion. Closing the shell does not cancel an accepted service job.

```powershell
Import-Module FileMapper
$connection = @{
    Server = 'http://127.0.0.1:4177/'
    TokenFile = Join-Path $env:LOCALAPPDATA 'FileMapper/data/scanner.token'
}
$run = Start-FileMapperContentAnalysis -SnapshotId '<finished-snapshot-id>' -Mode all -MaxBytesRead 1073741824 -Workers 2 @connection
Get-FileMapperContentAnalysis -RunId $run.Id @connection

# Once ResultHash is present:
$matches = Get-FileMapperContentMatch -RunId $run.Id -Mode exact -Offset 0 -Limit 50 @connection
$matches.Page.Items
Get-FileMapperContentOverlap -RunId $run.Id -Offset 0 -Limit 50 @connection
Get-FileMapperContentEvidence -RunId $run.Id -Offset 0 -Limit 50 @connection
```

`Suspend-FileMapperContentAnalysis`, `Stop-FileMapperContentAnalysis` and `Resume-FileMapperContentAnalysis` take `-RunId`. `Get-FileMapperContentMember` adds `-GroupId`; `Get-FileMapperContentChunk` takes `-EvidenceId`. `Get-FileMapperContentPath -RunId $run.Id -NodeId '<identity>' -Offset 0 -Limit 50 @connection` pages the frozen path names separately from identity counts. Start and control cmdlets support `-WhatIf`. Set `-ScopePath`, `-MinimumBytes`, `-BytesPerSecond` or `-NoCache` as needed. The module exposes the other limits under the same option names as the JSON contract, in PascalCase.

## Local HTTP API

All routes require a local bearer credential. GET routes require `read`; POST routes require `scan`. Requests use strict camelCase JSON and the start body is limited to 64 KiB. The following body also works as a CLI request file:

```json
{
  "snapshotId": "<finished-snapshot-id>",
  "options": {
    "mode": "all",
    "maxBytesRead": 1073741824,
    "workers": 2,
    "bytesPerSecond": 52428800,
    "maxChunkRecords": 100000,
    "maxOverlapPairs": 10000,
    "maxPairComparisons": 200000,
    "reuseCache": true
  }
}
```

For example, after saving that body as `hashing-request.json`:

```powershell
$server = 'http://127.0.0.1:4177'
$tokenFile = Join-Path $env:LOCALAPPDATA 'FileMapper/data/scanner.token'
$headers = @{ Authorization = 'Bearer ' + (Get-Content -LiteralPath $tokenFile -Raw).Trim() }
$run = Invoke-RestMethod -Uri "$server/api/hashing/jobs" -Method Post -Headers $headers -ContentType 'application/json' -InFile .\hashing-request.json
Invoke-RestMethod -Uri "$server/api/hashing/jobs/$($run.id)" -Headers $headers
```

| Route | Contract |
| --- | --- |
| `POST /api/hashing/jobs` | Start `{snapshotId, options?}`; returns HTTP 202, the run and its status location. |
| `GET /api/hashing/jobs` | List runs; optional `snapshotId`, `limit`; response includes `items` and `recoveryDeferred`. |
| `GET /api/hashing/jobs/{id}` | Run status, phase, policy, coverage, completeness and result commitment. |
| `POST /api/hashing/jobs/{id}/pause` | Request cooperative pause. |
| `POST /api/hashing/jobs/{id}/cancel` | Request cooperative cancellation. |
| `POST /api/hashing/jobs/{id}/resume` | Explicitly resume eligible unsealed work with the original policy; returns HTTP 202. |
| `GET /api/hashing/jobs/{id}/groups` | Sealed groups; `mode=exact` or `probable`, `offset`, `limit`. |
| `GET /api/hashing/jobs/{id}/groups/{groupId}/members` | Sealed group members; `offset`, `limit`. |
| `GET /api/hashing/jobs/{id}/overlaps` | Sealed shared-chunk pairs; optional `nodeId`, `offset`, `limit`. |
| `GET /api/hashing/jobs/{id}/evidence` | Committed success and failure receipts; optional `nodeId`, `offset`, `limit`. |
| `GET /api/hashing/evidence/{id}` | One receipt with native versions, times and cache-source reference. |
| `GET /api/hashing/evidence/{id}/chunks` | Retained verified chunks, including referenced cache chunks; `offset`, `limit`. |
| `GET /api/hashing/jobs/{id}/work` | Work by required `stage`; optional `afterNodeId`, `limit`; returns `nextNodeId`. |
| `GET /api/hashing/jobs/{id}/paths` | Frozen paths for required `nodeId`; `offset`, `limit`. |
| `GET /api/hashing/jobs/{id}/objects` | Bounded native spatial projection; optional `nodeId`, `offset`, `limit`. |

Group/member/overlap/evidence/path pages contain `items`, `total`, `offset` and `limit`, within a response wrapper. Group/member/overlap/evidence limits are at most 200; the HTTP chunks/path limits are at most 1,024. Work uses a node cursor rather than an offset. Result queries require a sealed `resultHash`; committed evidence can be inspected while a run is unfinished. A visible page is not the analysis denominator.

## Resource limits and read accounting

| Option | Default | Meaning |
| --- | --- | --- |
| `minimumBytes` | 0 | Smallest admitted logical stream size. |
| `maxIdentities` / `maxPaths` | 1,000,000 / 2,000,000 | Frozen baseline inventory bounds. |
| `maxBytesRead` | 1,099,511,627,776 (1 TiB) | Cumulative content-read budget, including conservative crash charges. |
| `workers` / `bytesPerSecond` | 2 / 0 | Parallel reads and shared rate target; zero rate is unlimited. |
| `sampleWindowBytes` | 65,536 | First/middle/last window size; overlapping ranges merge. |
| `chunkMinimumBytes` / `chunkAverageBytes` / `chunkMaximumBytes` | 65,536 / 262,144 / 1,048,576 | CDC boundary policy; average is a power of two. |
| `maxChunksPerFile` / `maxChunkRecords` | 65,536 / 2,000,000 | Per-file and per-run retained chunk bounds. |
| `maxOverlapPairs` | 100,000 | Maximum discovered identity pairs. |
| `maxChunkFanout` | 256 | Maximum identities sharing a fingerprint used to discover pairs. |
| `maxPairComparisons` | 2,000,000 | Maximum pair-discovery comparisons, including repeated candidate pairs. |
| `reuseCache` | true | Permit version-validated reuse of eligible original evidence. |

Sampling hashes framed length, range offsets and sampled bytes under `first-middle-last-sha256/v1`. It reads no more than `min(fileLength, 3 * sampleWindowBytes)` content bytes. Full-only reads use `sha256-unnamed-stream/v1`; chunked reads use `gear64-sha256-cdc/v1`. Policy hashes distinguish these algorithms and settings, including chunk-retention limits.

Before reading content, the worker durably creates an attempt and reserves its maximum possible content bytes: the sample bound or the full file length. Completed charges, outstanding reservations and the next reservation cannot exceed `maxBytesRead`. Successful or failed receipts charge their actual returned bytes and release unused reservation. A cache hit uses zero content bytes and needs no content-read reservation. Reservation can reject a file even if a smaller read would fit; it does not read a prefix and call it a full hash.

`bytesRead` counts bytes returned by the content read API, including failed attempts and repeated stages. It is not physical disk traffic, metadata I/O, database I/O or cache lookup traffic. `inFlightBytesRead` is live progress, separate from committed receipt totals. `budgetChargedBytes` includes committed actual bytes and conservative charges for abandoned reservations. Outstanding reservations also constrain admission internally; a displayed committed budget counter is not permission to exceed the run cap.

Chunk batches are provisional until a successful receipt atomically publishes them. Failed reads retain their receipt and consumed-byte count but discard provisional chunks. A per-file chunk cap allows the full SHA-256 read to finish while retaining only a chunk prefix: exact full-hash evidence can remain valid, `chunksComplete` is false, and CDC coverage is limited. A global chunk or byte cap ends unfinished work as limited and seals the evidence already verified. Cache-referenced chunks count against the current run's chunk cap.

## Cache freshness and recovery

Cache entries are keyed by native identity, observed version key and policy hash. Only a successful direct original read can populate the cache; cache hits never form chains of new source evidence. A reuse receipt retains a reference to the original read evidence and records its own native validation interval with zero content bytes. Follow `cacheSourceEvidenceId` to inspect when the original bytes were actually read.

Reuse currently requires NTFS physical volume/native file identity and a coherent USN journal identity plus file USN, queried through the held filesystem handle. The version also commits relevant metadata and stream state. Missing privilege, unavailable journal evidence or an unsupported filesystem makes reuse ineligible; size and modification time alone never establish cache freshness. A direct read can still be attempted when cache reuse is unavailable.

This freshness decision assumes ordinary NTFS change tracking. It is not a new byte read and does not protect against mapped, raw or offline writes outside that assumption. Inspect `cacheEligible` and `cacheReason` on the native versions, or use `--no-cache`, `-NoCache` or `reuseCache: false` when new content reads are required.

Pause and cancellation are cooperative requests. Committed evidence remains available. An unsealed run in `paused`, `cancelled`, `interrupted` or `failed` status can be explicitly resumed; the original baseline, options, admitted paths and completed evidence remain fixed, and unfinished stages revalidate the current file version. Resume does not erase failures or restart completed/invalidated/limited work. Sealed results cannot be resumed or expanded; start a new run for a new policy or larger budget.

Recovery takes the normal exclusive writer admission, marks abandoned running jobs interrupted and discards provisional chunks. An attempt that ended without a receipt increments `unaccountedReadAttempts`; its actual bytes are unknown, so `bytesReadAreLowerBound` becomes true. Its **entire durable reservation remains charged** in `unaccountedReservedBytes` and `budgetChargedBytes`, including on resume. A graceful failed/cancelled receipt instead preserves its actual byte count. Unexpected failures with still-open attempts receive the same conservative abandonment accounting.

Runs participate in normal writer/maintenance coordination. Recovery can report `recoveryDeferred` while another writer or maintenance operation owns admission. Jobs do not silently resume after restart. A crash after sealing but before terminal status persistence can leave an interrupted job with a readable immutable result; that result cannot resume.

Stop requests use a separate small SQLite database at `<map-database>.hashing-control.db`. Its fully initialized schema is published atomically. Stop writes can commit while the main database holds an admission or sealing transaction; cancellation takes precedence over pause. Each record binds the exact run ID, request hash and original start time. Invalid schema or a mismatched record fails explicitly. The final seal checks the control channel while holding its short control transaction, so a stop accepted before publication prevents that publication. Explicit resume clears the prior request while the run is still stopped, before exposing it as running.

The normal SQLite map backup includes hashing runs, reservations, evidence and sealed results in the main database; it does **not** include this separate stop-control channel. The channel is operational state, not a result artifact or substitute for the main backup. Do not transplant a control database from another map: run/request binding rejects mismatches. After restoring a main backup, normal writer-owned recovery handles abandoned running records, and explicit resume clears prior matching controls. A pending stop absent from the backup is not reconstructed as a user command, and recovery does not automatically restart content reads.

## Interpret confidence, overlap and savings

Exact groups require equal logical lengths and complete full SHA-256 receipts at their recorded observations. Probable groups require equal lengths and sampled digests; equal samples do not prove full equality. Known unequal full hashes suppress a contradictory probable group, and a fully verified exact group does not need a redundant probable label. `identityCount` and `pathCount` remain separate.

CDC overlap compares chunk fingerprints `(SHA-256, length)` as **multisets**, preserving repeated chunks. For each retained fingerprint, shared bytes are `min(leftCount, rightCount) * chunkLength`, summed across fingerprints. `sharedBytes` cannot exceed either file's logical stream length. `leftFraction` and `rightFraction` use each entire unnamed-stream length as denominator, even when only a chunk prefix was retained. These values describe matching chunks, not semantic similarity or a promise that arbitrary edits will be recognized.

Pair discovery excludes fingerprints above `maxChunkFanout` and stops at the pair or comparison budgets. `skippedHighFanoutChunks` counts excluded distinct fingerprints; `overlapPairsTruncated`, `pairComparisonBudgetReached` and `pairComparisons` expose the other bounds. Once a pair is admitted, its shared-byte total uses every valid retained matching fingerprint, including those excluded from pair discovery, with multiplicity conservation. Pair completeness distinguishes partial CDC coverage from bounded pair discovery. Missing pairs under these limits are not evidence of no shared content.

`reclaimableBytes` is always null for probable groups and overlap pairs. Exact groups expose an allocation estimate only when the baseline scope is conservative, all current allocations are known, alternate streams are absent, and validated alias coverage matches every native link count. At most 1,024 aliases per identity are validated by the coordinator; incomplete or out-of-scope alias coverage leaves savings unknown. The estimate is total allocated bytes minus the largest member allocation: retain that identity and remove every alias of each redundant identity. It is an observation-time estimate, not guaranteed device space recovery or authorization to act.

Inspect `status` and `completeness` separately. `completed` means the job finished and sealed its result. `complete-for-policy` means the recorded policy completed over its eligible baseline coverage; it is not whole-volume completeness. `limited` records admission, byte, chunk, fanout or pair-discovery limits. `incomplete` records unsafe exclusions, read errors, invalidations, unaccounted reads or incomplete baseline coverage when no limiting flag takes precedence. Always inspect the individual coverage fields because multiple conditions can coexist.

The sealed SHA-256 `resultHash` commits the model, normalized request, baseline, coverage, frozen candidates/paths, retained evidence, referenced chunks and derived results. It is an integrity commitment, not an independent verification of the filesystem or a performance/production acceptance claim.
