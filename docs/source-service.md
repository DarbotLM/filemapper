# Retained-source analysis in the local app

Open **Source semantics** after connecting to FileMapper. This view works without a filesystem snapshot. It reads the active source connection, lets an operator or scanner select a captured revision and eligible C#/PowerShell entries, and starts a bounded native analysis. A reader can browse registered results and download their original evidence.

The document selector describes captured metadata. Eligibility is not a promise that decoding, retained lexical coverage, or analysis budgets will succeed. Admission requires the capture and extraction to belong to the active source index and to match retained receipts. Source bytes are parsed without executing repository programs, importing PowerShell modules, restoring packages, or fetching dependencies.

## Workflow

1. Select a capture and choose all eligible entries or an explicit selection. Adjust the displayed document, byte and graph limits when necessary.
2. Start analysis and follow its durable job record. Closing the browser does not cancel accepted work. Cancel explicitly from the job tile; cancelled or interrupted work is not silently resumed.
3. Select a registered result. Orbit its native semantic objects, change camera perspective, filter and page through shapes, then inspect a node's source span, observations, related edges and retained lexical occurrences.
4. Import an existing sealed result by providing its local directory and the SHA-256 of its original `manifest.json`. The service verifies and copies originals into its own catalog, builds an immutable query projection, and registers only complete publication. An identical retry revalidates the existing result.

The spatial viewport is a bounded page of native shapes. Only fetched edges with both endpoints visible are drawn. Displayed source-span containment is not symbol resolution or runtime flow. Source intervals, evidence times, capture/extraction identities and analysis/import times retain their separate meanings. These views do not establish a complete session history or train learner weights.

C# binding uses the host's trusted runtime metadata, which is committed in the analysis manifest and semantic context. The Service can supply ASP.NET references absent from the CLI. Identical captured source can therefore produce different partial graphs in these hosts. Compare reference sets and context identities before comparing results; importing a frozen CLI result preserves that result's original host context.

## Local API

All routes except health require a local role credential. `read` authorizes catalog queries; `scan` authorizes starting, importing and cancelling work.

| Route | Purpose |
| --- | --- |
| `GET /api/harvest/captures/{id}/documents` | Page capture metadata; optional `supportedOnly`, literal `search`, `offset`, `limit` |
| `POST /api/source-semantics/jobs` | Submit the native `{captureId, extractionId, entryIds?, options?}` request |
| `POST /api/source-semantics/imports` | Submit `{directory, analysisId}` for a sealed local result |
| `GET /api/source-semantics/jobs` | Durable bounded job history and restart-recovery state |
| `GET /api/source-semantics/jobs/{id}` | One durable job record |
| `POST /api/source-semantics/jobs/{id}/cancel` | Request cooperative cancellation |
| `GET /api/source-semantics/analyses` | Page registered analyses |
| `GET /api/source-semantics/analyses/{id}` | Manifest, import time and original byte count |
| `GET /api/source-semantics/analyses/{id}/nodes` | Page nodes with optional `kind` and literal `search` |
| `GET /api/source-semantics/analyses/{id}/objects` | Page native SOM objects with the same filters |
| `GET /api/source-semantics/analyses/{id}/node?nodeId=…` | Node, native object, source span and separate evidence pages |
| `GET /api/source-semantics/analyses/{id}/observations` | Page observations, optionally by `subjectId` |
| `GET /api/source-semantics/analyses/{id}/edges` | Page edges, optionally by `nodeId` |
| `GET /api/source-semantics/analyses/{id}/word-links?nodeId=…` | Page span links joined to their exact retained lexical records |
| `GET /api/source-semantics/analyses/{id}/artifacts/{name}` | Download an allowlisted, hash-verified original artifact or manifest |

Catalog pages expose `total`, `offset`, `limit`, `nextOffset` and `byteLimitReached`. Follow `nextOffset`; do not infer completeness from the number of visible shapes. Catalog pages accept 1–200 items and have an 8 MiB record budget. Individual records beyond the page budget require their original artifact download. Document pages use the source index's 1–500 item limit and a separate metadata byte bound. Analysis submission alone accepts a 256 KiB request to accommodate the native maximum of 2,000 selected entry IDs; other API bodies retain the 64 KiB bound.

`filemapper harvest documents <capture-sha256> --workspace <corpus> --compact-index <database> --supported-only` exposes the same candidate metadata from PowerShell or another terminal. Omit `--supported-only` to include captured metadata for other languages.

## Ownership and limits

Jobs hold FileMapper's normal writer/maintenance admission from acceptance through terminal persistence. Installation cannot interrupt active source work, and source work cannot bypass an installer's maintenance barrier. Source jobs also appear in the app's shared activity history. Restart recovery records abandoned work as interrupted; maintenance can defer recovery until normal admission is available.

Managed originals, projections, jobs and unsealed failure stages live under the service data directory's `source-semantics` folder. Import and catalog integrity guards currently require Windows. They reject path aliases, reparse points, unexpected artifact names and inconsistent content commitments. Import does not imply that arbitrary supplied semantic assertions have been independently proven.

Capture bytes, source indexes, filesystem snapshots and learner weights are not changed by source analysis or import. Catalog content remains local, including private-repository evidence. Existing bakeoff reports remain immutable historical results. C#/PowerShell analysis and partial semantic evidence do not establish CodeQL feature parity, full language coverage, or a measured learning improvement. See [source-semantics.md](source-semantics.md) for the native graph contract and its bounds.
