[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,[ValidateSet('Source','Pages','Release')][string]$Kind='Source')
$ErrorActionPreference='Stop'
$base=[IO.Path]::GetFullPath($Path)
if((Get-Item -LiteralPath $base).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Public tree root is a reparse point.'}
$pending=[Collections.Generic.Stack[string]]::new();$pending.Push($base)
$count=0;$bytes=0L;$issues=[Collections.Generic.List[object]]::new()
$privateRoots=@($env:USERPROFILE,$env:HOME)|Where-Object{$_ -and $_.Length -gt 3}
while($pending.Count){
    foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force){
        $relative=[IO.Path]::GetRelativePath($base,$item.FullName).Replace('\','/')
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){$issues.Add(@{path=$relative;rule='reparse-path'});continue}
        if($item.PSIsContainer){
            if($item.Name -in @('.git','__pycache__','node_modules') -or ($Kind -ne 'Release' -and $item.Name -in @('bin','obj','data','artifacts'))){$issues.Add(@{path=$relative;rule='private-or-generated-directory'});continue}
            $pending.Push($item.FullName);continue
        }
        $count++;$bytes+=$item.Length
        if($item.Name -match '(?i)\.(token|db|db-shm|db-wal|pfx|p12|pem|key|py|pyc|pyo|log)$' -or $item.Name -match '(?i)^(installation|reader|planner|scanner|service)\.json$'){$issues.Add(@{path=$relative;rule='private-or-disallowed-file'})}
        if($Kind -ne 'Release' -and $item.Extension -match '(?i)^\.(exe|dll|pdb|zip|jpg|jpeg|png)$'){$issues.Add(@{path=$relative;rule='unapproved-binary'})}
        if($item.Length -gt 268435456){$issues.Add(@{path=$relative;rule='file-too-large-to-review'});continue}
        $raw=[IO.File]::ReadAllBytes($item.FullName)
        $texts=@([Text.Encoding]::UTF8.GetString($raw))
        if($Kind -eq 'Release'){$texts+=@([Text.Encoding]::Unicode.GetString($raw))}
        foreach($text in $texts){
            $normalized=$text.Replace('\\','\').Replace('/','\')
            foreach($private in $privateRoots){if($normalized.IndexOf($private.Replace('/','\'),[StringComparison]::OrdinalIgnoreCase) -ge 0){$issues.Add(@{path=$relative;rule='private-profile-path'});break}}
            if($normalized -match '(?i)\bD:\\(?:Github|DEV)\\' -or $text -match '(?i)d-bakeoff[\\/]'){$issues.Add(@{path=$relative;rule='private-benchmark-path'})}
            if($text -match '(?i)\b(?:ghp|github_pat)_[A-Za-z0-9_]{30,}' -or $text -match '(?i)Bearer [a-f0-9]{64}\b' -or $text -match '-----BEGIN (?:RSA |EC )?PRIVATE KEY-----'){$issues.Add(@{path=$relative;rule='credential-pattern'})}
        }
    }
}
if($issues.Count){$issues|ConvertTo-Json -Depth 8|Write-Error;throw 'Public tree privacy gate failed; no publication performed.'}
[pscustomobject]@{passed=$true;kind=$Kind;files=$count;bytes=$bytes;checks=@('no private/generated trees','no credentials or databases','no authored Python','no local profile or benchmark paths','no matched credential patterns');limit='Pattern/allowlist review, not a guarantee against every possible secret. Public files are individually hash-manifested.'}

