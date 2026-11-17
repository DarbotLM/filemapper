# FileMapper

Local-first filesystem and semantic evidence tooling, built with C#/.NET and PowerShell. Development preview; inspect the exact release manifest and validation notes before relying on a build.

- **Filesystem:** paths, physical identities, logical/allocation measurements, hierarchy, duplicate candidates and explicit errors/completeness.
- **Storage queries:** versioned completed-snapshot facets and duplicate projections with atomic preparation, exact pagination, cancellation and live/read-only fallback. See [native storage projections](docs/storage-projections.md); the ten-million-node performance gate still requires measured acceptance.
- **Storage landscape:** orbit recorded files and folders as selectable 3D polycubes, drill into a folder, and inspect exact paths and measured bytes. Color encodes observed kind; voxel count encodes explicit byte bands. See [storage mapping](docs/storage-landscape.md); this display does not imply semantic similarity or live filesystem state.
- **Source:** bounded C#/PowerShell analysis and evidence routes; repository, ownership, dependency and artifact provenance metadata.
- **Source word atlas:** pinned Git bytes, exact lexical positions, replayable corpus receipts and a compact query projection. Compare normalized terms across rotatable capture layers and inspect their source occurrences. See [source harvesting](docs/source-harvesting.md); lexical matches do not establish semantic equivalence or learning improvement.
- **Source semantics:** analyze retained C#/PowerShell bytes through cancellable local jobs, import verified results, and inspect native 3D objects with exact source spans and separate relationship evidence. See the [local workflow](docs/source-service.md) and [native contract](docs/source-semantics.md); partial source analysis does not establish runtime behavior or CodeQL parity.
- **Interfaces:** loopback service and browser workspace, CLI, typed PowerShell, and MCP tools.
- **Task context:** bounded graph retrieval with score components, recorded connections and source citations, exact UTF-8 byte limits and cancellation. See [context workspace](docs/context-workspace.md); relevance and model-token acceptance remain separate work.
- **Learning arena:** persistent vocabulary, explicit supervised labels, auditable relevance checkpoints and native whole-shape XYZ matches. Models start untrained; game points are never training labels. See [learning workspace](docs/learning-workspace.md).
- **Spatial view:** nine canonical polycube shapes, observed snapshot layers and accessible evidence cards. No inferred motion or learned distance in the spatial atlas.
- **Public edition practice:** select a pinned edition from the word bank, freeze a bounded source-attributed 3D Tetrid arena, make legal placements, and save or reopen a replay. Use native beam or explicitly train an arena-bound imitation policy from a reviewed complete replay, with hash-linked lineage and weight updates. Practice is separate from semantic learning and held-out evaluation; no external expert policy is imported. See [edition practice](docs/edition-practice.md).

The website bundle targets [GitHub Pages](https://darbotlm.github.io/filemapper/) and includes a clearly synthetic spatial demo plus a separately reviewed public-source game report. Preparing this bundle does not establish that it has been published. The source export excludes private scans, drive inventories, screenshots, local validation outputs, tokens, build artifacts, and prior private Git history.

## Build

Use the stable SDK pinned in global.json (currently .NET 11), and PowerShell 7.6 or newer. The native module targets the PowerShell host runtime.

~~~powershell
dotnet restore FileMapper.sln --locked-mode
dotnet build FileMapper.sln -c Release --no-restore
dotnet run --project src/FileMapper.Cli -c Release -- --help
./eng/Build-Release.ps1 -RuntimeIdentifier win-x64 -OutputDirectory artifacts/release
~~~

Extract a prepared Windows release and run ./operations/Install-Local.ps1 -StageOnly, then ./operations/Install-Local.ps1 to activate it. The default service is loopback-only. Never paste its credential into the public website. See [release operations](eng/README.md) and the [public guide](site/docs.html).

## Scope and trust

An unknown value is not zero. Candidate source flows are not proven exploits, and absent findings are not proof of safety. Spatial distance has no measurement meaning. Git attribution is advisory. Hash-linked artifact manifests do not prove a build ran. Fixture results and real-drive observations are distinct; this public tree contains reusable synthetic fixtures, not private benchmark evidence.

Releases include SHA-256 manifests and CycloneDX SBOMs. Unsigned packages are explicitly development artifacts. Cross-machine reproducibility, trusted production signing, and platform acceptance require their own actual evidence.

Repository automation uses native PowerShell/C#; browser code uses HTML/CSS/JavaScript. No Python runtime dependency is introduced.

License status: no license grant has been selected. Making this source public does not itself introduce an open-source license.

