# Native Context regression runner

From the repository root, using .NET 11:

```powershell
dotnet run --project eng/Context.Acceptance/Context.Acceptance.csproj -c Release -- <new-empty-fixture-directory>
```

The runner requires one new or empty directory. It creates approximately 12 MB of owned SQLite fixtures via public ingestion, runs bounded assertions, writes `review-results.json` plus representative Context/SARIF payloads, and returns exit code 0 only when every check passes. It preserves all output for inspection and never scans user drives or modifies installed state. Use a fresh fixture path on each invocation.

Checks cover recorded graph citations and ranking components, exact serialized UTF-8 budgets, Unicode query limits, two-hop boundaries, missing evidence, hubs/cycles, pinned reads, no current-source substitution, logical nonmutation, cancellation during native SQL, callback cleanup before pooled connections are reused, and SARIF's actual Engine version. The receipt records hashes of loaded assemblies. No Python, PowerShell module installation, server, network request, or external test package is required.

This is reusable product regression automation derived from a sealed independent review. A successful run is not fresh independent evidence, a relevance or machine-learning score, top-20 precision acceptance, a model-token budget, or a full campaign gate. HTTP, MCP, CLI argument parsing, and PowerShell transport require their own integration checks.
