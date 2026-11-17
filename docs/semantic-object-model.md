# Semantic objects and canonical shapes

`FileMapper.Semantics` is a native .NET library with only a project reference to `FileMapper.Domain`. `SemanticObjects.Create(snapshot, node, observedFrom, observedTo)` projects supplied evidence; it does not collect new evidence, run a trained model, reconstruct missing history, or attribute data to the current host. Its version is `semantic-object/v1`.

## Identity and hashes

`somId` is `som:` plus the SHA-256 of canonical JSON `{model:"som-identity/v1",nodeId:<exact underlying ID>}`. `SemanticObjects.SomIdFor(nodeId)` computes the same value without creating a projection. Neither snapshot, path, label, token, color nor rotation participates. This preserves the collector's identity decision, including its limitations: a `path-fallback` ID cannot establish rename continuity; two hard links sharing a native file ID share a semantic identity; two different IDs at one path remain different objects. IDs are not Unicode-normalized or case-folded.

`observationId` separately hashes snapshot ID, exact node ID, complete raw source hash, and the supplied observation window under `som-observation/v1`. The same identity can therefore have many observations. A node's source evidence IDs remain literal references; this library does not manufacture Evidence records or assert that a supplied reference resolves.

| Hash field | Exact input and scope |
| --- | --- |
| `hashes.rawSourceHash` | Full canonical JSON of the source MapNode, including every property. No token/output limit is applied. This is a metadata hash, not a claim to have read or hashed file bytes. |
| `hashes.contentHash` | `{kind,name,path,properties}`. Top-level observation and presentation property names listed below are excluded. Node ID and snapshot are excluded. This is semantic source-content identity, not underlying filesystem content identity. A renamed path can change this hash while retaining `somId`. |
| `hashes.geometryHash` | `{model:"oasis-canonical-geometry/v1",cells:[...]}` using the supplied canonical pose, sorted by X, Y, Z. Color and display rotation do not participate. |
| `hashes.objectHash` | The entire resulting semantic object with `hashes.objectHash` replaced by the empty string. All other hash values are included. This documents a reproducible preimage without self-reference. |

The content exclusions are exact, ordinal top-level property names: `evidenceId`, `evidenceIds`, `evidenceRefs`, `observationId`, `observationIds`, `observedAt`, `observedFrom`, `observedTo`, `snapshotId`, `shape_type`, `shape_color`, `somshape`, `rotation`, `axis`. Nested domain data remains intact. A presentation change can change the raw observation/object hash while leaving `somId` and `contentHash` unchanged. Hashes describe the immutable projection at creation; a caller that modifies a returned record must recompute its relevant hash.

`CanonicalJson.Serialize<T>` uses web-style camel-case .NET property names, recursively sorts object keys using ordinal comparison, preserves array order, emits compact JSON, and never routes JSON numbers through `double` or `decimal`. JsonElement number spelling survives exactly, including large integers, tiny exponents and trailing zeroes: `1`, `1.0`, and `1e0` intentionally have different hashes. This is the versioned FileMapper canonical representation, not RFC 8785/JCS. Duplicate object keys, undefined JSON values, invalid UTF-16 strings, surrogate `char` values or dictionary keys, unsupported types and nonfinite numbers fail rather than silently coerce. A surrogate `char` cannot represent a Unicode scalar by itself; arrays of individual surrogate chars are also rejected. Valid U+FFFD remains legitimate data. Default System.Text.Json nesting limits apply. `Sha256<T>` hashes canonical UTF-8 JSON; `HashUtf8(string)` hashes exact UTF-8 text without JSON wrapping or Unicode normalization. Both return all 64 lowercase hex digits.

## Fields and time

The public C# `SemanticObject` record contains `ModelVersion`, `SomId`, `ObservationId`, `Label`, `SomeType`, `ShapeType`, `ShapeColor`, `SomShape`, `SomeShapes`, `RawSourceNode`, `EvidenceRefs`, `SourceReference`, `SnapshotWindow`, `ObservedWindow`, `Dimensions`, `Bounds`, and `Hashes`. JSON uses camel case except the explicitly named `some_type`, `shape_type`, `shape_color`, `somshape`, and `some_shapes` fields. `some_type` is the unmodified node Kind.

`rawSourceNode` is a complete, detached JsonElement copy; later changes to the caller's property dictionary cannot alter it. `sourceReference` contains snapshot ID, node ID and supplied path. `evidenceRefs` contains only IDs present in `evidenceId`, `evidenceIds` or `evidenceRefs`, with JSON Pointer source fields. The snapshot window reports the snapshot's actual stored root, start, finish, status and completeness. `observedWindow` reports only the explicitly supplied `from`/`to`, with basis `supplied-observation-range`; absent timestamps remain null with basis `unknown`. The snapshot's timestamps are never substituted for observations. A supplied reversed observation range is rejected.

Every object exposes workspace, repository, task, terminal, computer, network and chip dimensions. A nonempty direct `<dimension>Id` property (preferred), or `<dimension>` property, supplies its exact JSON value and source field with status `source-asserted`. Otherwise status is `unknown` with null value/source. A snapshot root does not prove a workspace or repository; a collector running on this computer does not prove the historical object's computer. `source-asserted` does not mean independently verified.

## Deterministic lexical word shapes

`SemanticObjects.Tokenize(string)` returns a `string[]`: Unicode NFKC normalization, invariant lowercase, then maximal Unicode letter/digit runs with combining marks attached to a preceding run. Punctuation, underscores and path separators delimit runs. Camel-case words are not split; no stemming, learned embeddings, language inference or semantic similarity claim is made. This uses the pinned .NET runtime's Unicode tables. Original text remains in the raw source. For example, composed/decomposed accent forms and compatibility forms can intentionally produce the same lexical token while keeping different raw hashes and exact node identities.

The source strings are Kind, Name, Path and recursively visited string property values from the content projection. Numeric values and property names are not tokenized. Each `some_shapes` entry has a normalized `token`, occurrence `count`, exact JSON Pointer `sources`, and a `wordId` (`word:` plus full SHA-256 of `{model:"lexical-word/v1",token}`). The full word ID preserves normalized-word identity; the token remains available to compare or verify it.

The `features` record contains rune length, letter/digit counts, non-ASCII and combining-mark flags, and first/last-digit flags. `signature9` uses these nine deterministic bits; `signatureBits` renders them in nine binary digits:

| Bit, least significant first | Feature |
| --- | --- |
| 0 | Length 1–3 |
| 1 | Length 4–7 |
| 2 | Length at least 8 |
| 3 | Contains a letter |
| 4 | Contains a decimal digit |
| 5 | Contains a non-ASCII rune |
| 6 | Contains a combining mark after normalization |
| 7 | Begins with a decimal digit |
| 8 | Ends with a decimal digit |

`signatureModel` is `lexical-flags-9/v1` and `signatureCollisionsAllowed` is always true. There are at most 512 signatures and many words share them; neither a signature nor a shape is a unique word or object identifier. Word shape is a deterministic visual category selected by `signature9 % 9` over `I,O,T,L,J,S,Z,pillar,stair`.

The output retains the first 256 distinct normalized tokens in ordinal order. `bounds` exposes `maxTokens`, `retainedTokens`, exact `totalDistinctTokens`, `totalTokens` and `truncated`. Counts require scanning the complete supplied metadata; this cap bounds the emitted vocabulary, not the size of the input or raw hash. Overflow affects the word-shape projection only. No truncated raw hash is ever presented as a full-source hash.

## Geometry and presentation

`ShapeCatalog.Shapes` returns defensive copies of all nine supplied geometries, including their original voxel ordering from `wwwroot/spatial-renderer.js` CELLS. All canonical cells have Z=0. `Voxel` is a public `(X,Y,Z)` readonly record struct. `ForKind` maps node categories to shape keys; unknown kinds use `pillar`. `ColorFor` returns one of the existing nine palette colors.

`Rotate(shape, turns)` performs counterclockwise XY quarter turns, wraps positive/negative turns modulo four and translates minimum X/Y/Z to zero. Four turns recover the canonical geometry; reflection is never a rotation. This is the native board's planar rotation convention. The existing 3D renderer has a separate axis/pose convention; its display pose does not redefine canonical geometry or identity. Returned `somshape` starts at `{shape,axis:0,rotation:0}`. Changing a pose or palette never changes the underlying node or normalized-word identity.

## Observed native validation

On 2026-11-15 the library built in Release using the pinned stable .NET SDK 11.0.100 with zero warnings and errors. A standalone C# harness under `work/semantic-model/object-harness` passed 74 assertions on .NET runtime 11.0.0. It compared every canonical voxel and its order against the actual existing JavaScript CELLS literal, checked four/negative turns and S/Z and L/J reflection distinctions, and attempted to mutate returned geometry arrays.

The same run verified snapshot-independent identity, rename versus path-reuse distinctions, separately scoped observation IDs, reproducible hashes, exact large/tiny JSON numbers and number spelling, duplicate-key and malformed-surrogate rejection, Unicode normalization versus exact node identity, expected nine-bit collisions, full hashes beyond the 256-token projection limit, detached raw source data, actual observation timestamps, and explicit unknown dimensions. Independent review identified that the default `char` serializer could replace a malformed surrogate with U+FFFD; the fix adds explicit char validation, and eight regression cases cover direct, property, boxed, nullable, array and dictionary-key chars while preserving valid characters. This is focused native model validation; it does not claim service, database, UI or benchmark acceptance.
