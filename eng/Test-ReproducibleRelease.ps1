[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$RuntimeIdentifier='win-x64')
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){if(Get-ChildItem -LiteralPath $output -Force){throw 'Reproducibility output must be empty.'}}
[IO.Directory]::CreateDirectory($output)|Out-Null
$first=& (Join-Path $PSScriptRoot 'Build-Release.ps1') -OutputDirectory (Join-Path $output 'first') -RuntimeIdentifier $RuntimeIdentifier -RequireCleanSource
$second=& (Join-Path $PSScriptRoot 'Build-Release.ps1') -OutputDirectory (Join-Path $output 'second') -RuntimeIdentifier $RuntimeIdentifier -RequireCleanSource
$a=Test-FileMapperPackage $first.packageDirectory;$b=Test-FileMapperPackage $second.packageDirectory
if($a.sourceRevision -ne $b.sourceRevision -or $a.sourceDateEpoch -ne $b.sourceDateEpoch -or $a.sdkVersion -ne $b.sdkVersion){throw 'The builds do not have identical source/toolchain/time provenance.'}
if(-not ('FileMapper.Release.ByteComparison' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'Compare-FileBytes.cs')}
$equal=[FileMapper.Release.ByteComparison]::Equal($first.archive,$second.archive)
$result=[ordered]@{schemaVersion=1;sourceRevision=$a.sourceRevision;sdk=$a.sdkVersion;runtimeIdentifier=$RuntimeIdentifier;signing=$a.signing.status;sourceDateEpoch=$a.sourceDateEpoch;firstSha256=(Get-FileHash -LiteralPath $first.archive -Algorithm SHA256).Hash.ToLowerInvariant();secondSha256=(Get-FileHash -LiteralPath $second.archive -Algorithm SHA256).Hash.ToLowerInvariant();byteIdentical=$equal;scope='Two isolated unsigned builds on this host. Not cross-machine reproducibility or repeatable timestamped signatures.'}
Write-FileMapperJson (Join-Path $output 'reproducibility.json') $result
if(-not $equal){throw 'The two complete archives differ; do not claim reproducibility.'}
$result
