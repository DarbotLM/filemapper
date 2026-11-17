# Persistent native pattern learning

FileMapper's pattern learner is a separate, versioned model over observed semantic inputs. It does not change `semantic-object/v1`, native SOM identity, stored filesystem/code edges, or the original semantic game's scoring. A learned relevance score is advisory. It never establishes a filesystem relationship, code path, command execution, host identity, or other authoritative fact.

There are two supervised heads: `storage-group` and `code-path`. Training requires a binary label with `human`, `controlled-fixture`, or `reviewed-dataset` provenance and a source identifier/rationale. Missing edges, similar paths, game clears and unlabeled encounters do not generate relevance labels. These provenance fields record the supplied claim; they are not independent authentication of a label's correctness.

Unlabeled observations still grow a persistent vocabulary and an indexed occurrence history. Whole-shape hashes, words, ordered path/command sequences, and recorded adjacent token pairs can become descriptive patterns. Unordered token sets never become execution sequences. Training events contribute learning-encounter counts but cannot insert source-observation occurrences. No command is executed by this learner.

## Native contracts

The pure core is in `FileMapper.Semantics/PatternLearning*.cs`:

- `PatternLearning.Create(config, partitionPolicy, epoch)` creates neutral weights and an immutable genesis checkpoint.
- `AssignPartition(policy, groupKey)` and `AssignmentHash(...)` implement the sealed group split.
- `PatternLearningFeatures.Extract(config, input)` returns bounded sparse integer features, their source-input hash, collisions, truncation and missing-context disclosures.
- `Apply(state, event, expectedCheckpointHash)` accepts only the next sequence and exact preceding audit/checkpoint commitment. It returns a new state, vocabulary deltas and source occurrences, leaving the old state unchanged.
- `Freeze(state)` preserves a checkpoint and its exact weights. `Predict(frozen, input, head)` returns an advisory score and signed feature contributions. `PredictMany(..., cancellationToken)` evaluates at most 4096 pairs after one model validation.
- `HashEvaluationEvents(events, cancellationToken)` hashes the concatenation of canonical event JSON plus LF, one bounded event at a time. `Evaluate(frozen, manifest, events, cancellationToken)` verifies that commitment and produces held-out metrics without updating weights.

`PatternLearningState` contains checkpoint metadata and a separate weights document. The checkpoint commits `WeightsHash`, configuration/feature/partition hashes, epoch, sequence, counters, preceding checkpoint, last event hash and audit root. Checkpoint and weights hashes are SHA256 of their canonical JSON documents, with no self-hash field. Observation-only events change the audit/checkpoint while retaining exactly the same weights hash. Equal hashes commit content, not its authenticity.

Context dimensions are explicit `workspace`, `repository`, `task`, `terminal`, `computer`, `network` and `session` values. Status `unknown` requires null value/source. `observed` and `declared` require a nonempty value and source. A captured app-session identifier is session context, not proof that a shell command ran. The adapter must preserve that distinction.

Token status is `unknown` or `observed`. Unknown tokens have empty arrays and `Order = "unknown"`; observed tokens require provenance and order `recorded` or `unordered`. The learner uses supplied strings ordinally and performs no additional Unicode normalization or tokenization. Path inputs must be root-relative observed lexical tokens supplied by the adapter. Command inputs must come from recorded command metadata, such as parsed AST command names, not assumptions about a terminal. Observation input permits `Right = null`; relevance training/prediction requires a pair.

## Objective and exact updates

Each head is a linear logistic relevance classifier with cross-entropy objective

`L = -y log(p) - (1-y) log(1-p)`, with `p = sigmoid(w · x)`.

The implementation is an explicit fixed-point approximation to that objective. Weights and probabilities use scale 1,000,000; features use scale 1000. The default learning rate is 25,000, representing 0.025. For each retained feature, the exact integer update is:

`deltaWeightQ = roundAway(rateQ * (label * 1,000,000 - probabilityQ) * featureQ / 1,000,000,000)`.

The next weight is projected onto the configured closed interval, default `[-8,000,000, 8,000,000]`. Logits are clipped to the same ±8 real-valued range. Fixed source tables for sigmoid and softplus at 1/16 logit intervals, rounded once to 1e-6, are linearly interpolated using integer arithmetic. The table hash, algorithm version and rounding rule are part of the configuration commitment. Training does not call platform `Math.Exp` or `Math.Log`; same canonical inputs and order reproduce identical integer weights and hashes. These are quantized, clipped logistic updates, not a claim of exact real-arithmetic optimization or calibrated probabilities.

Default features use 4096 bins per head, with at most 128 active candidate features. Structural features include bias, shared shape/geometry, lexical set overlap and recorded path-prefix fraction. Other bounded features include individual shape keys, lexical tokens and recorded adjacent tokens. Structural slots are reserved; other keys use a deterministic SHA256-derived bin. Hash collisions are disclosed and bounded by clipped aggregation. Nine-bit signatures are not used as identifiers. Raw SOM/node/snapshot IDs, absolute roots, group keys, labels, timestamps and context values are excluded from predictive features.

Training order is intentionally meaningful for online SGD. The persisted sequence and audit chain define that order. A different ordering is a different training history, not a reproducibility failure. Any future algorithm, table, feature or token policy change requires a new explicit version/configuration.

## Sealed groups, epochs and evaluation

The partition policy commits a cohort ID, seed hash, and bucket boundaries. The assignment preimage is canonical JSON of `{ schema: "filemapper.pattern-group-assignment/1", policyHash, groupKey }`. SHA256 of its exact UTF-8 bytes supplies an unsigned big-endian integer from the first eight bytes, modulo 10000. Defaults assign buckets 0–7999 to `train`, 8000–8999 to `validation`, and 9000–9999 to `test`. Group strings are not case-folded or normalized. A supplied partition flag cannot override the derived assignment.

The curator/adapter must group aliases, related source families and repeated views consistently before sealing the cohort. The deterministic split does not discover that two differently named groups actually share a source. Changing group keys to move difficult examples into training invalidates the evaluation design. A deliberately stratified benchmark may select a different sample proportion from the policy's bucket proportions; it must disclose that selection.

Only training-group labels update weights. A model lineage keeps one partition policy. A new cohort starts a new learner with neutral genesis weights, rather than transferring a trained model into a newly relabeled holdout. An epoch can have an open end or an explicit end sequence. The first event of a successor epoch commits its exact parent checkpoint and start sequence. This permits later training-group observations to update successor checkpoints without retroactively changing a frozen match or evaluation.

Evaluation accepts 1–100,000 explicitly labeled validation/test events after the frozen checkpoint's sequence. It rejects duplicate event IDs/sequences, wrong partitions, altered dataset bytes and sequence-boundary violations. Its manifest commits dataset ID, partition policy, partition, temporal range, event-stream hash and label policy. Metrics retain per-head cases/confusion counts, quantized log-loss sum/mean, exact squared-error sum and Brier mean, plus precision/recall when their denominators are nonzero. Missing head coverage is visible as zero cases, not a measured success. Evaluation never feeds labels or metrics into the model. Threshold selection/tuning should use validation; a held-out test is reported once against frozen candidates.

The author's small controlled tests demonstrate deterministic mechanism and correct update direction. They do not establish useful relevance on arbitrary workspaces. Claims of improved storage grouping or code-path relevance require independent held-out labels, frozen baselines and reported coverage. Distribution drift, sparse labels, proxy-label errors, poisoning and feature collisions can all reduce utility.

## Persistence, source occurrences and retries

`FileMapper.Domain/PatternLearningPersistence.cs` defines persistence DTOs. `MapStore.PatternLearning.cs` depends on Domain only; it does not load the learner. Mathematical validation/replay belongs to Semantics and the Engine adapter. `InitializePatternLearning()` is an explicit additive migration and does not modify base `MapStore.Initialize`. All learning reads open an existing read-only database connection; missing databases/schemas return absent or empty state without creating files or tables.

`CreatePatternLearner` persists genesis metadata and content-addressed weights. `CommitPatternLearning` holds writer admission and one SQLite transaction: exact expected-head comparison, immutable epoch/group checks, event/audit/JSON commitments, checkpoint insertion, vocabulary/source/context projections and head advancement either all commit or all roll back. An identical historical event retry returns its original receipt before comparing the current head. Conflicting duplicate bytes or projections fail. The Engine can use `GetPatternLearningEvent` to resolve a request retry before computing another update. A concurrent head change requires reload and deterministic replay; it is not silently reordered.

Weights are content-addressed separately from checkpoints. Unlabeled occurrences reuse the existing weights blob, so there is no 8192-weight copy per observation. Immutable old checkpoints remain readable by hash. Context JSON is likewise content-addressed and indexed by its explicitly supplied dimensions.

Three levels are deliberately distinct:

1. A source-observation/pattern row identifies one recorded semantic source, including native SOM/node/snapshot identifiers and the actual nullable observed time range.
2. A session/source/pattern occurrence records that source being encountered in a recorded or unknown session.
3. An event encounter records capture time and exact context, allowing task/context changes within a session without rewriting earlier provenance.

Vocabulary `EncounterCount` counts retained token/shape encounters, including supervised pair encounters. `SourceObservationCount` counts distinct source-observation/pattern rows and is not inflated when the same source is seen in another session. Occurrence pages count matching encounter rows in `Total` and distinct session/source/pattern occurrences in `TotalSourceOccurrences`. Source observation times are never replaced by capture time. Time filtering selects either capture times or known observed interval endpoints; unknown observed endpoints do not satisfy an observed-time predicate.

Read APIs include `ListPatternLearners`, `GetPatternLearningHead`, arbitrary checkpoint/weights/event lookup, paged events/vocabulary and `ReadPatternLearningOccurrences` with pattern, SOM, source observation, snapshot, session, dimension, unknown-dimension and time filters. Each page is bounded to 500 rows. Full provenance stays in event and context documents rather than being reconstructed from a display label.

`VerifyPatternLearningJournal` checks a pinned read view of exact stored JSON hashes, sequence/audit/checkpoint/weights references, immutable group assignments, projection commitments, context/time indexes and, for a complete journal, aggregate vocabulary counters. It reports checked/total events, truncation, completeness and problems. Reaching `maxEvents` never yields a complete-integrity claim. This storage check does not substitute for replaying the mathematical learner. Hash checks do not defend against an actor authorized to rewrite all data and recompute every hash.

## Bounds and operating limits

Inputs allow at most 64 tokens and 16 source references per token field, two subjects, 128 UTF-16 code units per token, 1 MiB canonical events, and 2 MiB stored model/event documents. Context documents are limited to 16 KiB. Each commit projects at most 1024 terms and 1024 occurrences. Default feature extraction retains 128 candidates; configuration permits 256 maximum and 256–16384 power-of-two bins. There are at most one billion events/updates per learner, one million vocabulary terms, one hundred million source-observation/pattern rows, and one trillion encounters per term. Bounds fail explicitly rather than silently evicting evidence or wrapping counters.

Prediction and evaluation support cancellation between bounded records. Native SQLite operations remain synchronous; cancellation and page limits do not promise a hard CPU or memory deadline inside SQLite. Writer admission can reject learning commits while another FileMapper writer owns the database. Large histories need explicit retention/export/new-cohort policy; this implementation does not silently delete old checkpoints or labels.

Games consume a frozen table of pair scores generated by the Engine. The table commits the model, weights/features and reward policy. Later explicitly labeled training events affect only future checkpoints/tables. No game clear automatically trains a relevance head, and no learned relationship is inserted into the authoritative graph.
