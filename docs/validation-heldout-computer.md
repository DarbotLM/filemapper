# Held-out public source computer round 01

Twelve episodes across two preselected public source families were independently replayed: fresh GPT 6 Astra and GPT 6.1 SOL sessions per case, the native initial/after-Astra/after-SOL expert policies, and the unchanged default native beam opponent. All 288 frames and 106 model choices passed native replay, prefix/source/actor binding and deadline review; 118 helper invocations completed without failure.

| Arm | copilot-rust-sdk reward / patterns | claimsassist reward / patterns |
|---|---:|---:|
| GPT 6 Astra | 4150 / 11 of 13 | 4750 / 13 of 16 |
| GPT 6.1 SOL | 4600 / 12 of 13 | 4850 / 13 of 16 |
| Initial computer | -1000 / 0 of 13 | -1000 / 0 of 16 |
| Computer after Astra | 2350 / 5 of 13 | 2600 / 6 of 16 |
| Computer after SOL | 3050 / 7 of 13 | 2800 / 6 of 16 |
| Native beam | 3400 / 6 of 13 | 3450 / 7 of 16 |

The final expert policy improves over the zero-weight initial ranker, but remains below beam on both cases. Final expert and beam each match 13 of 29 patterns in total; beam's 1000 higher combined reward comes from clears. The learner is eight-feature bounded pairwise imitation, not semantic supervision. Two games do not support statistical significance, general superiority, language equivalence, CodeQL parity or filesystem accuracy claims. Computation budgets differ across full-legal-list model players, 64-candidate ranker and default beam.

The new lineage is recomputed from INITIAL using the two reviewed retained training demonstrations, in fixed Astra → SOL order, under a 17-edition manifest (14 train / 1 validation / 2 test, 11 components). No test learning occurs. Source selection protects prior assignments and exact retained-content components; it cannot rule out model-provider pretraining or unobserved forks. Case1 and case2 use 26/27-piece bounded decks rather than full-repository semantic coverage.

Evidence pins:

- Protocol: `41f1ef8c6b9919a0603b4988f0d34dfe845b425a467aca7815aeaf886475c7fc`
- Native runtime snapshot, source `294930b483706b423b288f4b61ed9a5e793be925`: `ef51f1829531743d7b6ce8f05a0fded8b92271954b0b39a2c879a1493b72237f`
- Combined independent judge: `921ed83f87f460a61bf9199024a89b814857aa087ad8aaa2b000b4991956bc86`
- Final trained policy: `8a03aedd1c0dabcf280f909863b544a01fc1b226e30ee48ba165eed4f1719614`

The self-contained report lives in the local deliverables folder `filemapper-heldout-computer-round-01/report.html`, with embedded geometry, relative evidence references and source paths/spans for all53 pieces. The companion manifest is the authority for its final reviewed hash. Session markers link named model dispatches to retained actors, not private model weights. Shared files were not access-isolated. Native per-turn wall clocks and historical workspace/terminal/network intervals were not recorded; the 3D time stack depicts ordinal turns. Review is static only; no standalone browser preview or visual interaction pass is claimed.
