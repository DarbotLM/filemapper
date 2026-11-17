[CmdletBinding()]
param([string]$DataDirectory=(Join-Path $env:LOCALAPPDATA 'FileMapper/data'),[string]$OutputDirectory,[switch]$IncludeLogs)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Get-Location) ('filemapper-diagnostics-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Diagnostics output must be a new directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$installation = Get-Content -LiteralPath (Join-Path $DataDirectory 'installation.json') -Raw | ConvertFrom-Json -AsHashtable
$health = $null; $errorText = $null
try { $health = Invoke-RestMethod ($installation.url + '/api/health') -TimeoutSec 3 } catch { $errorText=$_.Exception.GetType().FullName }
$process = Get-Process -Id $installation.processId -ErrorAction SilentlyContinue
$summary = [ordered]@{schemaVersion=1;collectedAt=[DateTimeOffset]::UtcNow.ToString('O');product='FileMapper';version=$installation.version;url=$installation.url;health=$health;healthError=$errorText;processPresent=[bool]$process;processWorkingSet=$(if($process){$process.WorkingSet64}else{$null});runtime=[Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;os=[Runtime.InteropServices.RuntimeInformation]::OSDescription;databaseFiles=@(Get-ChildItem -LiteralPath $DataDirectory -File | Where-Object Name -like 'filemapper.db*' | Select-Object Name,Length,LastWriteTimeUtc);logsIncluded=[bool]$IncludeLogs;tokensIncluded=$false}
Write-FileMapperJson (Join-Path $output 'diagnostics.json') $summary
if ($IncludeLogs) {
    $secretValues = @(Get-ChildItem -LiteralPath $DataDirectory -Filter '*.token' -File | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw).Trim() } | Where-Object Length -gt 0)
    foreach ($log in Get-ChildItem -LiteralPath $DataDirectory -Filter '*.log' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 4) {
        $text = (Get-Content -LiteralPath $log.FullName -Tail 1000) -join "`n"
        foreach ($value in $secretValues) { $text=$text.Replace($value,'[REDACTED]') }
        $text = [regex]::Replace($text,'(?i)Bearer\s+[A-Za-z0-9._~+/=-]+','Bearer [REDACTED]')
        [IO.File]::WriteAllText((Join-Path $output $log.Name),$text)
    }
}
Write-Output $output
