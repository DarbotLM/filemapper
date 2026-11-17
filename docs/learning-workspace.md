# Persistent patterns and the spatial arena

The local workspace connects the native semantic objects to two separate mechanisms: a durable observation vocabulary and a supervised relevance model. Observation events preserve source identity, pattern occurrence, context and time. Explicit accepted labels update the model. Game scores, visual adjacency, missing edges, and a user's choice to clear a shape do not become training labels.

The first model has two fixed heads: `storage-group` and `code-path`. Its predictions are advisory relevance scores, not calibrated probabilities of a true filesystem identity, data-flow path or vulnerability. Physical accounting and graph witnesses still come from FileMapper's collectors and extractors. A new learner has zero training updates and neutral predictions. Persistence alone does not demonstrate useful learning. See [the learning model](pattern-learning.md) and [3D rules](spatial-tetrid.md).

## Native command surface

`filemapper learning --help` and `filemapper arena --help` describe the CLI. All commands use C# and accept `--database <existing-file>`. Structured writes use a UTF-8 JSON request file, limited to 64 KiB. Field names are case sensitive; missing required, unknown, duplicate, null and incorrectly typed fields are rejected. The CLI reports nonzero status for incomplete observation pages or cancelled computer searches.

Create a learner with `learning create --request create.json`:

```json
{
  "learnerId": "local-patterns",
  "policy": {
    "cohortId": "local-workspace-v1",
    "seedHash": "7061747465726e732d6578706c696369742d636f686f72742d736565642d763100"
  },
  "epochId": "initial"
}
```

The seed above is an illustrative fixed digest; choose and record a new seed/cohort before a new experiment. `learning model <learner>` returns its current checkpoint and weights. `--checkpoint <hash>` retrieves an older immutable checkpoint from that learner. Policy assignment reserves training, validation and test groups. A source family, repository and related temporal snapshots must use the same group key. New cohort experiments start new weights. The implementation enforces a supplied group's assignment; the experiment owner is responsible for grouping related sources correctly.

`learning subjects <snapshot> --offset 0 --limit 50` returns native subjects suitable for explicit references. A reference consists of `snapshotId`, `nodeId` and `expectedObjectHash`. An event request for `learning append <learner> --request event.json` contains:

| Field | Meaning |
|---|---|
| `eventId` | Stable caller operation ID; exact retries are idempotent |
| `expectedCheckpointHash` | Checkpoint on which the update is based; concurrent stale writes fail |
| `groupKey` | Preassigned whole-source family; held-out labels cannot train |
| `left`, `right` | Native object references; `right` may be null only for an observation |
| `context` | Seven explicit dimensions: workspace, repository, task, terminal, computer, network, session |
| `label` | Null for an observation; otherwise head, binary value, sourceKind, sourceId and rationale |
| `capturedAt` | Time of this encounter, distinct from source observation times |
| `epoch` | Optional successor epoch with its exact parent checkpoint and partition policy commitment |

Every context dimension is either `{"status":"unknown","value":null,"source":null}` or an `observed`/`declared` value with a source. Do not infer historic terminal, machine or session identity from the process currently reading an old snapshot. Labels must use `human`, `controlled-fixture` or `reviewed-dataset` provenance. These source declarations are retained for audit; they are not cryptographic authentication of the annotator. The service resolves native object references itself and rejects changed object hashes.

For continuing vocabulary ingestion, `learning observe <learner> --request page.json` accepts `snapshotId`, `expectedCheckpointHash`, `groupKey`, `context`, `capturedAt`, stable `batchId`, `offset` and `limit` (1–100). It returns processed/new-event counts, source-page total, checkpoint, page completion and snapshot-end flags. Continue at `offset + processed` using the returned checkpoint. The exact original page request can resume an uncertain response while the source page remains unchanged. Earlier events remain committed if a later event is cancelled or conflicts. Each page pins one read transaction; separate pages may observe later analysis of the source. If source analysis changed an interrupted page, inspect retained event history and begin a new explicit batch from the current head; an old page is not reconstructed from guessed history. Individual append-event retries use their committed immutable input and survive later source changes. A bounded game deck is never presented as ingestion of the entire snapshot.

`learning predict <learner> --request pair.json` requires `checkpointHash`, `head`, `left`, `right`, and `context`. It returns feature contributions, missing context, collision/truncation information and model commitments. `learning rank <learner> --request rank.json` takes one native `query`, distinct native `candidates` (at most 100), `checkpointHash`, `head`, `context` and `limit`. It validates all object hashes in one pinned read transaction and orders relevance ties by snapshot ID and node ID. This ranks native subjects; it does not inject content hashes, physical identities or flow labels into lexical features. The corresponding read-only HTTP operation is `POST /api/learning/{id}/rank`. `learning vocabulary` and `learning events` page through retained history. Encounters of the same source in later sessions are distinguished from new source observations.

## Freeze and play

`arena preview --request arena.json` reads without saving. `arena freeze` saves the complete native dataset, frozen model, context, rules and challenge as a content-addressed artifact. The request has:

```json
{
  "dataset": {
    "snapshotIds": ["recorded-snapshot-id"],
    "console": "storage",
    "objectsPerLayer": 16,
    "geometryVariant": "native-lift/v1",
    "clearMode": "planes",
    "round": 1
  },
  "learnerId": "local-patterns",
  "checkpointHash": "replace-with-the-64-digit-checkpoint-hash"
}
```

Consoles are `storage`, `words`, `csharp` and `powershell`. Choose 1–4 finished snapshots and 2–32 game units per layer. Collection completeness, candidate bounds, selected source count, game-unit count, witness-prefix scope, evidence caps and truncation remain in the saved dataset. Storage patterns use observed parent paths and equal actual filenames across folders, supported by matching filesystem evidence. Equal names are lexical groups, never a claim of equal bytes or physical identity. Word patterns require every retained filename token as an unordered whole-piece cluster; they do not validate word order or sentence meaning. Code patterns cite checked directed native edges; a bounded witness prefix does not establish a complete source-to-sink path or exploitability. Source code is never executed.

Round2 deterministically reorders the same selected evidence; round3 reverses it. These are presentation challenges, not independent held-out corpora. Geometry variants are explicit alternate encodings; they do not alter native SOM identity or retroactively change the old 2D game.

`arena load <artifact-hash>` reads a saved arena. `arena replay --request moves.json` accepts only `arenaHash` and `moves`; each move contains `orientationId`, `x`, `z`, `layerId`, and `expectedPriorStateHash`. The native engine reconstructs the score and legal state. No client score or reward is trusted. `arena computer` chooses one bounded native move; `arena baseline <artifact-hash>` plays the whole deck. These are FileMapper computer runs, not named language-model runs.

`arena save --request moves.json --competitor "declared label"` stores the verified transcript. The label remains caller declared and does not prove external model authorship. `arena export <artifact-hash>` writes exact stored JSON to stdout. A changed model or later source analysis cannot alter the immutable saved match. Subsequent matches can explicitly choose a newer model.

Same-layer learned affinities use one fixed ordinal endpoint order and the relevant head. At most512 pairs are retained, ranked by relevance then ID; candidate count/truncation are recorded. Neutral or lower relevance grants no learned bonus. The fixed game policy caps each learned pair at25 points and the entire learned contribution at100 points. Those bounds cannot be increased by training.

## Local service and agent access

HTTP uses the existing authenticated loopback service and a64KiB request bound. Only the operator role has the new `learn` capability for creating learners, committing events, observing pages, freezing arenas and saving replays. Reader, scanner and planner roles do not gain learning writes. Read-only operations include model/vocabulary/events, prediction, occurrence queries, bounded journal checks, geometry, saved-arena replay and computer suggestions.

The endpoints are grouped under `/api/learning`, `/api/snapshots/{id}/learning-subjects`, `/api/spatial` and `/api/spatial-artifacts`. `POST /api/learning/{id}/occurrences` accepts a typed occurrence filter (including source/session/context and observed/captured time basis) but performs no write. Arena CPU operations admit at most two simultaneous requests and have a30-second cooperative deadline. An exceeded budget is incomplete, not a fabricated successful result.

The read-only MCP tools expose stored models, vocabulary, occurrences, arena lists, native geometry, replay and the native computer. No MCP learning mutation or filesystem export destination is exposed. Its existing payload, concurrency and cancellation bounds still apply. See [MCP contracts](native-mcp.md).

## Validation scope

A mechanism test can show reproducible gradients, checkpoint persistence, collision handling and exact game replay. A separate, grouped and frozen evaluation is required to show improvement in retrieval or analysis. A successful geometric score cannot establish TreeSize or CodeQL feature parity. Keep source coverage, physical byte accounting, ordered data-flow witnesses, unknown cases, model utility and game score as separate measurements. New benchmark evidence must preserve failed rounds and distinguish a model's direct moves from a solver it authored.
