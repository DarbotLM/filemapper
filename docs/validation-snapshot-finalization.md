# Snapshot finalization validation

The finalization query now computes physical identity aggregates once with a row-value assignment. It retains the existing snapshot schema, completeness rules, null allocation handling and terminal status behavior. The query-plan improvement is not a measured production speedup.

An independent native review compared the exact old and new SQL over 1,200 deterministic mixed snapshots and four terminal statuses using SQLite 3.53.4 and the production index set. Updated fields matched. Both versions rejected integer overflow without changing the running snapshot. Fixtures included aliases, file and stream identities, directories, symlinks, zero and unknown allocation, errors and incomplete observations.

The application lifecycle checks also covered 20 fixture/status combinations and idempotent finalization timestamps. Normalized snapshot, entry and observation results matched the baseline. The dedicated acceptance project has a locked dependency graph and can be run with the pinned .NET SDK.

Independent evidence SHA-256: `6552cb6fe58375d7633ad5daa96acbbff7ce14dc921a6e26d35aa8749bbe2b01`. The independent fixture program hash is `896ea4f3fe8d24505dbae242ae251dade4f7a30f24fc18b4fb06e26646caca9f`.

These are finite semantic and query-plan checks. They do not establish native scan memory, a 10-million-entry finalization runtime, query latency, controlled hardware performance or complete production acceptance. The separate 10-million-node population run used an earlier source revision and cannot measure this change.
