[CmdletBinding()]
param([Parameter(Mandatory)][string]$Archive,[Parameter(Mandatory)][string]$PackageDirectory)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$null=Test-FileMapperPackage $PackageDirectory
$expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach($file in Get-FileMapperFileList $PackageDirectory){$expected.Add($file.path,$file)}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$stream=[IO.File]::OpenRead([IO.Path]::GetFullPath($Archive));$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read)
try{
    foreach($entry in $zip.Entries){
        if(-not $expected.ContainsKey($entry.FullName) -or -not $seen.Add($entry.FullName)){throw 'Archive has unexpected or duplicate entry.'}
        $record=$expected[$entry.FullName];if($entry.Length -ne $record.length){throw 'Archive entry length differs from payload.'}
        $input=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)).ToLowerInvariant()}finally{$input.Dispose()}
        if($hash -cne $record.sha256){throw 'Archive entry bytes differ from verified payload.'}
    }
    if($seen.Count -ne $expected.Count){throw 'Archive omits a payload file.'}
}finally{$zip.Dispose();$stream.Dispose()}
$archiveHash=(Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum=(Get-Content -LiteralPath ($Archive+'.sha256') -Raw).Trim().Split(' ')[0]
if($archiveHash -cne $checksum){throw 'Archive checksum sidecar differs.'}
[pscustomobject]@{passed=$true;entries=$seen.Count;sha256=$archiveHash;scope='Every ZIP entry matched verified payload bytes; no extraction or code execution.'}
