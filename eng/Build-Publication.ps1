[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$ReleaseDirectory,[switch]$AllowDirtySource)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent));$output=[IO.Path]::GetFullPath($OutputDirectory)
$comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
function Resolve-PublicationInput([string]$Relative){
    if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)){throw 'Public inputs must be repository-relative paths.'}
    $full=[IO.Path]::GetFullPath((Join-Path $repo $Relative));$prefix=$repo
    if(-not [IO.Path]::EndsInDirectorySeparator($prefix)){$prefix+=[IO.Path]::DirectorySeparatorChar}
    if(-not $full.Equals($repo,$comparison) -and -not $full.StartsWith($prefix,$comparison)){throw 'Public input escapes the repository root.'}
    # Checking only the leaf misses a normal child underneath a junction. Inspect every
    # component, including the tree root and repository root, before enumerating/reading.
    $current=$full
    while($true){
        $item=$null
        try{$item=Get-Item -LiteralPath $current -Force -ErrorAction Stop}
        catch{if($_.CategoryInfo.Category -ne [Management.Automation.ErrorCategory]::ObjectNotFound){throw}}
        if($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Public input has a reparse component: '+$Relative)}
        if($current.Equals($repo,$comparison)){if($null -eq $item){throw 'Repository root is missing.'};break}
        $parent=[IO.Path]::GetDirectoryName($current)
        if([string]::IsNullOrEmpty($parent) -or $parent.Equals($current,$comparison)){throw 'Public input has no validated repository ancestor.'}
        $current=$parent
    }
    return $full
}
. (Resolve-PublicationInput 'eng/Release.Common.ps1')
if($output.Equals($repo,$comparison) -or ($output.StartsWith($repo+[IO.Path]::DirectorySeparatorChar,$comparison) -and -not $output.StartsWith((Join-Path $repo 'artifacts')+[IO.Path]::DirectorySeparatorChar,$comparison))){throw 'Export outside the source tree or inside its artifacts directory to avoid recursive self-inclusion.'}
if(Test-Path -LiteralPath $output){if(Get-ChildItem -LiteralPath $output -Force){throw 'Publication output must be empty.'}}
$policy=Get-Content -LiteralPath (Resolve-PublicationInput 'eng/public-source-policy.json') -Raw|ConvertFrom-Json
$revision=(& git -C $repo rev-parse HEAD).Trim();if($LASTEXITCODE){throw 'Git source revision unavailable.'}
$dirty=[bool](& git -C $repo status --porcelain --untracked-files=normal)
if($dirty -and -not $AllowDirtySource){throw 'Public final export requires clean source. Use AllowDirtySource only for a clearly marked review draft.'}
[IO.Directory]::CreateDirectory($output)|Out-Null;$source=Join-Path $output 'source';[IO.Directory]::CreateDirectory($source)|Out-Null
function Copy-Public([string]$Relative){
    $input=Resolve-PublicationInput $Relative
    if(-not(Test-Path -LiteralPath $input)){return}
    if((Get-Item -LiteralPath $input).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Public input is a reparse point.'}
    $target=Get-FileMapperChildPath $source $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
    Copy-Item -LiteralPath $input -Destination $target
}
foreach($relative in @($policy.rootFiles)+@($policy.documentation)){Copy-Public $relative}
foreach($tree in $policy.trees){
    $path=Resolve-PublicationInput $tree;if(-not(Test-Path -LiteralPath $path)){continue}
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($path)
    while($pending.Count){
      $directory=Resolve-PublicationInput ([IO.Path]::GetRelativePath($repo,$pending.Pop()))
      foreach($item in Get-ChildItem -LiteralPath $directory -Force){
        if($item.Name -in $policy.excludedDirectories){continue}
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Public source cannot contain reparse paths.'}
        if($item.PSIsContainer){$pending.Push($item.FullName);continue}
        if($item.Extension -notin $policy.allowedExtensions){throw ('Unreviewed source extension: '+[IO.Path]::GetRelativePath($repo,$item.FullName))}
        Copy-Public ([IO.Path]::GetRelativePath($repo,$item.FullName))
    }}
}
Copy-Item -LiteralPath (Resolve-PublicationInput $policy.rootReadmeSource) -Destination (Join-Path $source 'README.md')
$sourceCheck=& (Join-Path $PSScriptRoot 'Test-PublicTree.ps1') -Path $source -Kind Source
$pages=Join-Path $output 'pages';$null=& (Join-Path $PSScriptRoot 'Build-Pages.ps1') -OutputDirectory $pages
$release=$null
if($ReleaseDirectory){
    $releaseRoot=[IO.Path]::GetFullPath($ReleaseDirectory);$package=Join-Path $releaseRoot 'package'
    $release=Test-FileMapperPackage $package
    if($release.sourceDirty){throw 'Dirty release payload cannot be offered as a final public release.'}
    if($release.sourceRevision -ne $revision){throw 'Release payload does not match this source revision.'}
    $null=& (Join-Path $PSScriptRoot 'Test-PublicTree.ps1') -Path $package -Kind Release
    $destination=Join-Path $output 'release';[IO.Directory]::CreateDirectory($destination)|Out-Null
    $archive=Join-Path $releaseRoot ('FileMapper-'+$release.version+'-'+$release.runtimeIdentifier+'.zip')
    $null=& (Join-Path $PSScriptRoot 'Test-ReleaseArchive.ps1') -Archive $archive -PackageDirectory $package
    foreach($file in @($archive,($archive+'.sha256'),(Join-Path $package 'release-manifest.json'),(Join-Path $package 'sbom.cdx.json'))){Copy-Item -LiteralPath $file -Destination $destination}
    if(Test-Path -LiteralPath (Join-Path $package 'release-manifest.p7s')){Copy-Item -LiteralPath (Join-Path $package 'release-manifest.p7s') -Destination $destination}
}
$manifest=[ordered]@{schemaVersion=1;requestedRepository='darbotlm/filemapper';requestedPages='https://darbotlm.github.io/filemapper/';state=$(if($dirty){'review-draft; not publishable'}else{'prepared; not published'});sourceRevision=$revision;sourceDirty=$dirty;historyIncluded=$false;demo='synthetic-only';benchmark='separately reviewed public-source held-out game report';licenseGrant='No new license grant is introduced by this export.';sourcePrivacy=$sourceCheck;release=$(if($release){@{version=$release.version;signing=$release.signing;runtimeIdentifier=$release.runtimeIdentifier}}else{$null});files=(Get-FileMapperFileList $output)}
Write-FileMapperJson (Join-Path $output 'publication-manifest.json') $manifest
[pscustomobject]@{directory=$output;state=$manifest.state;files=$manifest.files.Count;manifestSha256=(Get-FileHash -LiteralPath (Join-Path $output 'publication-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant();published=$false}

