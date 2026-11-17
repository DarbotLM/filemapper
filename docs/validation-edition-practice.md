# Edition practice validation

The public bank can now freeze one eligible pinned edition into a saved practice arena. Human moves, native beam suggestions, per-word source spans and replay artifacts use the native game kernel. Saved arenas remain replayable after catalog reconnect or service restart. This feature does not import a trained expert checkpoint or update a semantic learner.

Independent review covered the combined backend and browser-module candidates, an isolated Release build, HTTP read/learn capabilities, rejection of client paths and invalid quotas, a real small public edition, source attribution, saved replay export, restart, cancellation and UI request lifecycle. Browser-module checks used a controlled DOM harness; installed browser observations require their own receipt.

The accepted review pins backend `61e9f4f6309f8823fcd853c5e178de7f6cac8504` and UI `f49b177`, including their earlier candidate commits. Revised review SHA-256: `d7e31f8e359096001cc4dfb18451d74c0c01c92bcc41baf31411963a8d7f8d28`. The retained initial hold is `904b05b98550933bd7a1f813b98f220218492b8dafc4847dd382233521b50043`.

The first candidate was held: an eligible 4,077,903,872-byte edition exceeded the shared 30-second deadline and returned HTTP 409 without saving an arena. A 35,794,944-byte edition succeeded in 1.08 seconds. The revised candidate retains complete SHA-256 verification, gives only practice freeze a 180-second cooperative budget, and aligns the browser's timeout to 190 seconds with an explicit Cancel control. Other arena operations retain 30 seconds and the shared two-operation limit remains.

One revised attempt on the same large edition succeeded in 119.52 seconds and stored exactly one arena with eight word-source records. A separate request on a 1,201,037,312-byte edition was canceled after one second; the server returned 499 and no arena was observed afterward. These are observations under concurrent machine load, not speedup claims or guarantees for other hardware.

Sealed edition reads use SQLite immutable URI mode under file read guards. A controlled provider test distinguished sealed main-database rows from an active WAL; paths containing spaces, percent/hash characters and Unicode also passed. Before/after sidecar checks remain. A full practice-level transient-WAL interleaving was not independently exercised, so that stronger claim is not made.

The review also fixed two UI provenance and lifecycle issues: unchanged reopened replays preserve the original declared competitor, and canceled operations suppress late responses. Continuations refer to the prior replay but do not assert server-verified ancestry. Interrupted writes may have committed; the UI directs users to refresh saved artifacts before retrying. Earlier failed evidence remains retained.

A later cold-start review found that the initial UI waited for the word catalog before loading saved arenas. The corrected UI loads these independently and offers Refresh editions. An independent controlled harness loaded an arena and replay while the edition request remained pending, then covered catalog failure, retry and view destruction. This focused review is bound by SHA-256 `12013245f81de70c98fb2c4990b03d3303b6c7854f40dceef32e1b0245202cca`; it did not repeat the native freeze or assert browser acceptance.
