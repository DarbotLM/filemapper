# Build and operate a release

`Build-Release.ps1` restores checked-in lockfiles and uses isolated .NET `--artifacts-path` output, so a running development service cannot lock release build binaries. It publishes the requested RID, builds the native PowerShell module, produces a CycloneDX 1.6 SBOM from resolved NuGet assets with dependency edges and SHA-512 package hashes, records the framework versions from the published runtime configuration, inventories shipped file SHA-256 hashes, and emits a sorted ZIP with fixed entry timestamps. Service and CLI packages are self-contained by default; `-SelfContained:$false` opts into requiring an installed matching runtime. The PowerShell module uses the PowerShell host runtime. Cross-machine bit-for-bit reproducibility remains a measured release gate, not an assumed property.

The separate Robocopy bootstrap is published into `worker/` with its runtime and metadata. Service and CLI resolve this sibling executable; the packaged worker needs no system SDK. It waits for parent job containment before launching the system Robocopy binary. The package does not redistribute Robocopy or grant additional action roles. The exact stable SDK in `global.json` is required. `-RequireCleanSource` rejects uncommitted release inputs.

```powershell
./eng/Build-Release.ps1 -OutputDirectory artifacts/release -RuntimeIdentifier win-x64
./eng/Install-Local.ps1 -PackageDirectory artifacts/release/package -StageOnly
./eng/Install-Local.ps1 -PackageDirectory artifacts/release/package
```

For a concrete repeatability check, run `./eng/Test-ReproducibleRelease.ps1 -OutputDirectory artifacts/reproducibility`. It builds two independent outputs from clean source and compares every archive byte, retaining both archives and `reproducibility.json`. It does not infer cross-machine repeatability.

## Prepare public source and Pages

`Build-Publication.ps1` exports a curated source tree without the existing `.git` history, raw local validation outputs, screenshots, private inventory, credentials or generated binaries. It substitutes the public README and enforces `public-source-policy.json`. The public site uses only invented demo records. A pattern/allowlist privacy check is a review aid, not a universal secret-detection guarantee.

```powershell
./eng/Build-Pages.ps1 -OutputDirectory artifacts/pages
./eng/Build-Publication.ps1 -OutputDirectory artifacts/publication -ReleaseDirectory artifacts/release
```

The final export requires clean source; `-AllowDirtySource` is only for an explicitly marked review draft. A supplied release must match the exported revision and have `sourceDirty=false`. The result contains a SHA-256 publication manifest, a source tree, a static Pages tree, and selected ZIP/checksum/manifest/SBOM release files. It does not copy build logs or perform network publication. No new license grant is introduced; this is public source, not an asserted open-source license.

Place `eng/pages-workflow.yml.template` at `.github/workflows/pages.yml` in the public repository, set Pages to GitHub Actions, and let the workflow publish only the generated `artifacts/pages` tree. The template follows GitHub's [custom Pages workflow](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages), with a `github-pages` environment and restricted deployment permissions. Do not publish the entire working directory. Repository creation, remote push, Pages configuration and release upload require an authenticated account with access to the requested organization; preparation does not imply those remote actions succeeded.

Unsigned output is explicitly marked `development`/`unsigned`. To require signing, supply an existing accessible code-signing certificate in the current user's certificate store and a timestamp service:

```powershell
./eng/Build-Release.ps1 -OutputDirectory artifacts/signed-release `
  -CertificateThumbprint $thumbprint -TimestampServer $timestampUrl -RequireSignature
./eng/Install-Local.ps1 -PackageDirectory artifacts/signed-release/package `
  -RequireSignature -SignerThumbprint $thumbprint
```

The build signs FileMapper executables/assemblies and PowerShell operations using Authenticode, then signs the exact release manifest bytes with detached CMS. Verification validates every manifest hash, rejects extra/missing files and reparse paths, checks the trusted CMS signature, and pins the requested publisher thumbprint. No certificate or private key is generated or uploaded by these scripts. The signed flow requires validation with the actual release certificate before it can pass the production signing gate. Timestamped signatures are inherently time-dependent.

Installation stages an immutable content-addressed version directory and probes the CLI, service and UI on an isolated database before touching the installed service. It backs up SQLite through the engine's online backup API, verifies health before activating the installation manifest, and attempts to restore previous data/service/module on activation failure. A maintenance admission lock spans that entire operation. A short-lived hashed owner marker and child-only environment token let explicit installer startup/migration/restore operations run; ordinary jobs remain blocked. Old binaries and the pre-upgrade database backup are preserved. `-DataDirectory`, `-InstallRoot` and `-ModuleRoot` allow fully isolated installations. `-StageOnly` never stops or changes the active installation.

This concurrency guarantee requires all clients accessing that database to use the maintenance protocol. Stop older CLI/module processes and other direct SQLite writers before upgrading from a version without it. The installer stops the recorded legacy service itself; it does not claim to discover or fence unrelated legacy processes. Live handles define ownership, so a leftover lock filename never means an owner is active. Installer bypass requires the exact database path, matching token hash, live owner PID/start time, and held exclusive maintenance lock. The marker is removed in `finally`; a retained child token cannot bypass future maintenance after that session ends. Health verification matches the child PID (or the Windows listener owner for legacy service responses). Recovery failures report the preserved backup location and do not claim restoration succeeded.

The activation output provides the exact restart command. It uses the packaged `operations/Start-Local.ps1` and the active installation manifest. `operations/Get-Diagnostics.ps1` exports health, runtime and database-file sizes without data contents or credentials. `-IncludeLogs` includes bounded log tails with known credential values and bearer strings redacted; paths and application error details may remain in those logs.

Installation and restart wait up to 120 seconds for the actual child process and OS listener to become healthy. `-StartupTimeoutSeconds` accepts 5–300 seconds for workspaces whose immutable source-index verification or database initialization needs longer. Timeout still fails activation and invokes recovery; it does not bypass health, ownership or package verification. Packaging also rejects mismatched release, UI and PowerShell module versions.

Primary format/tool references: [CycloneDX JSON schema](https://cyclonedx.org/schema/bom-1.6.schema.json), [Microsoft Authenticode signing](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-authenticodesignature?view=powershell-7.6).
