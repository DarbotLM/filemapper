# PowerShell semantic extraction

## Supplied retained-source documents

`ExtractDocumentsAsync(snapshotId, contextId, documents, inputOptions, observedAt, cancellationToken)` uses the shared `SuppliedSourceDocument`, strict decoder, byte-span map and bounded input/status contracts documented in the C# extractor README. Defaults are 256 documents, 2 MiB/document and 32 MiB total; only `.ps1`, `.psm1` and `.psd1` are supported. It parses retained text directly with `Parser.ParseInput`, without temporary source files, a runspace, module import, repository dependency reads or source execution.

Configured `AnalyzerReportPaths` are rejected in this mode; report contents are never implicitly read. Literal dependency references remain unverified logical references. Relative references within the same `filemapper-source` repository/revision URI may receive a logical target URI. Host paths, external URIs and revision-escaping references become contextual `DependencyReference` records; they are never normalized into host paths or loaded. Source membership, module behavior and dynamic command resolution are not inferred from those records.

The existing filesystem API and explicit external-report adapter retain their prior behavior. Parser/flow models remain the versioned offline catalog documented below. PowerShell parsing is a synchronous library call without an internal cancellation parameter; cancellation is checked before and after parsing and throughout the bounded flow pass. Input/flow bounds are not process isolation or a wall-clock guarantee. Supplied observation time is preserved separately from the actual extraction interval; UTF-16 offsets are not rewritten.

`PowerShellExtractor` calls `Parser.ParseInput` and interprets AST data. It never starts a runspace, invokes a repository script, imports a module, loads PSScriptAnalyzer, or executes a custom rule. Partial scripts retain source documents and parse diagnostics. Source byte hashes bind observations to the analyzed input.

The offline `fm-ps-local-flow/2.1.0` catalog contains command names, aliases, named/positional/pipeline parameters, source models and propagation contracts. Exact parameter names, catalog aliases, unique abbreviations, literal hashtable splats and locally declared function parameters bind to graph nodes. Module-qualified catalog commands bypass local function shadowing. Literal Set-Alias/New-Alias updates are modeled in local analysis state; dynamic aliases and splats remain uncertain. Catalog names are assumptions, not proof of runtime resolution. Version 2.1.0 changes literal-table field and alias flow; historical 2.0.0 results remain historical evidence rather than being relabeled.

Ordered flow covers definitions and reads, reassignment, strings, expressions, arrays/hashtables, pipelines, returns, local function calls and inline/stored scriptblocks. Branches merge possible values. Function calls use actual arguments and separate local scope; explicit script/global writes share modeled script state with an uncertainty observation. Uncalled top-level functions are assumed entrypoints with candidate parameter sources. Remoting models `$using:` capture and the first ArgumentList position. ForEach-Object propagates values through `$_`/`$PSItem`; Where-Object does not sanitize them.

Literal `@{ }` tables have bounded identities and case-insensitive string entries. A definite `=` write to a statically resolved string member/index replaces that entry's current value; later reads and splats use the current entry rather than stale aggregate taint. Other entries and scalar values copied before the write remain unchanged. Direct table aliases share an identity, including arguments to modeled local functions. Reassigning an alias to another table does not rewrite the old object. Control-flow branches use copy-on-write table states and join possible field values; one branch's overwrite does not mutate another branch's input.

Compound assignments, dynamic/non-string keys and alternative receiver identities use conservative weak updates. Unknown entry values remain candidate parameter values. Key provenance is separate: an untrusted key alone does not turn a constant splatted value into expression text, while whole-container output retains key content, including possible secret egress. Unmodeled receiver methods may write arguments into unknown entries; they never execute. Constructor-created tables and arbitrary objects do not inherit the literal-table comparer or strong-update guarantee. Concatenation, conversions and multiple-output expressions are not mistaken for scalar constant keys. Deep/cyclic table reads produce an explicit partial unknown. Per-read memoization avoids expanding shared nested-table graphs repeatedly, and each heap read participates in the operation/cancellation budget.

Findings include ordered graph witnesses, locations, evidence, model version, completeness and assumptions. `modeled-flow` means a configured source reached a configured sink within supported local flow; it does not establish exploitability. Unknown transformations and dynamic behavior produce `candidate-flow` or partial observations.

| Rule | Modeled sink policy |
| --- | --- |
| FM-PS-0001 | Untrusted expression text reaches Invoke-Expression |
| FM-PS-0002 | Untrusted executable or ArgumentList reaches Start-Process |
| FM-PS-0003 | Untrusted target, file, arguments or value reaches remoting |
| FM-PS-0004 | Untrusted URI reaches web commands |
| FM-PS-0005 | Untrusted destination reaches file writes |
| FM-PS-0006 | Untrusted path, property or value reaches provider writes |
| FM-PS-0007 | Untrusted dynamic invocation or recognized native argument |
| FM-PS-0008 | Secret reaches network body/headers or file output |

Sources include script/function inputs, Read-Host, file/provider reads, web responses, environment variables, Get-Credential and Get-Secret. Secret-like environment names use a disclosed name heuristic. A primitive numeric cast removes expression-code injection taint only; it does not authorize process, network or filesystem targets. Non-numeric casts and unmodeled methods clear that sanitizer fact. Character reconstruction remains tainted. Regex escaping, path normalization and string casts are not general sanitizers.

Literal `.psd1` RootModule, NestedModules, RequiredModules, ScriptsToProcess and RequiredAssemblies entries become dependencies, including ModuleName specifications. Import-Module and dot-sourcing become dependency candidates. No manifest expression is evaluated. Version constraints and imported exports are unresolved. Dot-sourced file mutations are unknown; cross-file execution is not simulated.

## External diagnostics

`new PowerShellExtractor(new PowerShellAnalysisOptions(AnalyzerReportPaths: [reportPath]))` adds precomputed PSScriptAnalyzer diagnostics. `PSScriptAnalyzerReport.Import` can run separately with an explicit dictionary of analyzed source hashes.

The adapter accepts a DiagnosticRecord JSON array, or `{ "SourceHashes": { "relative.ps1": "sha256" }, "Diagnostics": [...] }`. It preserves RuleName, Message, Severity and line, including numeric severity and `Extent.StartLineNumber`. UTF-8/UTF-16 BOMs are decoded as data. Limits are 2 MiB, depth 32 and 5,000 records. Source paths must identify already hashed files inside the root. Provided hashes must match; missing hashes are explicitly unverified. Report/source hashes and external provenance remain evidence. These `external-diagnostic` findings have no synthetic taint route. Explicit reports may reside outside the root but never grant access to additional sources.

Diagnostic shape and severity are defined in PSScriptAnalyzer's [DiagnosticRecord source](https://github.com/PowerShell/PSScriptAnalyzer/blob/main/Engine/Generic/DiagnosticRecord.cs). The adapter consumes exported data without invoking [Invoke-ScriptAnalyzer](https://learn.microsoft.com/en-us/powershell/module/psscriptanalyzer/invoke-scriptanalyzer).

## Scope and budgets

Supplied-byte extraction additionally shares `SuppliedSourceOptions.MaxGraphRecords` across all its documents: 500,000 native nodes/edges/observations/findings by default, with a 1,000,000 hard ceiling. Exceeding it raises `SourceGraphLimitException` before the next record is added; no completed analysis is returned. This bounds record count rather than process memory. Filesystem extraction retains its prior budgets.

Limits are 100,000 AST/heap operations/file, 4,096 local variables, 4,096 literal-table allocation identities/file, 4,096 known string keys/table, six call/heap-read levels, 16 alternative traces/value and 64 route steps. Overflowing table keys remain unknown-entry candidates; allocation overflow loses precise table identity with an uncertainty observation. Truncation applies to both aggregate and residual object traces; witnesses retain a contiguous suffix ending at the sink and mark any omitted predecessor and reason. Shared source limits are 2 MiB/file, 2,000 files, 64 MiB total and no reparse traversal.

Loops join zero/one iteration with partial evidence, not a fixed-point proof. Exception handlers merge conservatively. Parameter-set dispatch, advanced-function pipeline parameters, full remoting ArgumentList expansion, arbitrary object mutation/aliasing, session changes and imported/native catalogs remain incomplete. Unknown .NET methods propagate receiver/argument taint without execution. Table effects from returning branches remain in conservative joins because a caller may still observe them; this can retain extra candidates in following local code. Explicit script/global variable branch effects retain the earlier conservative shared-state limitation. Custom argument transformations and validation attributes are not trusted sanitizers. Absence of a finding outside this scope is not a safety result.

Reusable corpus: `samples/semantic-powershell/cases.json`; runner: `eng/PowerShell.Acceptance`; measured results and limits: `docs/validation-powershell.md`. Shared enumeration/evidence helpers remain in the C# extractor project. Parser dependency is pinned to `System.Management.Automation` 7.6.10.
