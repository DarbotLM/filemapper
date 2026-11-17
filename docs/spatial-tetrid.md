# Native spatial Tetrid arena

`SpatialGame` is a separate, pure C# engine with schema `filemapper.spatial-game.challenge/1` and rules `spatial-tetrid/1`. The existing `SemanticGame`, `semantic-tetris/1` rules, `ShapeCatalog`, hashes and recorded results are unchanged. No trained model, external service, repository execution, randomness or current wall-clock time is needed to replay a match.

This is an evidence exploration surface. Match explanations retain FileMapper SOM, observation, source-node, evidence and code-edge references so a client can open the corresponding storage group, dependency route or audit witness. A score does not establish a new vulnerability, owner, causal relationship, deletion recommendation or physical storage saving. Storage measurements and finding severity must come from their original evidence.

## Coordinates and geometry

Each observation layer has its own bounded **X/Y/Z** board. X and Z are horizontal; Y is gravity, with the floor at zero. `LayerId` identifies recorded observations, not height or an elapsed-time coordinate. A piece is permanently assigned to one layer, and a move must name that layer. Two different layers can contain pieces at exactly the same X/Y/Z without collision. Native SOM identity and snapshot-specific observation identity remain separate.

The default board is 6×12×4. Width and depth may each range from 2–12, height from 4–24. There are at most four layers and 128 deck occurrences. A deck occurrence records `id`, `somId`, `observationId`, `sourceNodeId`, `layerId`, `baseShape`, `geometryVariant`, `geometryHash`, label, tokens and source evidence IDs. `unitKind` defaults to `object`; `word` occurrences additionally require `sourceSomId` identifying their parent native object. A word occurrence must not be presented as the entire file or function.

`SpatialGameGeometry.Get(baseShape, geometryVariant)` returns the declared encoding. All nine existing canonical bases are available, with five explicit variants:

| Variant | Geometry and interpretation |
|---|---|
| `canonical/v1` | The unchanged native shape; these canonical source shapes are initially planar. |
| `corner4/v1` | Four voxels extending from one corner along three orthogonal axes. |
| `screw4/v1` | A connected four-voxel chain turning through all three axes. |
| `cube8/v1` | A solid 2×2×2 cube. |
| `native-lift/v1` | Retains every native footprint voxel, lifts its first occupied cell into Z, and adds transverse support only for a native line. Each base remains recognizable and spans true XYZ. |

The four spatial variants are intrinsically three-dimensional **alternate encodings**, not rotations of the old planar geometry. Their declaration and geometry hash are retained alongside `baseShape`; they do not change native SOM identity. The three named corner/screw/cube variants share their cell geometry across base categories; `native-lift/v1` preserves the category footprint and is the new arena default. Existing frozen challenges keep their declared variant and hash.

`SpatialGameGeometry.Orientations` enumerates the 24 proper cube rotations using signed axis permutations with determinant +1. It normalizes translation and deduplicates symmetric poses. There are no reflections. An orientation ID hashes its sorted voxel geometry; a cube has one unique pose and a straight rod has three axis poses. The challenge hash commits to each declared geometry hash.

## Native contracts and moves

The public records are in `SpatialGameModels.cs`. Core calls are:

```csharp
SpatialState SpatialGame.Start(SpatialChallenge challenge);
ImmutableArray<SpatialLegalMove> SpatialGame.LegalMoves(SpatialState state, CancellationToken token = default);
SpatialState SpatialGame.Apply(SpatialState state, SpatialMove move, CancellationToken token = default);
SpatialState SpatialGame.Replay(SpatialChallenge challenge, IReadOnlyList<SpatialMove> moves, CancellationToken token = default);
```

A `SpatialMove` contains `OrientationId`, `X`, `Z`, `LayerId`, and `ExpectedPriorStateHash`. Y is computed by a straight downward hard drop; the player cannot provide landing height, move sideways through obstacles, or rotate after landing. `LegalMoves` lists in-bounds commands with predicted nullable `LandingY` and `TopOut`. A top-out command is an explicit game-ending outcome, not an overlapping placement. If every in-bounds command tops out, the opponent selects one deterministically and records that result.

`Start` validates and freezes the challenge, fills geometry/scoring hashes, and rejects mismatched supplied hashes. `Apply` reconstructs the supplied state from its move history and compares the complete state and audit. `Replay` accepts moves, never a client score or learned weight. The state hash commits to the frozen challenge, placements, deck position, totals, paid catalogs, ordered moves and end state. Audit after-hashes are excluded from that preimage to avoid self-reference, but the complete audit is checked during state validation.

## Rigid whole-piece clearing

The frozen `clearMode` is either `planes` (default) or `lines`:

- Planes require every X/Z cell at one Y to be occupied.
- Lines require a complete X row at fixed Y/Z or a complete Z row at fixed X/Y.

The modes are exclusive. Immediate line clearing would otherwise remove constituent rows before many planes could form, making the plane objective misleading.

Every shape intersecting a completed surface is removed **in full**, including its cells outside that surface. Surviving pieces remain rigid and settle vertically; no fragment can acquire the identity or semantic bonus of a whole shape. This differs intentionally from traditional Tetris cell slicing. Settling computes the greatest jointly movable set at each downward step, so mutually supporting shapes can fall together and outcomes do not depend on iterating piece IDs in a particular order. A subsequent completed surface can trigger another clear wave. Every wave removes at least one piece, bounding cascades by the placed piece count.

The audit records triggering plane heights/axis lines, all removed piece IDs, and each remaining piece's old/new Y. Scores count the completed surfaces, not the extra cells removed as part of intersecting shapes.

## Frozen patterns, evidence and advisory affinities

The Engine adapter supplies the challenge's dataset/model hashes and validates its references against observed records. The pure game checks structure and membership; a content hash by itself does not authenticate a source assertion.

`SpatialPattern` contains an ID, assigned layer, `kind` (`filename`, `path`, or `code`), 2–8 distinct member **piece occurrence IDs**, evidence IDs, and source edge IDs. There are at most 256 patterns. A repeated layer/kind/member set is rejected even if renamed; its supporting evidence must be merged into one frozen pattern. Code patterns require source edge references. Filename and path patterns are labeled observational lexical grouping; they do not establish code reachability. The game never invents source patterns from convenient board contacts.

A pattern qualifies when every required whole occurrence is present on its assigned layer and the contact graph induced by those members is connected. Contact requires a shared voxel face. Diagonal proximity does not count, and unrelated pieces cannot bridge disconnected required members into a match. Pattern eligibility is checked after placement and after settling, before removal. Each pattern pays at most once per match.

`SpatialAffinity` contains explicit pair endpoints, assigned layer, `probabilityPpm`, `modelHash` and `featureHash`. There are at most 512 affinities; duplicate unordered pairs on the same layer are rejected. Endpoints must be distinct deck occurrences on that layer, and every affinity's model hash must match the challenge's frozen model artifact. An affinity qualifies only when those whole shapes directly share a face. The engine neither trains the learner nor changes the frozen affinity during a match.

The fixed `spatial-prior/v1` scoring policy is part of the challenge hash:

| Component | Points |
|---|---|
| Completed plane | 1,000 |
| Completed axis line | 100 |
| Observed pattern with two members | 250 |
| Observed whole pattern with 3–8 members | 250 per member |
| Learned affinity at or below 500,000 ppm | 0 |
| Learned affinity above 500,000 ppm | `floor(25 × (ppm − 500000) / 500000)`, at most 25 per pair |
| All learned affinity bonuses together | At most 100 per match |

Filename/path points are reported separately as `ObservedLexicalPoints`. Recorded code pairs use `GroundedRelationshipPoints`; larger recorded code patterns use `GroundedWholePatternPoints`. Learned points are always `LearnedAdvisoryPoints`, with model/feature hashes and an explicit inferred interpretation. The neutral threshold, cap and ordinal affinity-payment order are frozen policy. Each pair pays at most once, including zero-point contacts, so settling or repeated contacts cannot farm score. No online co-clear count feeds back into this match's score.

## Deterministic computer baseline

`SpatialOpponent.ChooseMove` and `Play` use the same legal commands, transition function and scoring as other players. Their name is **FileMapper deterministic native baseline**. Generated moves are never attributed to Astra, SOL or another external competitor.

The default `SpatialSearchBudget` is 2,048 evaluated transitions per move, beam width eight and two-ply lookahead. Supported bounds are 1–20,000 evaluations, beam width 1–32, and one or two plies. First-level exploration uses half the budget for two-ply search, followed by fixed equal second-level quotas for the selected beam. Unused quota is not silently converted into extra visibility or another competitor's allowance.

Actions are enumerated by ordinal orientation ID, ascending X, then ascending Z. Evaluation uses a fixed lexicographic order: avoid top-out; maximize resulting score; maximize grounded code points; minimize covered empty cells; minimize maximum occupied height; minimize summed column heights. Equal evaluations select the first root action in that stable order. The search policy hash commits to the configuration, evaluation and tie-breaking rules. No wall-clock limit or random seed influences completed choices.

Every decision reports evaluated actions, expanded nodes, enumerated legal commands, truncation, selected evaluation, policy hash and starting state hash. Bounded enumeration can miss a better placement; this is a reproducible heuristic, not an optimal solver. Cancellation returns an incomplete decision/run without applying a partial choice. A run's recorded moves replay through the public game API to the same final state.

External named competitors must receive the same frozen challenge, geometry, scoring policy, lookahead visibility and declared resource allowance. Their actual submissions, failures and stopping conditions must be recorded separately. This engine does not manufacture such runs or claim agent superiority from its native baseline fixtures.

## Author validation

A retained native C# fixture harness passed 115 checks under the pinned stable .NET 11 SDK. It covers all nine bases and four variants, with an independent quarter-turn breadth-first closure matching every deduplicated pose set; intrinsic volume; shape-identity preservation; true 3D collision/top-out; full-plane and alternate line clears; removal of voxels outside the trigger plane as part of a whole piece; rigid post-clear falling; independent observation layers; induced whole-pattern contact; separate evidence/advisory totals; once-only awards; neutral and aggregate advisory caps; malformed challenge/state guards; replay equality; deterministic opponent runs, operation limits and cancellation.

The two eight-piece native baseline fixture runs were byte-equivalent as canonical records and replayed through the public API. A single default-budget fixture decision evaluated 1,560 actions across nine expanded nodes in about 282 ms on the validation host. These timings describe that small controlled fixture, not general throughput, a trained model's quality or a completed Astra/SOL comparison. Live FileMapper adapter and browser integration are validated separately by their owners.
