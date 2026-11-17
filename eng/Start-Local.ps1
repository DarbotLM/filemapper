[CmdletBinding()]
param([string]$DataDirectory=(Join-Path $env:LOCALAPPDATA 'FileMapper/data'),[string]$InstallerToken,
    [ValidateRange(5,300)][int]$StartupTimeoutSeconds=120)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$manifestPath = Join-Path $DataDirectory 'installation.json'
$installation = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$serviceDirectory = if ($installation.ContainsKey('serviceDirectory')) { $installation.serviceDirectory } else { Join-Path $installation.installRoot 'service' }
$executable = Join-Path $serviceDirectory 'FileMapper.Service.exe'
if ($installation.ContainsKey('packageDirectory')) { $null = Test-FileMapperPackage $installation.packageDirectory }
if ($installation.ContainsKey('processId') -and $installation.processId) {
    $existing = Get-Process -Id $installation.processId -ErrorAction SilentlyContinue
    if ($existing -and $existing.Path -eq $executable) {
        $health = Invoke-RestMethod ($installation.url + '/api/health') -TimeoutSec 3
        if ($health.product -eq 'FileMapper') { Write-Output "FileMapper is already running at $($installation.url)."; return }
        throw 'The installed process is running but unhealthy; collect diagnostics before restarting it.'
    }
}
$port = ([uri]$installation.url).Port
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$arguments=@('--data',('"'+[IO.Path]::GetFullPath($DataDirectory)+'"'),'--port',$port)
$environment=@{}
if($InstallerToken){$arguments+=@('--installer-session','true');$environment['FILEMAPPER_INSTALLER_TOKEN']=$InstallerToken}
$process = Start-Process -FilePath $executable -WorkingDirectory $serviceDirectory -ArgumentList $arguments -Environment $environment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $DataDirectory "service-$stamp.stdout.log") -RedirectStandardError (Join-Path $DataDirectory "service-$stamp.stderr.log")
try { $null = Wait-FileMapperHealth $process $port $StartupTimeoutSeconds } catch { if (-not $process.HasExited) { Stop-Process -Id $process.Id }; throw }
$installation.processId = $process.Id
Write-FileMapperJson $manifestPath $installation
Write-Output "FileMapper is running at $($installation.url)."
