# Staged-content quest acceptance

The independent 2026-11-16 review of installed FileMapper 0.3.8 accepted **Q2.3** (candidate, sampled and full-hash stages with I/O throttling) and **Q2.4** (exact/probable/partial confidence and independently reclaimable bytes). Each earns 1,000 XP. All world bosses remain open.

The accepted implementation is source revision `b74d77a833448e675d573bea2bb84f84fa951396`. The unsigned development archive has SHA-256 `4f412ce5afc1c9f0545f1b3c0445b838db17456e01a4d034e4d714bdd250972a`. The installed release review separately verified archive/publication bytes, both running instances and preservation of existing data. No signing, publication or whole-platform completion is inferred.

| Evidence | Scope | SHA-256 |
|---|---|---|
| Independent quest reconciliation | Canonical victory conditions and tests/docs/security/demo requirements | `8bf4a30b6510dc4e823532fea715c86c975b285416b3b0c2e269985a530b2cfe` |
| Independent native execution | 24 crash, accounting and concurrency checks on frozen candidate02 | `670418dfbb1a3996f0e340ee40ecfe13e6cecba4886a08954babdfce547f2b3f` |
| Feature security/correctness review | HS-01–06 closed; separately attributed author and reviewer evidence | `81119567a952cbdad01ba06b5f4240f9f98e9c234a18e7b5d6e98a132d945178` |
| Installed release review | Bounded archive/source export and installed-state acceptance | `d0336c24d4d3e38f9d5228a18486c719e5527312f2132ae081d19e8cdb5a9097` |

Q2.3 includes deliberately colliding samples rejected by full SHA-256, independent digest/span reads, journal-validated NTFS cache reuse, restored-mtime invalidation and exFAT fallback. Retained original read receipts also establish finite shared-rate behavior: two overlapping uncached 524,288-byte reads consumed 1,048,576 bytes in 1.7795671 seconds at a shared 524,288 bytes/s rate. With a single 131,072-byte initial burst, the minimum is 1.75 seconds. A resumed 2,097,152-byte read at 262,144 bytes/s took 7.7737743 seconds against a burst-adjusted minimum of 7.75 seconds. These are finite admission-rate observations, not an indefinite throughput SLA.

Q2.4 includes shifted-content CDC and repeated-chunk multiset checks. The positive savings fixture has two physical identities, three verified names, two 1,048,576-byte allocations and no alternate streams. Retaining the largest identity yields 1,048,576 independently estimated reclaimable bytes; the additional hardlink name does not count as another copy. Actual ADS and outside-scope aliases produce unknown savings. Probable/partial matches do not invent independently reclaimable values. No deletion is authorized by an estimate.

The author suites (29 C-volume, 25 D-volume, 37 control and 11 transport checks) retain separate attribution from independent execution. The installed generated-corpus analysis and spatial/evidence-card witness provide the demo artifact. The shipped [staged-hashing contract](staged-hashing.md) documents modes, rate/budget accounting, control recovery, error coverage and savings limits. The complete original receipts remain with the staged-content delivery; the review made only read-only retained-record queries and preserved both source database hashes.

Ten-million-node query performance, native scan throughput/memory, full accessibility, signed publication and platform-wide threat review retain their own open acceptance gates.
