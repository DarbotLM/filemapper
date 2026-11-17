# Literal-table flow regression corpus

`table-flow-2.1.0-cases.json` is a separate, versioned set of 32 development cases for `fm-ps-local-flow/2.1.0`. It does not replace the original 51-case `cases.json`, any independent judge corpus, or frozen learning/bakeoff evidence. These cases are authored regressions, not an estimate of held-out accuracy or full PowerShell coverage.

The corpus covers definite member/index overwrite, case-insensitive literal keys, aliases and alias reassignment, one/both branch writes, compound writes, dynamic keys, local function mutation, nested tables, earlier scalar copies, non-string keys, opaque method candidates, composite-key ambiguity, key/value content channels, adapted Keys/Values and literal entries that shadow them. The bounded-trace case requires exactly 16 findings with explicit partial/truncated metadata.

Every positive case additionally requires its expected original source kind in a native witness. This prevents a dynamic member's unrelated input source from masking a lost Read-Host flow. The existing acceptance runner validates graph/evidence/ordered routes and persists each result through production `MapStore.SaveAnalysis`. Source text is parsed, never invoked.

Run from the repository with the pinned SDK, using a fresh output directory outside the repository:

```powershell
dotnet build eng/PowerShell.Acceptance -c Release --artifacts-path ../../work/ps-table-flow-validation
dotnet ../../work/ps-table-flow-validation/bin/PowerShell.Acceptance/release/PowerShell.Acceptance.dll samples/semantic-powershell/table-flow-2.1.0-cases.json ../../work/ps-table-flow-validation/table-fixtures ../../work/ps-table-flow-validation/table-results.json
dotnet ../../work/ps-table-flow-validation/bin/PowerShell.Acceptance/release/PowerShell.Acceptance.dll samples/semantic-powershell/cases.json ../../work/ps-table-flow-validation/original-fixtures ../../work/ps-table-flow-validation/original-results.json
```

Cases marked `vulnerable` mean an input reaches a configured sensitive argument under the catalog assumptions. Conservative candidates are allowed for dynamic/opaque behavior; they do not assert exploitability. Safe cases require absence of the specified flow. Remaining arbitrary object, imported command, looping, exception and session limitations are documented in the extractor README.
