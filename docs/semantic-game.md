# Native semantic game

`FileMapper.Semantics.SemanticGame` is a deterministic C# hard-drop game. It operates on supplied immutable records and performs no filesystem reads, writes, network calls, random sampling or model calls. `Start(challenge)`, `Apply(state, move)` and `Replay(challenge, moves)` all return `GameState`. Invalid input throws `GameValidationException`, an `ArgumentException` with a stable `Code`.

The challenge identifies the dataset by its lowercase SHA-256 digest, schema and version. The adapter must describe the digest's scope: a bounded selection of observed files and graph nodes is not a full-drive dataset. Evidence ids are references supplied by that adapter, not proofs that the engine independently reads or authenticates. To compare model arms, the host must pin the same challenge hash and reject replacement challenges. The engine's hash commits to content; it is not an authenticity signature.

## Contracts and coordinate system

The JSON field names below assume the repository's camelCase serializer. All collection fields use `ImmutableArray<T>` and must be supplied as arrays, including empty `associations`, `patterns`, `history` and `learning` arrays.

```json
{
  "id": "explicitly-labelled-fixture-or-observation-id",
  "datasetHash": "<64 lowercase hexadecimal characters>",
  "datasetSchema": "<adapter's dataset schema>",
  "datasetVersion": "<adapter's dataset version>",
  "deck": [{ "id": "piece-1", "somId": "som-1", "label": "alpha", "tokens": ["alpha"], "shape": "I" }],
  "associations": [],
  "patterns": [],
  "challengeHash": null,
  "width": 12,
  "height": 20,
  "schema": "filemapper.semantic-game.challenge/1",
  "rulesVersion": "semantic-tetris/1"
}
```

`Start` computes `challengeHash`; a supplied non-null hash must match. Deck order is significant. Piece ids must be unique; multiple pieces may refer to the same SOM id, but repeating one object cannot create a relationship with itself or satisfy a multi-object word pattern. Supported canonical shapes are `I`, `O`, `T`, `L`, `J`, `S`, `Z`, `pillar`, and `stair`, using exactly the planar XY cells in `ShapeCatalog`.

```json
{ "rotation": 0, "column": 4, "expectedPriorStateHash": "<stateHash from the preceding response>" }
```

The board is exactly 12 columns by 20 rows. Column zero is left; row zero is the bottom. Rotation is 0–3 **counterclockwise** XY quarter turns, applying `(x,y) -> (-y,x)` and normalizing minimum X and Y to zero. `column` is the normalized piece's leftmost column. The engine rejects a piece crossing either side, spawns it at `20 - rotatedHeight`, rejects an occupied spawn as top-out, and drops until the next downward step would collide. There are no lateral moves, wall kicks, hidden cells, soft drops or timing-dependent actions.

All full horizontal rows clear simultaneously. Remaining cells fall by the number of cleared rows below them. Top-out consumes the attempted deck entry, records a zero-point step and ends the match without changing the board. Successful use of the final deck piece ends with `endReason: "deck-exhausted"`.

`GameState` contains `challenge`, `board` (`column`, `row`, `pieceId`), `nextPieceIndex`, `score`, `clearedLines`, `isOver`, `endReason`, `history`, `learning`, and `stateHash`. A `GameStep` contains `index`, `pieceId`, `move`, nullable `landingRow`, `topOut`, `clearedRows`, `scoreBreakdown`, `learningUpdates`, `stateBeforeHash`, and `stateAfterHash`.

## Evidence, scoring and learning

Scoring occurs only on actual completed rows. The prior is fixed at `fixed-evidence-prior/1`; `SemanticGame.ScoringPrior` exposes every weight, and `SemanticGame.ScoringPriorHash` is included in challenge and state identity.

| Component | Points |
| --- | ---: |
| Simultaneous lines | `100 × lines²` |
| Grounded filename/path token pattern | `250 × matched token count` |
| Inferred lexical token pattern | `30 × matched token count` |
| Supported same-source relationship | 60 |
| Supported path relationship | 40 |
| Supported graph relationship | 80 |
| Inferred lexical relationship | 10 |

An association is `{id, fromSomId, toSomId, kind, evidenceIds}`. The first three supported kinds require nonempty evidence ids. A `lexical` association must have an empty evidence array. Endpoints must be different SOM ids present in the deck. One association is allowed per unordered SOM pair and kind; several distinct kinds can describe the same pair. Relationships score on adjacent distinct piece identities in the cleared row's left-to-right identity list. Association direction is preserved in the challenge, but adjacency scoring treats its endpoints as an unordered supported connection; it does not assert a new causal or directed graph edge.

A pattern is `{id, tokens, kind, evidenceIds}` with 2–32 ordered tokens. `filename` and `path` require evidence ids; `lexical` requires none. Tokens are compared after trimming, NFC normalization and invariant lowercasing. Each normalized token sequence can appear once in the challenge, preventing reward multiplication through duplicate pattern ids. A sequence must match contiguous words spanning at least two distinct SOM ids. Tokens inside one filename, or copies of the same object, cannot alone earn a word-pattern bonus.

For each cleared row, the engine takes each piece id once, at its first column from the left, and expands that piece's ordered tokens once. Wide or disconnected cells do not multiply semantic matches. A pattern id and an association id pay at most once per move, even when the same objects span multiple simultaneously cleared rows. The audit retains matches on every relevant row; a repeated relationship match records zero points. It records row identities, ordered words (`pieceId`, `somId`, `token`, `tokenIndex`), matching pattern ids and relationship evidence.

`scoreBreakdown` separates `linePoints`, `groundedPatternPoints`, `inferredPatternPoints`, `groundedRelationshipPoints`, and `inferredRelationshipPoints`, plus `lines` and `total`. Lexical coincidences never become grounded relationships. Scores measure this declared game objective, not general semantic understanding or model quality.

Learning is explicitly an **empirical grounded co-clear count**, not trained language knowledge. Every grounded unordered SOM pair appearing together in any cleared row increases its count once per move, even when nonadjacent. It records `firstSomId`, `secondSomId`, `coClearCount`, `groundingAssociationIds` and `interpretation: "empirical-grounded-co-clear-count"`. Lexical-only pairs do not enter the learning table. `Start` always resets counts to empty, and the scoring function never reads them. Neither arm can strengthen its scoring prior by first collecting convenient matches.

## Replay, bounds and fair rounds

The state hash commits to the challenge hash, fixed prior, sorted board, deck position, cumulative score and lines, termination state, learning table, and ordered moves. A move includes its expected prior state hash. Audit after-hashes are excluded from the state hash to avoid self-reference; `Apply` reconstructs the entire match from its move history and compares the complete state and audit to reject modifications. Supplying a recomputed hash alongside a fabricated score or board does not bypass replay. `Replay` accepts no client score at all.

Limits are 512 deck pieces, 2,048 associations, 512 patterns, 32 tokens per piece/pattern, and 32 evidence ids per record; text fields also have explicit size and control-character limits. Malformed dimensions, missing arrays, duplicate ids, unknown shapes, self relationships, duplicate relationship kinds, duplicate normalized patterns, inconsistent supplied hashes, invalid moves, altered history, or moves after termination are rejected. Replaying a submitted state has linear move count with growing immutable transcript/hash work; this is deliberately bounded and designed for short evaluation matches rather than an unbounded game service.

`CreateCompetitionRounds(challenge)` produces exactly three deterministic rounds: the observed deck order, a SHA-256-derived fixed permutation, and reverse deck order. They use the same objects, associations, patterns and fixed scoring prior; they are alternative presentations, **not held-out datasets**. Every `GameCompetitionRound` carries its `challenge`, `sharedInputHash` (the challenge hash) and `scoringPriorHash`. Both arms must start fresh from that exact challenge, receive the same history/observation budget and deck visibility, and submit legal moves. Model identity, prompts, token/time budget, tool access, retries and transcript provenance belong in the competition host's report. The engine does not fabricate model decisions or comparative results.

## Native author validation fixture

A transparent synthetic fixture uses six `O` pieces with distinct SOM ids and the one-token labels `alpha`, `beta`, `gamma`, `delta`, `epsilon`, `zeta`. Drops at columns `0,2,4,6,8,10` clear two actual rows on the last move. With one grounded `alpha beta` pattern, one grounded graph relationship from alpha to beta, and one lexical relationship from beta to gamma, the final step must score `400 + 500 + 80 + 10 = 990`. Both rows retain their words, but semantic matches pay once, and the grounded learning pair increments once. Repeating the six objects with fresh piece ids produces the same 990-point step and increases the empirical count to two without altering weights.

Twelve vertical `I` pieces at columns 0–11 clear four rows for 1,600 base points. Stacked `pillar` pieces at column zero fill 18 cells; the seventh attempt tops out. Additional native checks should round-trip JSON, replay exactly, reject changed scores and cleared-row audits, reject stale hashes and boundary violations, and establish that a pattern entirely inside one object's tokens earns no bonus. Fixtures must remain labelled synthetic and must not be represented as observed drive data or model competition evidence.
