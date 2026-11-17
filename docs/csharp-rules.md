# C# rules and validation scope

Model: `csharp-flow-v1`. Public string/object/array/Stream parameters are untrusted source assumptions by default. Console input and environment reads are sources. Secret tags apply only to configured environment names containing SECRET, PASSWORD, TOKEN, API_KEY, PRIVATE_KEY or CONNECTION_STRING; values are never retained. Hosts can replace the predicate or disable public-parameter sources.

| Rule | Family / CWE | Default sinks or model requirement | False-positive / sanitizer notes |
|---|---|---|---|
| FM-CS-COMMAND | Process execution / CWE-78 | Resolved Process.Start inputs | Fixed executables and structured arguments may be intentional; shell behavior is not inferred. |
| FM-CS-SQL | SQL text / CWE-89 | DbCommand.CommandText; resolved SqlClient constructors | Parameter values remain separate from query text; provider models require review. |
| FM-CS-PATH | Path traversal / CWE-22 | File read/write/open/delete; FileStream construction | Combine/GetFullPath propagate. Canonicalization alone is not a sanitizer. |
| FM-CS-DESERIALIZE | Unsafe deserialization / CWE-502 | Resolved BinaryFormatter/NetDataContractSerializer | Ordinary JSON parsing is not a sink. Exact external APIs need metadata/models. |
| FM-CS-SSRF | Network destinations / CWE-918 | HttpClient destination/request arguments, including observed local request-object fields | DNS/redirect policy and arbitrary external heap mutation remain unknown. |
| FM-CS-SECRET | Secrets exposure / CWE-532 | Console/Debug output | Environment naming is a host assumption; other logging APIs need models. |
| FM-CS-CRYPTO | Crypto misuse / CWE-328 | MD5/SHA1 Create/HashData | Intrinsic policy pattern; non-security checksums may be intentional. |
| FM-CS-AUTH | Authorization gaps / CWE-862 | Host-configured privileged APIs, including constant/no-argument operations | Throwing checks must hold on all reaching paths. Barrier argument selects a resource value; omitted argument explicitly declares a global permission contract. |

Every SDK rule records severity, CWE, source kind and false-positive notes. Sanitizers remove specified rule tags, not all taint. A void barrier can sanitize selected local/parameter values, or all current values for specified rules. Boolean validators are not unconditional barriers. Resource authorization survives direct aliases but is invalidated by reassignment; branch joins intersect authorization facts. Models are trusted host configuration and never executed/discovered from analyzed repositories. External middleware authorization can make a missing-barrier result a false positive.

The ordered route records source spans, reasons, connecting edge IDs, model version and assumptions. All current findings declare partial safe-compilation scope. Branch predicates are not correlated; exception transfer is conservative. Allocation-site identities cover direct local aliases, not arbitrary heap joins or transitive objects. Direct source-body return/ref/out summaries have depth/recursion limits; unknown results propagate conservatively. Parser worker isolation, complete workspace contexts and global analysis remain open.

## Execution

The reusable acceptance entrypoint and labeled source live in `src/FileMapper.Extract.CSharp/Acceptance`. The source is parsed as data, never run. [Recorded results](csharp-acceptance.json) cover eight families plus reassignment, branch joins, loops, named arguments, call/return, local functions, fields, exception/finally paths and C# 15 syntax. Independent checks validate CFG categories, route integrity, `SaveAnalysis` persistence and operation/route budgets.

The adversarially expanded 61-case run measured 35 true positives, zero false positives and zero false negatives across 35 vulnerable and 26 safe labels. That is 100% precision/recall **on this hand-authored model-scoped fixture only**. A separate top-level program source-to-sink case also passes. SQL/deserialization/authorization cases use explicit host-configured API contracts. This is not held-out production evaluation, broad framework coverage, proof of exploitability or CodeQL parity. The World 4 boss remains open.

## Adversarial rotation

The PowerShell reviewer identified missing non-void ref/out propagation and over-broad authorization state. Source-body summaries now carry ref/out writes, unknown outputs are conservative, resource-specific barriers track value identity, and constant/no-argument privileged operations require their configured precondition. Nine retained cases cover these corrections.

The provenance reviewer identified missing ProcessStartInfo property flow, direct receiver aliases and finally mutations after try blocks. Local allocation-site identities now connect those aliases and whole-object sinks, and finally continuations transfer the post-finalizer state. Nine further cases cover bad/safe setters, object initializers, aliases, request destinations and finally mutations. The object-initializer case exposed an additional Roslyn flow-capture receiver gap, which was corrected. All new routes passed the storage layer's transactional validation.

Three further cases exercise captured reads/writes in local functions. Compiler capture keys are scoped by callable to prevent captured caller state colliding with the callee's temporary slots. The final acceptance also checks top-level-program entrypoints and a separately exhausted syntax/reference budget.
