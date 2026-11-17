[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$RuntimeIdentifier = 'win-x64',
    [bool]$SelfContained = $true,
    [string]$CertificateThumbprint,
    [string]$TimestampServer,
    [switch]$RequireSignature,
    [switch]$RequireCleanSource,
    [switch]$SkipArchive
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/release-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { if (Get-ChildItem -LiteralPath $output -Force) { throw 'Release output must be empty; existing artifacts are never overwritten.' } }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$payload = Join-Path $output 'package'
New-Item -ItemType Directory -Path $payload | Out-Null
$buildLog = Join-Path $output 'build.log'
$buildArtifacts = Join-Path $output 'build'
Push-Location $repo
try {
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE) { throw 'A source revision is required for release provenance.' }
    $dirty = [bool](& git status --porcelain --untracked-files=normal)
    if ($RequireCleanSource -and $dirty) { throw 'This release requires a clean committed source tree.' }
    $sourceEpoch = if ($env:SOURCE_DATE_EPOCH) { [long]$env:SOURCE_DATE_EPOCH } else { [long](& git show -s --format=%ct HEAD) }
    $sdkVersion = (& dotnet --version).Trim()
    $pinnedSdk=(Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw|ConvertFrom-Json).sdk.version
    if($sdkVersion -ne $pinnedSdk -or $sdkVersion.Contains('-')){throw 'A release requires the exact stable SDK pinned in global.json.'}
    $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $shellHtml = Get-Content (Join-Path $repo 'src/FileMapper.Service/wwwroot/index.html') -Raw
    $shellVersion = [regex]::Match($shellHtml, 'data-ui-version="([^"]+)"').Groups[1].Value
    $moduleVersion = (Import-PowerShellDataFile (Join-Path $repo 'src/FileMapper.PowerShell/FileMapper.psd1')).ModuleVersion
    if ($shellVersion -ne $version -or $moduleVersion -ne $version) {
        throw 'Release, UI and PowerShell module versions must match before packaging.'
    }
    $selfContainedValue=$SelfContained.ToString().ToLowerInvariant()
    $pathMap="-p:PathMap=$buildArtifacts=/_/Artifacts%2C$repo=/_/FileMapper"
    & dotnet restore FileMapper.sln --locked-mode --artifacts-path $buildArtifacts "-p:SelfContained=$selfContainedValue" *> $buildLog
    if ($LASTEXITCODE) { throw "Locked restore failed; see $buildLog" }
    & dotnet build FileMapper.sln -c Release --no-restore --artifacts-path $buildArtifacts -p:ContinuousIntegrationBuild=true $pathMap *>> $buildLog
    if ($LASTEXITCODE) { throw "Release build failed; see $buildLog" }
    foreach ($project in @(@{name='FileMapper.Service';folder='service'},@{name='FileMapper.Cli';folder='cli'},@{name='FileMapper.Actions.Robocopy';folder='worker'})) {
        & dotnet publish (Join-Path $repo ('src/' + $project.name)) -c Release -r $RuntimeIdentifier --no-restore --artifacts-path $buildArtifacts --self-contained $selfContainedValue -p:ContinuousIntegrationBuild=true $pathMap -o (Join-Path $payload $project.folder) *>> $buildLog
        if ($LASTEXITCODE) { throw "Publish failed for $($project.name); see $buildLog" }
    }
    # SDK compressed-asset metadata otherwise records this build's wall-clock time.
    $assetModified=[DateTimeOffset]::FromUnixTimeSeconds($sourceEpoch).UtcDateTime.ToString('r',[Globalization.CultureInfo]::InvariantCulture)
    foreach($endpointFile in Get-ChildItem -LiteralPath $payload -Filter '*.staticwebassets.endpoints.json' -Recurse -File) {
        $endpoints=Get-Content -LiteralPath $endpointFile.FullName -Raw | ConvertFrom-Json -AsHashtable
        if($endpoints -isnot [Collections.IDictionary] -or -not $endpoints.ContainsKey('Version') -or $endpoints.Version -ne 1 -or -not $endpoints.ContainsKey('Endpoints') -or $endpoints.Endpoints -isnot [array]) {
            throw 'Unsupported static web asset endpoint metadata; deterministic normalization requires a reviewed schema.'
        }
        foreach($endpoint in $endpoints.Endpoints) {
            if($endpoint -isnot [Collections.IDictionary] -or -not $endpoint.ContainsKey('ResponseHeaders') -or $endpoint.ResponseHeaders -isnot [array]) {throw 'Malformed static web asset response headers.'}
            foreach($header in $endpoint.ResponseHeaders) {
                if($header -isnot [Collections.IDictionary] -or -not $header.ContainsKey('Name') -or -not $header.ContainsKey('Value')) {throw 'Malformed static web asset response header.'}
                if($header.Name -eq 'Last-Modified'){$header.Value=$assetModified}
            }
        }
        Write-FileMapperJson $endpointFile.FullName $endpoints
    }
    $module = Join-Path $payload 'module'
    New-Item -ItemType Directory -Path $module | Out-Null
    Copy-Item (Join-Path $buildArtifacts 'bin/FileMapper.PowerShell/release/FileMapper.*') -Destination $module
    $operations = Join-Path $payload 'operations'
    New-Item -ItemType Directory -Path $operations | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'Install-Local.ps1'),(Join-Path $PSScriptRoot 'Start-Local.ps1'),(Join-Path $PSScriptRoot 'Get-Diagnostics.ps1'),(Join-Path $PSScriptRoot 'Release.Common.ps1') -Destination $operations
    $certificate = $null
    if ($CertificateThumbprint) {
        if (-not $IsWindows) { throw 'Authenticode release signing requires Windows.' }
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
        if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -lt [DateTime]::Now) { throw 'Signing certificate must be unexpired and have an accessible private key.' }
        if (-not $TimestampServer) { throw 'TimestampServer is required for signed production artifacts.' }
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { ($_.Name -like 'FileMapper*' -and $_.Extension -in @('.exe','.dll')) -or $_.Extension -eq '.ps1' }) {
            $signature = Set-AuthenticodeSignature -LiteralPath $file.FullName -Certificate $certificate -TimestampServer $TimestampServer -HashAlgorithm SHA256
            if ($signature.Status -ne 'Valid') { throw "Artifact signing failed for $($file.Name): $($signature.Status)" }
        }
    } elseif ($RequireSignature) { throw 'A code-signing certificate and timestamp server are required; this build is unsigned.' }
    $packageInstructions=@'
The per-user installer supports Windows. Extract the complete ZIP, open PowerShell 7.6 or newer in this directory, then run:

```powershell
.\operations\Install-Local.ps1
```

The installer verifies this package, stages and health-checks it, then activates it. It prints the local URL, CLI path and reusable restart command. No source checkout or SDK is needed to install a self-contained package. Use `-StageOnly` for validation without stopping the installed service. Stop legacy CLI/module writers before upgrading a database from a version without the maintenance protocol.

The default installation is per-user under LocalAppData/FileMapper. `-InstallRoot`, `-DataDirectory`, `-ModuleRoot` and `-Port` support separate installations. `operations/Get-Diagnostics.ps1` collects bounded diagnostics without credential contents. For a signed release, verify the expected publisher using `-RequireSignature -SignerThumbprint <publisher-thumbprint>`.
'@
    $packageStatus="Release channel: development. Signing: $(if($certificate){'signed'}else{'unsigned'}). Runtime identifier: $RuntimeIdentifier. Self-contained: $selfContainedValue."
    [IO.File]::WriteAllText((Join-Path $payload 'README.md'),"# FileMapper $version`n`n$packageStatus`n`n$packageInstructions`n",[Text.UTF8Encoding]::new($false))
    $packages = @{}; $dependencySets = @{}
    foreach ($project in @('FileMapper.Service','FileMapper.Cli','FileMapper.PowerShell','FileMapper.Actions.Robocopy')) {
        $assets = Get-Content (Join-Path $buildArtifacts "obj/$project/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in ($assets.libraries.Keys | Sort-Object)) {
            $library = $assets.libraries[$key]
            if ($library.type -ne 'package') { continue }
            $parts = $key.Split('/'); $ref = "pkg:nuget/$($parts[0])@$($parts[1])"
            if (-not $packages.ContainsKey($ref)) {
                $component = [ordered]@{type='library';'bom-ref'=$ref;name=$parts[0];version=$parts[1];purl=$ref}
                if ($library.sha512) { $component.hashes = @([ordered]@{alg='SHA-512';content=[Convert]::ToHexString([Convert]::FromBase64String($library.sha512)).ToLowerInvariant()}) }
                $packages[$ref] = $component
                $dependencySets[$ref] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            }
        }
        foreach ($target in $assets.targets.Values) {
            foreach ($key in $target.Keys) {
                if ($target[$key].type -ne 'package') { continue }
                $parts = $key.Split('/'); $ref = "pkg:nuget/$($parts[0])@$($parts[1])"
                if (-not $target[$key].ContainsKey('dependencies')) { continue }
                foreach ($name in $target[$key].dependencies.Keys) {
                    $resolved = @($target.Keys | Where-Object { $_.StartsWith($name + '/', [StringComparison]::OrdinalIgnoreCase) })
                    foreach ($dependency in $resolved) { $depParts=$dependency.Split('/'); [void]$dependencySets[$ref].Add("pkg:nuget/$($depParts[0])@$($depParts[1])") }
                }
            }
        }
    }
    $runtimeComponents = @{}
    foreach ($runtimeConfig in Get-ChildItem -LiteralPath $payload -Filter '*.runtimeconfig.json' -Recurse -File) {
        $options=(Get-Content -LiteralPath $runtimeConfig.FullName -Raw | ConvertFrom-Json -AsHashtable).runtimeOptions
        $frameworks=@()
        foreach($key in @('framework','frameworks','includedFrameworks')) {if($options.ContainsKey($key)){$frameworks+=@($options[$key])}}
        foreach($framework in $frameworks) {
            $ref="pkg:generic/$($framework.name)@$($framework.version)?rid=$RuntimeIdentifier"
            if(-not $runtimeComponents.ContainsKey($ref)) {
                $runtimeComponents[$ref]=[ordered]@{type='framework';'bom-ref'=$ref;name=$framework.name;version=$framework.version;purl=$ref;properties=@([ordered]@{name='filemapper:deployment';value=$(if($SelfContained){'bundled'}else{'required-host-runtime'})},[ordered]@{name='filemapper:runtime-identifier';value=$RuntimeIdentifier})}
            }
        }
    }
    $artifactFiles = Get-FileMapperFileList $payload
    $fileComponents = @($artifactFiles | ForEach-Object { [ordered]@{type='file';'bom-ref'=('file:' + $_.path);name=$_.path;hashes=@([ordered]@{alg='SHA-256';content=$_.sha256})} })
    $rootRef = "pkg:generic/FileMapper@$version"
    $dependencies = @([ordered]@{ref=$rootRef;dependsOn=@(@($packages.Keys)+@($runtimeComponents.Keys) | Sort-Object)})
    foreach ($ref in ($packages.Keys | Sort-Object)) { $dependencies += [ordered]@{ref=$ref;dependsOn=@($dependencySets[$ref] | Sort-Object)} }
    foreach ($ref in ($runtimeComponents.Keys | Sort-Object)) { $dependencies += [ordered]@{ref=$ref;dependsOn=@()} }
    $sbom = [ordered]@{bomFormat='CycloneDX';specVersion='1.6';version=1;metadata=[ordered]@{timestamp=[DateTimeOffset]::FromUnixTimeSeconds($sourceEpoch).ToString('O');component=[ordered]@{type='application';'bom-ref'=$rootRef;name='FileMapper';version=$version};properties=@([ordered]@{name='filemapper:source-revision';value=$revision},[ordered]@{name='filemapper:source-dirty';value=$dirty.ToString().ToLowerInvariant()},[ordered]@{name='filemapper:sdk';value=$sdkVersion})};components=@($packages.Keys | Sort-Object | ForEach-Object {$packages[$_]}) + @($runtimeComponents.Keys | Sort-Object | ForEach-Object {$runtimeComponents[$_]}) + $fileComponents;dependencies=$dependencies}
    Write-FileMapperJson (Join-Path $payload 'sbom.cdx.json') $sbom
    $manifest = [ordered]@{schemaVersion=1;product='FileMapper';version=$version;runtimeIdentifier=$RuntimeIdentifier;selfContained=$SelfContained;sourceRevision=$revision;sourceDirty=$dirty;sdkVersion=$sdkVersion;sourceDateEpoch=$sourceEpoch;releaseChannel='development';signing=[ordered]@{status=$(if($certificate){'signed'}else{'unsigned'});thumbprint=$(if($certificate){$certificate.Thumbprint}else{$null})};files=(Get-FileMapperFileList $payload)}
    Write-FileMapperJson (Join-Path $payload 'release-manifest.json') $manifest
    if ($certificate) {
        Add-Type -AssemblyName System.Security.Cryptography.Pkcs
        $content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes((Join-Path $payload 'release-manifest.json')))
        $signed = [Security.Cryptography.Pkcs.SignedCms]::new($content,$true)
        $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
        $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
        $signed.ComputeSignature($signer,$true)
        [IO.File]::WriteAllBytes((Join-Path $payload 'release-manifest.p7s'),$signed.Encode())
    }
    $null = Test-FileMapperPackage $payload -RequireSignature:$RequireSignature -SignerThumbprint $CertificateThumbprint
    $archivePath = $null
    if (-not $SkipArchive) {
        $archivePath = Join-Path $output "FileMapper-$version-$RuntimeIdentifier.zip"
        $zipStream = [IO.File]::Open($archivePath,[IO.FileMode]::CreateNew)
        $zip = [IO.Compression.ZipArchive]::new($zipStream,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) {
                $entry = $zip.CreateEntry([IO.Path]::GetRelativePath($payload,$file.FullName).Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
                $input = $file.OpenRead(); $target = $entry.Open()
                try { $input.CopyTo($target) } finally { $target.Dispose();$input.Dispose() }
            }
        } finally { $zip.Dispose();$zipStream.Dispose() }
        (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath ($archivePath + '.sha256')
    }
    [pscustomobject]@{packageDirectory=$payload;archive=$archivePath;manifestHash=(Get-FileHash (Join-Path $payload 'release-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant();signing=$manifest.signing.status;packageComponents=$packages.Count;runtimeComponents=$runtimeComponents.Count;artifactFiles=$artifactFiles.Count;sdk=$sdkVersion;sourceDirty=$dirty}
} finally { Pop-Location }
