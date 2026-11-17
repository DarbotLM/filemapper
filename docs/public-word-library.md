# Public word-shape editions

The native word-library publisher turns a retained public Git capture and its sealed lexical extraction into an immutable SQLite edition. It does not execute source code. An edition is one provenance-bound capture, not a claim that every file in a repository was readable or admitted by policy.

## Admission and source evidence

Publication requires the original public GitHub repository-list response, its SHA-256, repository provider ID and canonical URL, and a pinned unauthenticated public HEAD observation. The HEAD receipt and original `git ls-remote` output must agree with the captured commit and ref. The allowed owners are `darbotlabs`, `DarbotLM` and `dayour`. The selected provider record must say both `private: false` and `visibility: public`.

These checks establish consistency between retained observations. Locally supplied receipts are not provider signatures or cryptographic identity attestation. The observation time is preserved; a public observation does not assert that visibility remains unchanged forever. Private capture workspaces must not be mixed into a public publication batch.

The batch binds its public inventory, catalog, original API pages and HEAD receipt by hash. Repositories without a capture matching the observed HEAD remain explicit missing inputs. Forks can be represented in the library, but the current computer challenge builder excludes editions without an established non-fork family.

## Schema and meaning

`filemapper.public-word-library/4` stores:

- Exact word occurrences, normalized lookup terms, source document identity, source SHA-256, line and byte spans, and lexical completeness.
- Git path components with the original raw bytes encoded as base64, component ordinals and byte spans. Paths can remain observable when file content is unavailable.
- Lexical candidates for variable-like tokens, line-initial shell words and operator runs. These are explicitly candidates, not parsed or executed command semantics.
- Repository, revision, capture, extraction, extraction-policy, compact-index, public-observation and public-HEAD provenance; entry exclusions and coverage counts.

Shape geometry is assigned deterministically to nine catalog buckets. A geometry or color collision does not merge source identities, prove synonymy, identify a language, or establish a relationship. The source IDs, spans and evidence remain authoritative. Native C# and PowerShell semantic extraction provide separate parsed evidence where available.

`PublicWordLibraryReader` streams lookup results across SHA-pinned editions. Publishing from a compact index streams word occurrences in one SQL pass rather than issuing one query per vocabulary term.

## Commands

```powershell
filemapper word-library publish request.json
filemapper word-library verify request.json
filemapper word-library verify-index request.json
```

The request includes `captureRoot`, `indexPath`, `compactIndex`, `destination`, `extractionId`, `evidenceJsonPath`, `evidence`, `headEvidence`, `headReceiptPath`, `headStdoutPath`, and optional resource limits. Requests are limited to 64 KiB. `publish` creates a new edition and never overwrites one. `verify-index` requires exactly the requested capture and sealed extraction. `verify` checks an existing edition against its bound inputs, counts and SQLite integrity before it is accepted on resume.

Use the native batch script for a pinned estate:

```powershell
./eng/Build-PublicWordLibrary.ps1 `
  -Dotnet ./dotnet.exe -CliDll ./filemapper.dll `
  -CaptureRoot ./public-captures `
  -CaptureCatalog ./capture-catalog.json `
  -InventoryDirectory ./public-inventory `
  -HeadReceipt ./public-heads/heads.json `
  -OutputDirectory ./public-editions -PlanOnly
```

Review the generated plan, then invoke the same command without `-PlanOnly`. Use an immutable runner directory and retain its assembly, native runtime and script hashes with the batch. The plan includes all matching retained captures; it does not silently select a first-N sample. It also lists inventory repositories without a matching capture.

The default batch floor is 10 GiB free space, the compact-index limit is 2 GiB and the edition limit is 8 GiB. `-MinimumFreeBytes`, `-MaximumIndexBytes` and `-MaximumEditionBytes` allow explicit larger runs. These are admission and periodic storage checks, not a guarantee against all concurrent disk writers. Extraction needs its own monitored resource boundary. A sampled watchdog may stop an owned process tree, but cannot guarantee that free space never crosses a threshold between samples.

Stages retain extraction, compact-index and edition receipts. Resume validates exact inputs before reusing them. If an edition was published before its completion receipt was written, validation can recover the receipt. Per-capture failures are retained and the batch continues to other captures; failures are not counted as empty or complete repositories. Large archives, excluded blobs, missing objects and partial extraction remain visible in coverage.

## Local Word library bank

The application has a **Word library bank** workspace. A session with the `scan` capability can connect a folder whose immediate child directories contain `complete.json` and `edition.db`. Connection verifies each edition's SHA-256 and matching public metadata, persists the selected pins in the service data directory, and holds read guards while the catalog is in use. This admission does not independently authenticate the original provider or HEAD observations. Only completed editions enter the selection; reconnect to admit later completions. The catalog currently permits up to 1,000 editions.

Readers can search exact words, variable-prefix candidates, line-initial shell-word candidates, operator runs, and Git path components. Word lookup uses the normalized-term index. Candidate and path queries currently scan each selected edition and may be slow on large catalogs; cancellation interrupts the native SQLite query. Search accepts nonnegative 64-bit offsets without a hidden million-row clamp. The browser stops pagination if a count or offset exceeds its safe integer range.

Each result carries a stable source observation ID and a separate edition-scoped view ID. Repeated path components retain their ordinals. The spatial view shows up to 30 observations per page, with one ordinal layer per distinct capture timestamp and an unknown-time layer. Equal layer spacing does not represent elapsed time. Selecting a shape reveals repository, commit, capture, document and edition evidence. File-content spans and Git path-byte spans are labeled separately; unavailable coordinates remain unknown.

The service requires `read` for `/api/word-library`, `/api/word-library/editions`, and `/api/word-library/search`; `POST /api/word-library/connection` requires `scan`. Read routes do not accept arbitrary database paths. Catalog verification runs in the background after restart, and an in-flight query leases its existing catalog through a reconnect.

## Computer challenges and evaluation

The computer freezes a bounded deck from SHA-pinned editions. The current challenge builder creates ordered source-word and path-component patterns. Placement rotation changes geometry presentation, not the source identity. A paid ordered pattern requires its whole shapes to connect with increasing X anchors in the recorded source order.

Repository, revision and shared-content families determine the frozen partition. Validation and test episodes cannot train. Model-agent session hashes identify the orchestrated session; they are not hashes of private model weights. Replaying a recorded episode verifies legal transitions, source bindings and scores, not model identity or reasoning.

The first protocol round used one 93-byte public README, with 13 word occurrences and one path component. Its results validate the protocol at that scope. They do not establish a complete public corpus, semantic learning gain, CodeQL parity, model superiority or general game optimality. Production corpus ingestion and multi-family evaluation have separate receipts.
