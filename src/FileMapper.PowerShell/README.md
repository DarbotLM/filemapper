# Native PowerShell module

`Get-FileMapperContext -SnapshotId <id> -Search 'AuthController authorization' -ByteBudget 40000` returns a typed `FileMapperContext` with ranked node metadata, score components, connection paths, evidence previews and truncation reasons. It is read-only and uses the shared [native context engine](../../docs/context-workspace.md). `ByteBudget` limits the compact service payload, not a model tokenizer or PowerShell display formatting.

Build `FileMapper.PowerShell.csproj` in Release and import `bin/Release/net10.0/FileMapper.psd1` in PowerShell 7.6+. This binary module targets .NET 10 to match the PowerShell host. It communicates with the .NET 11 service over authenticated loopback HTTP; it does not load the engine into the host.

```powershell
Import-Module ./FileMapper.psd1
$scan = Start-FileMapperScan -Path C:\Source -HashFiles
Get-FileMapperJob
Get-FileMapperSnapshot -SnapshotId $scan.SnapshotId
Get-FileMapperSnapshot | Get-FileMapperTree
Get-FileMapperItem -SnapshotId $scan.SnapshotId -Search '.cs' -Limit 100
Get-FileMapperRanking -SnapshotId $scan.SnapshotId -By allocated -Top 20
Find-FileMapperDuplicate -SnapshotId $scan.SnapshotId
Invoke-FileMapperAudit -SnapshotId $scan.SnapshotId
```

Connection parameters on every command: `-Server` (default `http://127.0.0.1:4177`) and `-TokenFile` (installed `%LOCALAPPDATA%/FileMapper/data/service.token` when present, otherwise `%LOCALAPPDATA%/FileMapper/service.token`). Only loopback HTTP(S) is allowed. Redirects and proxy use are disabled so credentials remain local. The token is never printed. Failures become structured terminating `ErrorRecord` values; Ctrl+C cancels active HTTP calls. `Start-FileMapperScan -WhatIf` previews snapshot creation. Stopping a scan request does not cancel a server job that has already started; use the UI job cancellation control.

Outputs are actual .NET records with formatting views, not JSON strings. Ranking/duplicate/route results retain snapshot and completeness metadata. Item paging is explicit through `-Offset` and `-Limit`; tree returns one child page. `Start-FileMapperAnalysis -SnapshotId <id>` starts immutable static analysis on a completed hashed snapshot. Optional `-AnalyzerReportPath <json>` imports an explicitly selected precomputed PSScriptAnalyzer report; referenced source must be within the scanned root and the analyzer is never executed. `Invoke-FileMapperAudit` retrieves existing findings. Add `-FindingId <id>` to retrieve the ordered witness, model assumptions, source spans, and supporting observations. Semantic workspace cmdlets are described below; broader history/provenance cmdlets and full help remain campaign work.

## Semantic workspace

These five binary cmdlets use a service credential with read capability. Challenges and replay are read-only computations over observed snapshot evidence. They do not scan files, mutate the snapshot, perform filesystem actions, or accept client-supplied grounding or scores.

| Command | Result and scope |
| --- | --- |
| `Get-FileMapperSemanticObject` | Emits typed objects individually for the pipeline. `-Offset`/`-Limit` select a page (default 50, maximum 500); `-Kind` and `-Search` filter. `-AsPage` preserves total, snapshot, limits and model metadata. `-All` walks pages from the chosen offset. |
| `Compare-FileMapperSemanticSnapshot -Before ID -After ID` | Returns the typed page including bounded candidate limits and explicit identity/pattern distinctions. `-Limit` is 1–50. Lexical matches do not establish identity, renames or movement. |
| `Get-FileMapperSemanticChallenge -SnapshotId ID -Round 1` | Returns the actual grounded challenge with deck, patterns, associations, dataset hash and challenge hash. Round is 1–3. |
| `Invoke-FileMapperSemanticReplay -SnapshotId ID -ExpectedChallengeHash HASH` | Regenerates the challenge on the server and verifies ordered typed `-Moves`; returns full board, history, score breakdown, empirical co-clear counts and state hash. Omit moves for the initial state. |
| `Export-FileMapperSemanticSnapshot -SnapshotId ID -Path FILE` | Downloads the full pinned semantic dataset as verified NDJSON, or a gzip file with `-Compression gzip`. Creates a new destination; never overwrites. |

```powershell
$snapshot = Get-FileMapperSnapshot -SnapshotId $scan.SnapshotId
$objects = @($snapshot | Get-FileMapperSemanticObject -Kind File -All -Limit 100)
$page = $snapshot | Get-FileMapperSemanticObject -AsPage -Limit 10
$page.Snapshot.Completeness
$objects | Select-Object Label, SomeType, SomId, ObservationId
$objects[0].SomeShapes | Select-Object Token, Signature9, ShapeType

# Preserve arbitrary JSON evidence, including integers beyond 2^53 and extreme exponents.
$objects[0].RawSourceJson
$objects[0].RawSourceNode.GetProperty('properties').GetRawText()
$objects[0].DimensionsJson
$objects[0].ToJson()
[FileMapper.PowerShell.FileMapperSemanticJson]::Serialize($page)

$comparison = Compare-FileMapperSemanticSnapshot -Before $olderId -After $newerId
$comparison.Limits
$comparison.Items | Select-Object MatchType, IdentityEqual, Explanation

$challenge = $snapshot | Get-FileMapperSemanticChallenge -Round 1
$state = $snapshot | Invoke-FileMapperSemanticReplay -Round 1 -ExpectedChallengeHash $challenge.ChallengeHash
$move = [FileMapper.PowerShell.FileMapperSemanticMove]@{
    Rotation = 0
    Column = 0
    ExpectedPriorStateHash = $state.StateHash
}
$state = $snapshot | Invoke-FileMapperSemanticReplay -Round 1 -ExpectedChallengeHash $challenge.ChallengeHash -Moves @($move)
$state.History.ScoreBreakdown
# On subsequent turns send the entire ordered move list, including each exact prior-state hash.

$snapshot | Export-FileMapperSemanticSnapshot -Path .\snapshot.ndjson -WhatIf
$export = $snapshot | Export-FileMapperSemanticSnapshot -Path .\snapshot.ndjson.gz -Compression gzip
$export.Result
```

Object windows preserve original timestamp strings. `RawSourceNode` and dimension values are `JsonElement`, preserving original arbitrary numeric JSON tokens; use `GetRawText()`, the explicit JSON views, or `FileMapperSemanticJson.Serialize(...)` for lossless evidence serialization. PowerShell's ordinary `ConvertTo-Json` describes a `JsonElement` object instead of its JSON contents and is **not** the supported evidence serializer. Known contract counters are .NET integer values. Hashes are returned unchanged. `SomId` is stable semantic identity; `ObservationId` identifies the snapshot observation. `ContentHash` hashes source metadata, not file bytes. The nine-bit lexical signature permits collisions and is not an identity test.

Each object page is a separate read. `-All` is convenient pipeline pagination, not an atomic snapshot export: concurrent analysis may change the set between requests. The full export uses one pinned server reader and includes every stored semantic object, edge, observation, location and finding. Its header declares scope and its final footer contains the service's `filemapper.semantic-export/1` result. The client hashes exact original UTF-8 NDJSON bytes including LF and excluding the footer, verifies schema, requested snapshot, record and kind counts, a completed final footer and SHA256. Gzip downloads first validate the complete DEFLATE framing through exactly one member and its trailer followed by EOF, then independently verify decoded content and CRC32/expanded size. Trailing garbage and concatenated members are rejected. Gzip headers are bounded to 1 MiB. The stored payload is never reserialized.

Export uses `ResponseHeadersRead`, streams to a uniquely created temporary file in the destination directory, then validates one record at a time before an atomic no-overwrite rename. `-WhatIf` does not read a credential, contact the service, create a temporary file or inspect export contents. Existing destinations, malformed/truncated responses and verification failures produce terminating error records; cancellation deletes the temporary file. If cleanup itself fails, the temporary path is reported. A final destination is never committed before verification succeeds.

The compressed framing pass follows [RFC 1951, sections 3.1.1 and 3.2.2–3.2.7](https://www.rfc-editor.org/rfc/rfc1951.html) for DEFLATE bit packing, Huffman alphabets and blocks, and [RFC 1952, section 2.3.1](https://www.rfc-editor.org/rfc/rfc1952.html) for optional headers, FHCRC and trailers. The single-member acceptance policy is specific to FileMapper exports; the general gzip format permits multiple members. Framing reads use a 64 KiB buffer and retain only small code tables; decoded bytes are verified separately by the framework decompressor and the NDJSON verifier.

Semantic commands require a loopback origin without a URL path/query/fragment, disable redirects/proxies/automatic HTTP decompression, and accept `-TimeoutSeconds` (default 300, maximum 86400). Ctrl+C cancels HTTP and streaming validation. Replay accepts at most 512 moves and also enforces the service's 64 KiB encoded request limit; a long transcript may hit the byte limit first. Export limits are independent and adjustable: `-MaximumDownloadBytes` defaults to 64 GiB, `-MaximumExpandedBytes` to 256 GiB (including the footer), and `-MaximumRecordBytes` to 64 MiB including LF. Large exports can raise these explicit bounds and the timeout, for example `Export-FileMapperSemanticSnapshot -SnapshotId $id -Path .\large.ndjson.gz -Compression gzip -TimeoutSeconds 3600`; normal paged JSON responses have a 256 MiB transport cap. Memory use for export is bounded by one record plus fixed stream buffers, not total dataset size. A valid digest verifies transfer consistency, not the truth or authenticity of the underlying evidence.

## Persistent learning and whole-shape arena

The 14 learning/arena binary cmdlets use the same loopback connection and timeout options as the semantic commands. Reads, predictions, replay and computer suggestions require `read`; creating learners, appending observations or labels, freezing arenas and saving replays require `learn` (available to the local operator credential). These operations do not execute file actions or shell commands. Learning results are advisory; a learned association never becomes authoritative filesystem or code evidence.

| Command | Result and scope |
| --- | --- |
| `Get-FileMapperLearner` | Emits a page of typed learner heads. `-AsPage` retains totals; `-Offset`/`-Limit` select a page. With `-LearnerId`, returns the current frozen model; `-CheckpointHash` selects an immutable earlier model. |
| `New-FileMapperLearner -RequestJson JSON` | Creates a persisted learner, immutable partition policy and initial checkpoint. Supports `-WhatIf`/`-Confirm`. |
| `Get-FileMapperLearningSubject -SnapshotId ID` | Emits native source references, object hashes and observed lexical inputs; `-AsPage` retains page metadata. |
| `Get-FileMapperLearningVocabulary -LearnerId ID` | Emits descriptive terms with separate encounter/source-observation counts. Supports `-Search`, `-Category`, `-Offset`, `-Limit` and `-AsPage`. |
| `Get-FileMapperLearningPartition -LearnerId ID -GroupKey KEY` | Returns the sealed group assignment and policy hash. |
| `Get-FileMapperLearningOccurrence -LearnerId ID -RequestJson JSON` | Returns an occurrence page with preserved context provenance, encounter counts and source occurrence counts. The default query is `{}`. |
| `Invoke-FileMapperLearningObservation -LearnerId ID -RequestJson JSON` | Appends one bounded native source page to descriptive vocabulary/context indexes. It does not train supervised weights. Supports `-WhatIf`/`-Confirm`. |
| `Invoke-FileMapperLearningEvent -LearnerId ID -RequestJson JSON` | Appends an explicit observation (`right`/`label` may be null) or a provenance-bearing supervised label. Supports `-WhatIf`/`-Confirm`. |
| `Invoke-FileMapperLearningPrediction -LearnerId ID -RequestJson JSON` | Returns typed fixed-point relevance, frozen model/feature hashes, missing context and contribution evidence. |
| `New-FileMapperArena -RequestJson JSON` | Persists a bounded source dataset and frozen model as an immutable arena. Supports `-WhatIf`/`-Confirm`. |
| `Get-FileMapperArena -ArenaHash HASH` | Loads a saved arena. Accepts `ArtifactHash` from a saved arena through the pipeline. |
| `Invoke-FileMapperArenaReplay -RequestJson JSON` | Computes the authoritative state and legal moves from a saved arena and the entire ordered move transcript. |
| `Invoke-FileMapperArenaComputer -RequestJson JSON` | Returns a bounded native search suggestion, work counters, truncation and policy/state hashes. It is a native baseline, not an attributed external model run. |
| `Save-FileMapperArenaReplay -RequestJson JSON` | Verifies and saves the transcript with a declared competitor label. Supports `-WhatIf`/`-Confirm`; returns the artifact hash and final-state summary. |

`RequestJson` accepts an explicit JSON object, not executable PowerShell or a path to a file. Read a chosen file with `Get-Content -Raw` if needed. Client checks reject duplicate properties at every depth, unknown/wrong-case top-level fields, missing required top-level fields and requests over 64 KiB. The server additionally validates every nested field, exact integer type, source hash, checkpoint and move. Null and omission have different meanings: required nullable fields such as an event's `right` and `label` must be present. The server remains the authority for validity and scores.

The following creates an explicitly scoped learner and observes one source page. Use a completed snapshot already captured by FileMapper. The policy seed is chosen once and persists with that learner; record a real benchmark's policy and group separation before labeling or evaluation.

```powershell
$reader = @{ Server = 'http://127.0.0.1:4177'; TokenFile = "$env:LOCALAPPDATA/FileMapper/data/reader.token" }
$operator = @{ Server = $reader.Server; TokenFile = "$env:LOCALAPPDATA/FileMapper/data/service.token" }
$create = @{
    learnerId = 'workspace-patterns'
    epochId = 'epoch-1'
    policy = @{ cohortId = 'workspace-cohort-1'; seedHash = ('0' * 64) }
} | ConvertTo-Json -Depth 10 -Compress
New-FileMapperLearner @operator -RequestJson $create -WhatIf
$head = New-FileMapperLearner @operator -RequestJson $create
$model = Get-FileMapperLearner @reader -LearnerId $head.LearnerId -CheckpointHash $head.CheckpointHash
$subjects = @(Get-FileMapperLearningSubject @reader -SnapshotId $snapshotId -Limit 10)

$unknown = @{ status = 'unknown'; value = $null; source = $null }
$context = @{
    workspace = $unknown; repository = $unknown; task = $unknown
    terminal = $unknown; computer = $unknown; network = $unknown
    session = @{ status = 'declared'; value = 'review-session-1'; source = 'user-declared application session' }
}
$group = 'source-bundle-1'
Get-FileMapperLearningPartition @reader -LearnerId $head.LearnerId -GroupKey $group
$observe = @{
    snapshotId = $snapshotId; expectedCheckpointHash = $head.CheckpointHash
    groupKey = $group; context = $context; capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
    batchId = 'source-page-1'; offset = 0; limit = 10
} | ConvertTo-Json -Depth 20 -Compress
$batch = Invoke-FileMapperLearningObservation @operator -LearnerId $head.LearnerId -RequestJson $observe
$batch | Select-Object Processed, NewEvents, PageComplete, SnapshotEnd, Error, Scope
Get-FileMapperLearningVocabulary @reader -LearnerId $head.LearnerId -AsPage
Get-FileMapperLearningOccurrence @reader -LearnerId $head.LearnerId -RequestJson '{"sessionId":"review-session-1","limit":50}'
```

An explicit labeled event supplies `eventId`, `expectedCheckpointHash`, `groupKey`, `left`, `right`, `context`, `label` and `capturedAt`. Each source reference is `{ "snapshotId": "...", "nodeId": "...", "expectedObjectHash": "..." }`, using a subject's `SnapshotId`, `SourceNodeId` and `ObjectHash`. The label is `{ "head": "storage-group", "value": 0, "sourceKind": "human", "sourceId": "review-record-id", "rationale": "recorded judgment" }`. Heads are `storage-group` or `code-path`; values are 0/1, and provenance must reflect the actual label source. Only TRAIN-group labels update weights. Keep validation/test labels separate. Do not create labels from game clears or model predictions. Prediction requests contain `checkpointHash`, `head`, `left`, `right` and `context`.

An accepted observation page may be partially committed if interrupted. Check `PageComplete`, `Error` and `Scope`; a returned object alone does not establish a complete page. Preserve the original batch/event request and identifiers for an exact retry. Retries reject changed content; observation-page replay also requires the source page to remain unchanged. Cancellation stops the client request and does not roll back already accepted server commits. Each paged read is separate and does not promise a consistent multi-page snapshot.

```powershell
$current = Get-FileMapperLearner @reader -LearnerId $head.LearnerId
$arenaRequest = @{
    dataset = @{ snapshotIds = @($snapshotId); console = 'storage'; objectsPerLayer = 12 }
    learnerId = $head.LearnerId; checkpointHash = $current.CheckpointHash; context = $context
} | ConvertTo-Json -Depth 20 -Compress
$arena = New-FileMapperArena @operator -RequestJson $arenaRequest
$arena | Get-FileMapperArena @reader
$initialRequest = @{ arenaHash = $arena.ArtifactHash; moves = @() } | ConvertTo-Json -Compress
$initial = Invoke-FileMapperArenaReplay @reader -RequestJson $initialRequest
$suggestionRequest = @{
    arenaHash = $arena.ArtifactHash; moves = @()
    budget = @{ maximumEvaluations = 128; beamWidth = 4; lookaheadPlies = 1 }
} | ConvertTo-Json -Depth 10 -Compress
$suggestion = Invoke-FileMapperArenaComputer @reader -RequestJson $suggestionRequest
$suggestion | Select-Object Completed, Truncated, ActionsEvaluated, Reason
if ($null -ne $suggestion.Move) {
    # Use exact returned move/state hashes. Later turns send the entire ordered transcript.
    $transcript = '{"arenaHash":"' + $arena.ArtifactHash + '","moves":[' + $suggestion.Move.ToJson() + ']}'
    $played = Invoke-FileMapperArenaReplay @reader -RequestJson $transcript
    $save = $transcript.Substring(0, $transcript.Length - 1) + ',"competitor":"native-baseline"}'
    $receipt = Save-FileMapperArenaReplay @operator -RequestJson $save
    $receipt.Summary.GetRawText()
}
$arena.Arena.Dataset.GetRawText() # Includes source evidence, selection bounds and completeness.
$arena.ToJson()                  # Supported lossless JSON view of nested values.
$current.WeightsJson
```

Models, datasets, histories and contributions retain deep `JsonElement` payloads. Every learning/arena result provides `ToJson()`, preserving numeric lexemes and string values without decimal/double conversion; raw views preserve the original nested JSON text. String escaping may differ when serializing an entire result. Use these methods or `FileMapperSemanticJson.Serialize(...)`, not ordinary PowerShell `ConvertTo-Json`, for returned evidence. `ConvertTo-Json` in the examples is only for user-authored request hashtables containing ordinary bounded integers and strings. Source/model hashes remain unchanged.

All five write commands test `ShouldProcess` before parsing the request or opening a token/HTTP connection. Thus `-WhatIf` can run with a missing credential and unreachable service and causes no server mutation. Reads do not expose `-WhatIf`. Client timeout defaults to 300 seconds and Ctrl+C cancels active transport; server arena computations separately have a 30-second cooperative budget and two concurrent operation slots. The common response cap is 256 MiB. Requests must fit the service's 64 KiB body cap regardless of the number of moves. Arena rewards stay bound to their frozen checkpoint; replay and computer suggestions never train the learner.

## Robocopy planning interface

The 11 `*FileMapperRobocopy*` cmdlets use the authenticated loopback service and default to `%LOCALAPPDATA%/FileMapper/data/planner.token`. A reader credential can read the catalog, plans, jobs, events and output; validation, plan creation, preview, cancellation and RCJ parsing/rendering require planning capability. No PowerShell approval or execution cmdlet is exposed. Local execution is an explicit, separately reviewed CLI workflow.

```powershell
$draft = [FileMapper.PowerShell.FileMapperRobocopyDraft]@{
    Source = 'C:\ExampleSource'
    Destination = 'C:\ExampleDestination'
}
$catalog = Get-FileMapperRobocopyCatalog
$catalog.Options | Where-Object TileCategory -eq 'scope'
Test-FileMapperRobocopyDraft -Draft $draft
$plan = New-FileMapperRobocopyPlan -Draft $draft
Start-FileMapperRobocopyPreview -PlanId $plan.Id -WhatIf
$job = Start-FileMapperRobocopyPreview -PlanId $plan.Id
Get-FileMapperRobocopyJob -JobId $job.Id
Get-FileMapperRobocopyEvent -JobId $job.Id
Get-FileMapperRobocopyOutput -JobId $job.Id -Channel native
# Stop-FileMapperRobocopyJob -JobId $job.Id
$rcj = Export-FileMapperRobocopyJob -Draft $draft
$parsed = Import-FileMapperRobocopyJob -InputText $rcj
```

`New`, `Start`, `Stop`, `Import` and `Export` support `-WhatIf`/`-Confirm`. `-WhatIf` does not read a token, contact the service, create a plan, run `/L`, or read an RCJ file. Starting a preview returns a persisted background job; cancelling the HTTP request does not cancel an accepted job. Use `Stop-FileMapperRobocopyJob` explicitly and inspect its eventual terminal status. A terminal cancellation conflict is a structured terminating error, not a claimed success.

Typed drafts preserve nullable fields and 64-bit quantities. Plans/jobs/events are typed records, and catalog dictionaries recursively contain native CLR values rather than `JsonElement`; `ConvertTo-Json -Depth 30` preserves their actual contents. HTTP failures are terminating `FileMapper.Robocopy.Request` errors with permission/not-found/cancel categories. IDs are URL-encoded, responses are bounded, and credentials are never copied into result objects. Native output is localized advisory evidence, not a complete operation manifest. RCJ imports are a strict data-only subset; unsupported nested jobs and native logging/settings side effects are rejected. Export returns text to the pipeline and does not write a file or run native `/SAVE`.
