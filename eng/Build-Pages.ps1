[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){if(Get-ChildItem -LiteralPath $output -Force){throw 'Pages output must be empty.'}}
[IO.Directory]::CreateDirectory((Join-Path $output 'assets'))|Out-Null
foreach($name in @('index.html','docs.html','site.css','demo.js','mark.svg','heldout-report.html','heldout-report-review.json')){
    $input=Join-Path $repo ('site/'+$name)
    if((Get-Item -LiteralPath $input).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Pages inputs must not be reparse points.'}
    Copy-Item -LiteralPath $input -Destination (Join-Path $output $name)
}
foreach($name in @('spatial-renderer.js','spatial-renderer.css')){
    $input=Join-Path $repo ('src/FileMapper.Service/wwwroot/'+$name)
    if((Get-Item -LiteralPath $input).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Pages inputs must not be reparse points.'}
    Copy-Item -LiteralPath $input -Destination (Join-Path $output ('assets/'+$name))
}
$review=Get-Content -LiteralPath (Join-Path $output 'heldout-report-review.json') -Raw|ConvertFrom-Json
$reportHash=(Get-FileHash -LiteralPath (Join-Path $output 'heldout-report.html') -Algorithm SHA256).Hash.ToLowerInvariant()
if($review.schema -cne 'filemapper.heldout-report-public-review/1' -or
   $review.status -cne 'accepted-static-only' -or
   $review.reportSha256 -cne $reportHash -or
   $review.browserExecuted -cne $false -or $null -ne $review.visualScore){
    throw 'Public benchmark must match its accepted static review; a changed report requires a new review.'
}
[IO.File]::WriteAllText((Join-Path $output '.nojekyll'),'',[Text.UTF8Encoding]::new($false))
$validation=& (Join-Path $PSScriptRoot 'Test-PublicTree.ps1') -Path $output -Kind Pages
$files=@(Get-ChildItem -LiteralPath $output -Recurse -File -Force|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
[IO.File]::WriteAllText((Join-Path $output 'site-manifest.json'),([ordered]@{schemaVersion=1;data='synthetic demo and separately reviewed public-source game benchmark';network='self-hosted static assets; no local service connection or analytics';files=$files;privacyCheck=$validation}|ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{siteDirectory=$output;files=$files.Count;syntheticDemo=$true;publicSourceBenchmark=$true}

