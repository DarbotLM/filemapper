# Retained-source semantic mapping

`harvest semantics` connects a sealed source capture and lexical extraction to the native C# and PowerShell analyzers. It creates a separate immutable directory containing the original native graph, semantic objects, evidence, and exact byte-span word links. It does not execute repository code, import modules, restore projects, write the source index, or train learner weights.

```powershell
@{
    captureId = '<capture SHA-256>'
    extractionId = '<extraction SHA-256 for that capture>'
    # Omit entryIds to include the whole capture admission denominator.
    entryIds = @('entry:<native entry SHA-256>')
    options = @{
        maximumDocuments = 256
        maximumDocumentBytes = 2097152
        maximumTotalBytes = 33554432
        maximumGraphRecords = 500000
        maximumOutputBytes = 536870912
        maximumJoinWordsPerDocument = 250000
        maximumLexicalLinks = 500000
    }
} | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 request.json

filemapper harvest semantics --input request.json `
    --workspace C:\Evidence\source-corpus `
    --destination C:\Evidence\semantic-run-01
```

The workspace and destination parent must already exist. The destination must be new, separate from the corpus, and cannot be its ancestor. Ordinary local paths are required; Windows device/UNC/alternate-stream/reparse aliases are rejected. Strict request JSON rejects unknown, missing, duplicate, null-required, or incorrectly typed fields. Progress goes to stderr; stdout is the final receipt. This CLI operation does not connect the result to an already running service or change a saved source selection.

## Admission and context

The request pins native capture and extraction commitments. The reader verifies their bounded manifests, raw tree, capture associations, extraction associations, and coverage totals. Metadata is hashed and consumed through the same held streams. Every selected entry receives an admission record, including unavailable source, unsupported language/kind/path, and explicit budget exclusions. Selected source bytes are copied, SHA-256 checked, and checked against their Git blob object IDs before parsing. This proves consistency with the selected retained evidence; it does not independently authenticate a provider or re-attest a Git commit.

Supported extensions are `.cs`, `.ps1`, `.psm1`, and `.psd1`. Decoding is strict UTF-8 or BOM-marked UTF-16 LE/BE. Malformed encoding, UTF-32, and decoded NUL reject the document rather than parsing a replacement-decoded prefix. Empty and BOM-only text are admitted. Repository paths become escaped logical URIs; they are not file-read or network-fetch instructions.

Identity separates the stable semantic context from the observation run:

- The document URI binds repository identity, commit, and exact path.
- `semanticInputSetSha256` binds selected document descriptors and admission outcomes in native tree order. Parser input order is the ordinal URI order.
- `contextId` binds those stable inputs, options, lexical policy, native implementation hashes, and ordered trusted runtime metadata hashes. Re-capturing identical inputs with the same configuration does not change this context solely because the capture timestamp changed.
- `inputSetSha256` and `requestSha256` retain exact capture/extraction/request provenance, including rejected or limited entries.
- `runId`, native observation IDs, and actual analysis timestamps belong to this attempt. Original source observation time remains unknown.

C# uses Roslyn with trusted host runtime metadata. It does not restore the target framework, project references, analyzers, or generators, so missing project context remains diagnostic/partial. The runtime metadata files are held read-only through the run; on Windows those ordinary handles deny writes/deletion while used. PowerShell uses its parser and offline versioned command/flow catalogs; dependency references remain unverified logical references. Neither adapter demonstrates runtime behavior.

## Artifacts and word links

| Artifact | Meaning |
| --- | --- |
| `documents.ndjson` | Every selected capture entry and its admission outcome |
| `parsers.ndjson`, `parser-documents.ndjson` | Native parser scope, actual start/finish times, input coverage and strict-decoder outcomes |
| `nodes.ndjson`, `edges.ndjson` | Unmodified native graph records |
| `observations.ndjson`, `findings.ndjson` | Original evidence, confidence, scope, diagnostics, and modeled findings |
| `objects.ndjson`, `object-sources.ndjson` | Native SOM projection plus native-node hash and actual subject observation IDs |
| `source-spans.ndjson` | Exactly one outcome per native node: exact span, derived whole-document extent, invalid/missing span, or external/unavailable source |
| `lexical-replays.ndjson`, `*.lexical.ndjson.gz` | Fresh lexical replay of each unique parser-admitted blob under the saved policy |
| `word-links.ndjson`, `document-joins.ndjson` | Wholly contained unfragmented words, explicit per-document/global limits, and join coverage |
| `manifest.json` | Schema, provenance, options, implementation/runtime commitments, coverage, artifact sizes/counts/SHA-256s |

Parser offsets are UTF-16 units excluding a leading BOM. The strict decoder maps valid scalar boundaries to exact half-open raw-byte ranges; offsets inside a surrogate pair are rejected. Source-document extents are explicitly derived from verified decoded length. External nodes receive no invented source span.

Fresh lexical output must match the selected extraction's status, completeness, stored hash, canonical decompressed hash, and counts. A link means **this lexical word lies wholly within this node's source span**. It does not mean the word resolves to that symbol, shares its meaning, translates to another language, or reflects an observed command execution. Fragmented word units are excluded from this join denominator and counted separately.

SOM IDs remain the native `SemanticObjects.SomIdFor(node.Id)` identities. Shapes and 9-bit signatures may collide without merging nodes. Original node records are retained; only projection copies receive additional actual subject evidence IDs. The projection's snapshot envelope is labeled a semantic extraction context, not a filesystem observation. Its observed window uses the actual supporting observation times, or remains unknown.

## Limits and publication

Default input admission is 256 documents, 2 MiB/document, and 32 MiB total. Hard ceilings are 2,000 documents, 16 MiB/document, and 64 MiB total. Native graph records share one aggregate limit across both adapters (default 500,000; maximum 1,000,000), enforced before adding a new node, edge, observation, or finding. Graph-limit exhaustion fails the attempt without a success receipt. Existing C# syntax/flow and PowerShell flow budgets still apply and report their own partial results.

The default join prefix retains up to 250,000 unfragmented words per document and emits at most 500,000 links globally. These limits can be configured within hard ceilings of 1,000,000 words/document and 5,000,000 links. Aggregate truncation includes either reason; per-document records expose the separate denominators. Exhausting the link count does not trigger repeated scans of the remaining candidate words.

The default 512 MiB output ceiling applies to sealed artifacts plus manifest (maximum 4 GiB). Input, graph, and publication limits are **not** a hard process-memory or wall-time guarantee. Native parsers can retain syntax/flow structures; lexical temporary output and an unsealed failed stage can exceed the publication ceiling before rejection. PowerShell parsing is synchronous with cancellation checks immediately before and after its bounded call.

Artifacts are written under a new sibling staging directory. Final publication is a same-parent directory rename without overwrite after the manifest is durably written. A failure/cancellation retains an explicitly unsealed stage and bounded diagnostic for review. The final analysis ID is SHA-256 of the exact canonical manifest bytes. This is local immutable publication, not an authenticated signing service or a power-loss recovery guarantee.

The sealed state always says `sealed-partial-semantic-analysis`. Input admission completeness, lexical completeness, modeled local witnesses, graph integrity, and overall semantic coverage are different claims. This operation establishes neither CodeQL parity nor learned relevance improvements.

## Service jobs

Retained-source service jobs use the active startup source selection. Each accepted analysis copies the strict request/options and records its selection fingerprint, capture ID, and extraction ID in an immutable admission receipt. Capture/extraction manifest handles remain pinned through analysis and catalog registration. A saved connection change does not retarget accepted work. A compact index without its retained source corpus can be browsed, but cannot supply bytes for a new analysis.

The coordinator holds ordinary `MapStore.AcquireWriter()` admission for the whole operation, including importing an existing sealed result. This excludes concurrent scans, analysis writers and installation maintenance without adding filesystem snapshots or modifying learner weights. Only one coordinator owns a service data directory. Outputs use generated service-owned paths under `source-semantics/runs`; clients cannot select an analysis destination. Existing-result imports require a source directory and exact expected analysis ID, and the catalog verifies/copies the sealed artifacts before registration.

Jobs remain `running` or `cancelling` until a terminal `completed`, `failed`, `cancelled`, or `interrupted` status. `outputState` separately records `unsealed`, `sealed-unregistered`, `registered`, or `unknown-after-interruption`. Cancellation cannot undo an already sealed directory or catalog publication. A late cancellation after successful registration reports completion; failed/unsealed stages remain available for inspection. HTTP admission cancellation does not cancel an accepted background job; use its explicit cancellation control.

History retains at most 200 job summaries in a bounded atomic ledger, with phase progress and actual operation times. Eviction does not delete immutable results or request receipts. On restart, exclusive ownership and ordinary writer admission are required before stale active records become interrupted; their prior owner, start time and progress remain recorded. If maintenance or another writer prevents recovery, it is deferred. Recovery never reruns source analysis automatically, and a corrupt ledger fails explicitly instead of being silently reset. The ledger and analysis directory publication do not claim a multi-file power-loss transaction or parser process isolation.
