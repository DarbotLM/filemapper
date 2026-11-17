# Repository and artifact provenance

`GitCollector` remains an `ICodeExtractor` with language `git`. `ProvenanceOptions` provides trusted host configuration for the Git executable and collection limits. No new service or execution API is introduced.

## Evidence and identities

Git files have repository + revision + normalized repository-path identities. Change records retain old/new blob IDs, mode, status, comparison parent and similarity threshold. `RENAMED_FROM` and `COPIED_FROM` record Git's similarity heuristic; they do not prove physical identity. Commits preserve author and committer attribution and signature-header presence. Signature verification is deliberately not performed: repository-configured GPG/SSH helpers are never invoked.

`TRACKED_PATH` only says a worktree pathname exists in the HEAD tree. `CONTENT_EQUALS_REVISION` additionally requires a SHA-256 comparison against the actual Git blob. The latter is produced for bounded source reads and explicit manifest inputs. Dirty or filtered working files are not silently equated with committed bytes.

Every imported document retains a `SourceDocument`, content hash and source URI. Derived observations retain contributing observation IDs. A file hash describes its recorded read time; content hashes are cached within one collection and reused by later imports. This is not a fresh read for every relationship or an atomic filesystem snapshot.

## Safe collection

Git is resolved to an absolute trusted host installation outside the analysis root, with reparse ancestors rejected. The scanner never searches repository CWD/PATH for an executable. `GitExecutable` can select a trusted absolute host installation when the conventional installation is unavailable.

Git reads disable inherited Git environment overrides, hooks, fsmonitor, replacements, lazy fetch, external diff/textconv, configured mailmap files and automatic signature display. No checkout, restore, build, repository module import, remote fetch or artifact execution occurs. `git status` is intentionally not used because it can invoke configured clean/process filters. Worktree evidence records index-vs-HEAD observations and bounded raw-byte comparisons; `trackedClean` remains unknown, and no full porcelain-status claim is made.

Defaults are 200 commit objects, 40 first-parent change comparisons, 10,000 changes, 20,000 tracked paths, 32 blame files with at most 2,000 HEAD lines each, 2,000 candidate documents, 4 MiB per parser/Git output, 64 MiB aggregate document bytes, 10,000 imported components, 128 content-verification files at at most 256 MiB each, ten seconds per Git command, and 90 seconds overall. Directory enumeration stops at 50,000 entries in one directory, 20,000 directories, or 20,000 pending directories. Exhausted budgets become partial observations. Cancellation is cooperative between bounded reads and graph operations.

Path reads must remain under the analysis root and reject observed reparse points. The path-check/open interval is not atomic; native handle-relative path validation remains an open hardening item.

## Ownership

CODEOWNERS lookup uses `.github/CODEOWNERS`, then root `CODEOWNERS`, then `docs/CODEOWNERS`; only the first existing file is used. The last matching supported rule wins, including an empty owner list that clears a declaration. Evidence identifies the selected rule, shadowed rules and conflicting owner lists. Supported patterns include rooted paths, ordinary wildcards and directory `**`; unsupported negation, character classes and escaped patterns produce diagnostics instead of guesses. Matching is case-sensitive, matching GitHub policy semantics.

CODEOWNERS is a declared review policy with priority 100. Blame is advisory line attribution with priority 10 and a recorded HEAD revision, line count and sampled fraction. GitHub team membership, provider permissions and a separate organizational ownership-policy engine are not inferred.

## Projects and dependencies

`.sln`, `.slnx`, `.csproj`, `.fsproj` and `.vbproj` are parsed as bounded data. XML DTDs and external resolution are prohibited. Conditions and property expansion remain unresolved. NuGet `project.assets.json` and `packages.lock.json` supply resolved versions, exact TFM/RID contexts, project references and dependency edges without running MSBuild or NuGet. Ambiguous project associations remain document-scoped. Imported resolution freshness, package payload integrity and central/imported MSBuild properties are not proved by these files.

CycloneDX JSON 1.4–1.7 and SPDX JSON 2.2/2.3 imports preserve component IDs, document identity, purls, hashes, declared licenses and dependency relationships. NuGet purls link to package-version nodes as declared identifiers. File components link to observed artifacts only after full SHA-256 equality. SPDX derivation assertions use `DECLARES_GENERATED_FROM`; importing an assertion alone never manufactures verified derivation. XML/tag-value formats, external SPDX documents, vulnerability database lookups and provider attestations remain unsupported.

## Explicit artifact manifests

A file named `*.provenance.json` can carry a `filemapper-build-provenance` schema-version 1 document. Paths are relative to that document and must remain inside the analysis root. Both input and output arrays require 1–64 full SHA-256 declarations; an optional length must also match. All hashes must match before any `GENERATED_FROM` edge is emitted.

```json
{
  "kind": "filemapper-build-provenance",
  "schemaVersion": 1,
  "project": "Example.csproj",
  "targetFramework": "net11.0",
  "commit": "<full Git object ID>",
  "timestamp": "2026-01-01T12:00:00Z",
  "tool": { "name": "trusted build recorder", "version": "1.0" },
  "invocation": { "arguments": ["declared build arguments"], "environment": { "runtime": "11.0.0" } },
  "inputs": [{ "path": "src/Example.cs", "sha256": "<64 hex characters>" }],
  "outputs": [{ "path": "out/Example.dll", "sha256": "<64 hex characters>" }]
}
```

The invocation and environment receive fingerprints of their JSON representations; raw argument/environment contents are not copied into graph properties. `GENERATED_FROM` means **explicit declaration plus matching observed input/output content**, with `executionVerified=false`. It does not establish that the build ran, was hermetic, used every declared dependency, or came from a trusted signer. Artifacts are never linked to source merely from filenames, nearby projects, release-version strings or an SBOM assertion.

## Reusable acceptance

```powershell
dotnet restore src/FileMapper.Collect.Git/Acceptance/Provenance.Acceptance.csproj --locked-mode --artifacts-path ../../work/provenance-round3
dotnet run --project src/FileMapper.Collect.Git/Acceptance/Provenance.Acceptance.csproj -c Release --no-restore --artifacts-path ../../work/provenance-round3 -- ../../work/provenance-fixture ../../work/provenance-round3/acceptance.json
```

The retained C# fixture generator creates real controlled Git commits, rename/copy history, competing ownership rules and a real Roslyn-emitted DLL. Synthetic multi-TFM assets/lock/SBOM records distinguish dependency metadata from an actual restore. Negative inputs cover mismatching hashes, escaping paths, DTDs, malformed JSON, configured executable/filter helpers, bounds and cancellation. It checks directed artifact-to-source/revision/commit/owner/dependency routes and evidence referential integrity. It never runs the generated repository's MSBuild target or emitted DLL.

Known coverage limits include HEAD-reachable bounded history, first-parent merge comparisons, a bounded blame subset, unverified signatures and identities, no remote/worktree management, and incomplete submodule/LFS/ignored-file classification. See `docs/validation-provenance.md` for executed evidence and independent review findings.

Primary semantics: [Git diff-tree](https://git-scm.com/docs/git-diff-tree), [Git blame](https://git-scm.com/docs/git-blame), [GitHub CODEOWNERS](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners), [CycloneDX JSON](https://cyclonedx.org/docs/1.6/json/), [SPDX 2.3](https://spdx.github.io/spdx-spec/v2.3/).
