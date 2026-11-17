# C# semantic extraction and flow models

`CSharpExtractor` implements `ICodeExtractor`. Roslyn 5.12.0 explicitly parses C# 15 using runtime metadata only. Repository projects, binaries, build targets, analyzers and generators are never executed. Missing package/framework references remain visible compiler diagnostics. This is safe fallback compilation, not a target-framework workspace.

The graph includes declarations, external symbols, references, syntax operations and source spans; resolved/candidate call edges; CFG entry/exit, branch and unreachable blocks; and conservative exception/finally edges. A finite worklist tracks reaching definitions, uses, assignments, local allocation-site fields/properties, direct aliases, captures, expressions, parameters, returns, branch unions and reassignment kills. Finalizer mutations feed their normal continuations. Top-level programs are analyzed through their compiler entrypoint. Direct source-body calls and local functions have bounded call/return and ref/out summaries; local functions carry captured reads/writes. Recursion and depth exhaustion propagate inputs conservatively with partial evidence.

`new CSharpExtractor(models, options)` accepts trusted host SDK configuration. `CSharpFlowModels` exposes symbol-qualified source, sink, sanitizer, barrier and propagation models and eight rule definitions. Models are never discovered in the analyzed repository. Argument indexes use Roslyn parameter ordinals, preserving named/reordered binding; `-1` selects all arguments. Sanitizers remove individual rule tags from return values. Barriers must be void APIs whose reviewed model promises to throw on failure. Boolean validators are not unconditional barriers. Authorization must hold on every reaching branch.

```csharp
var models = CSharpFlowModels.Default();
models.Sinks.Add(new(new("Company.Accounts", "Delete"), "FM-CS-AUTH", [0]));
models.Barriers.Add(new(new("Company.Policy", "DemandAccess"), [], Authorizes: true));
models.Barriers.Add(new(new("Company.Policy", "DemandResourceAccess"), [], Argument: 0, Authorizes: true));
models.Sanitizers.Add(new(new("Company.Paths", "AllowedPath"), ["FM-CS-PATH"]));
var result = await new CSharpExtractor(models, new(MaxCallDepth: 2))
    .ExtractAsync(snapshotId, root, cancellationToken);
```

These models assert trusted API contracts. The host must review sanitizer implementation and authorization scope. `Authorizes:true` without an argument is an explicit global permission contract; with `Argument:0`, authorization attaches to that value version and reassignment invalidates it. Configured privileged operations require authorization even for constant/no arguments. Authentication or a suggestive method name is insufficient. Findings carry ordered source-span/reason/edge routes, assumptions, model version and truncation. `modeled-flow` is a static may-flow witness; `policy-pattern` identifies intrinsic crypto use or a missing configured authorization precondition. Neither proves exploitability.

## Supplied retained-source documents

`ExtractDocumentsAsync(snapshotId, contextId, documents, inputOptions, observedAt, cancellationToken)` accepts `SuppliedSourceDocument(LogicalPath, Bytes, ExpectedSha256, ExpectedByteLength)`. It does not enumerate a source directory, create temporary source files, restore a project, resolve repository dependencies or execute source. The separate context ID scopes symbol identity and evidence; a logical URI such as `filemapper-source://repo-<hash>/<commit>/<escaped-path>` stays a logical URI. Runtime metadata still comes from the host's trusted reference set, so this is a partial safe compilation context rather than zero filesystem access or project-faithful binding.

Shared `SuppliedSourceOptions` defaults are 256 documents, 2 MiB/document and 32 MiB total bytes. Explicit overrides are bounded at 2,000 documents, 16 MiB/document and 64 MiB total. Snapshot/context IDs are at most 4,096 UTF-16 units and logical paths at most 16,384; invalid Unicode/control characters and duplicate/conflicting logical paths reject the batch before parsing. Too many documents reject the batch. Unsupported extensions, byte/hash mismatches, byte-budget exhaustion, UTF-32, invalid UTF-8/UTF-16 and decoded NUL each produce explicit rejected-document status and diagnostics. There is no accepted text prefix or replacement decoding. Failed hash/decoding attempts consume examined-byte budget; unsupported/oversized documents are not copied. `Coverage.Complete` describes admission of every input, not semantic completeness or absence of compiler errors.

Supplied extraction also limits aggregate native nodes, edges, observations and findings through `MaxGraphRecords` (default 500,000; hard ceiling 1,000,000). Adding a new record beyond the limit throws `SourceGraphLimitException` before insertion and returns no completed analysis. Replacing an existing node/edge/observation identity does not consume another record. This is an aggregate record bound, not a process-memory limit. Existing filesystem extraction retains its earlier behavior.

`SuppliedSourceDecoder.Decode` snapshots bounded bytes, verifies length/SHA-256, and accepts strict UTF-8 or BOM-marked UTF-16 LE/BE. `DecodedSuppliedSource` retains immutable text, encoding, BOM byte count, source hash/length and a precomputed O(n) boundary map. `TryMapUtf16Span(start, length, out byteStart, out byteLength)` performs an O(1) lookup and rejects negative/out-of-range values and surrogate-interior boundaries. Text offset zero maps after the BOM; an empty span at EOF is valid. Parser UTF-16 spans remain unchanged. The maximum decoded length is 16 Mi UTF-16 units.

An optional `observedAt` is preserved exactly as host-supplied evidence time. The returned actual `StartedAt`/`FinishedAt` interval is separate and does not turn that supplied timestamp into a measured source observation. Without the override, evidence uses the normal clock. The existing filesystem `ExtractAsync` keeps its prior enumeration/decoding contract. Both paths share the same compiler/flow implementation; no parser process isolation or full CodeQL parity is claimed.

Source limits: 2,000 files, 2 MiB each, 64 MiB total; `.git`, `bin`, `obj`, `node_modules`, `.vs` excluded; child reparse points not followed. Syntax/reference entities are capped at 100,000. Flow defaults: 2,000 callables, 2,048 blocks per callable, 10,000 total block visits, 200,000 operations, 64 route steps, 64 origins and 128 reaching definitions per value, call depth two. Exhaustion emits partial evidence. Route summaries explicitly identify omitted propagation. Absence of findings on a partial graph is not a safety result.

Branch-predicate correlation, ambiguous heap/alias joins, transitive object graphs, delegate invocation, complete virtual dispatch and concurrency remain incomplete. Direct local object aliases and their observed fields flow into whole-object sinks; arbitrary external heap mutation and constructor field-to-argument mappings remain conservative. Unknown calls and ref/out writes propagate possible inputs and can produce false positives. Exception dispatch is conservative and may be infeasible. Complete global summaries, parser process isolation and production accuracy gates remain open.

## Retained acceptance

From the repository root with the pinned SDK:

```powershell
dotnet run --project src/FileMapper.Extract.CSharp/Acceptance/CSharp.Acceptance.csproj -c Release --artifacts-path ../../work/csharp-round3 -p:RestoreLockedMode=false -- src/FileMapper.Extract.CSharp/Acceptance/Corpus docs/csharp-acceptance.json
```

Corpus source is parsed as data, excluded from executable/library compilation. The entrypoint checks labeled cases, CFG categories, route/node/edge/evidence integrity, transactional `SaveAnalysis` persistence and visible budgets. Custom SQL/deserialization/authorization APIs are explicit model contracts. See [rules](../../docs/csharp-rules.md) and [execution report](../../docs/csharp-acceptance.json). The authored corpus does not establish held-out production precision or CodeQL parity.

Official version evidence: [NuGet 5.12.0](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.12.0), stable release dated 2026-11-13; [C# 15 documentation](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15). Acceptance records the loaded assembly version and exercises C# 15 collection-constructor syntax.
