[CmdletBinding()]
param(
    [string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'FileMapper/app'),
    [int]$Port=4177,
    [string]$PackageDirectory,
    [string]$DataDirectory=(Join-Path $env:LOCALAPPDATA 'FileMapper/data'),
    [string]$ModuleRoot=(Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PowerShell/Modules/FileMapper'),
    [switch]$RequireSignature,
    [string]$SignerThumbprint,
    [string]$TimestampServer,
    [switch]$StageOnly,
    [ValidateRange(5,300)][int]$StartupTimeoutSeconds=120
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
if (-not $IsWindows) { throw 'The per-user installer currently supports Windows only.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Choose an unprivileged TCP port between 1024 and 65535.' }
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot);$DataDirectory=[IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
New-FileMapperPrivateDirectory $DataDirectory
# Build and verify everything before touching the running installation.
if (-not $PackageDirectory) {
    $bundledPackage=Split-Path $PSScriptRoot -Parent
    if(Test-Path -LiteralPath (Join-Path $bundledPackage 'release-manifest.json')){$PackageDirectory=$bundledPackage}
}
if (-not $PackageDirectory) {
    $build = & (Join-Path $PSScriptRoot 'Build-Release.ps1') -RequireSignature:$RequireSignature -CertificateThumbprint $SignerThumbprint -TimestampServer $TimestampServer -SkipArchive
    $PackageDirectory=$build.packageDirectory
}
$release=Test-FileMapperPackage $PackageDirectory -RequireSignature:$RequireSignature -SignerThumbprint $SignerThumbprint
if ($release.runtimeIdentifier -ne 'win-x64') { throw 'This installer requires a win-x64 release.' }
$hash=(Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'release-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$versionId=$release.version+'-'+$hash.Substring(0,16)
$versionDirectory=Get-FileMapperChildPath $InstallRoot ('versions/'+$versionId)
if (Test-Path -LiteralPath $versionDirectory) { $null=Test-FileMapperPackage $versionDirectory -RequireSignature:$RequireSignature -SignerThumbprint $SignerThumbprint }
else { New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null; Copy-Item -Path (Join-Path $PackageDirectory '*') -Destination $versionDirectory -Recurse; $null=Test-FileMapperPackage $versionDirectory -RequireSignature:$RequireSignature -SignerThumbprint $SignerThumbprint }
$service=Join-Path $versionDirectory 'service';$cli=Join-Path $versionDirectory 'cli'
& (Join-Path $cli 'filemapper.exe') --help | Out-Null
if ($LASTEXITCODE) { throw 'Staged CLI smoke check failed.' }
# Probe startup and empty-database migration on isolated data before touching installed data.
$probeData=Get-FileMapperChildPath $InstallRoot ('probes/'+[Guid]::NewGuid().ToString('N'))
New-FileMapperPrivateDirectory $probeData
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$probePort=([Net.IPEndPoint]$listener.LocalEndpoint).Port;$listener.Stop()
$probe=Start-Process -FilePath (Join-Path $service 'FileMapper.Service.exe') -WorkingDirectory $service -ArgumentList @('--data',('"'+$probeData+'"'),'--port',$probePort) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $probeData 'stdout.log') -RedirectStandardError (Join-Path $probeData 'stderr.log')
try { $null=Wait-FileMapperHealth $probe $probePort $StartupTimeoutSeconds; $page=Invoke-WebRequest "http://127.0.0.1:$probePort/" -TimeoutSec 5; if ($page.StatusCode -ne 200) { throw 'Staged UI is unavailable.' } }
finally { $probe.Refresh();if(-not $probe.HasExited){Stop-Process -Id $probe.Id;$probe.WaitForExit()} }
if ($StageOnly) { [pscustomobject]@{stage='verified';packageDirectory=$versionDirectory;manifestHash=$hash;signing=$release.signing.status}; return }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
$existingAcl=Get-Acl -LiteralPath $DataDirectory
$rules=$existingAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])
if (-not $existingAcl.AreAccessRulesProtected -or $rules.Count -ne 1 -or $rules[0].IdentityReference -ne $identity -or $rules[0].FileSystemRights -ne 'FullControl') {
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetOwner($identity);$acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($DataDirectory),$acl)
}
$manifestPath=Join-Path $DataDirectory 'installation.json'
$previous=$null;$previousBytes=$null;$oldProcess=$null
if (Test-Path -LiteralPath $manifestPath) {
    $previousBytes=[IO.File]::ReadAllBytes($manifestPath)
    $previous=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $previousService=if($previous.ContainsKey('serviceDirectory')){$previous.serviceDirectory}else{Join-Path $previous.installRoot 'service'}
    $oldProcess=Get-Process -Id $previous.processId -ErrorAction SilentlyContinue
    if ($oldProcess -and $oldProcess.Path -ne (Join-Path $previousService 'FileMapper.Service.exe')) { throw 'Recorded process ID belongs to another executable; installation state needs review.' }
    if($oldProcess) {
        $tokenFile=Join-Path $DataDirectory 'service.token'
        $headers=@{Authorization='Bearer '+(Get-Content -LiteralPath $tokenFile -Raw).Trim()}
        $jobs=Invoke-RestMethod ($previous.url+'/api/jobs') -Headers $headers -TimeoutSec 5
        if($jobs | Where-Object status -in @('running','pausing','cancelling')){throw 'Pause or finish active jobs before upgrading.'}
    }
}
$backup=Get-FileMapperChildPath $DataDirectory ('upgrade-backups/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$newProcess=$null;$stoppedPrevious=$false;$backupReady=$false;$databaseExisted=$false;$moduleTouched=$false;$module=$null;$maintenanceLock=$null;$tokenHash=$null
$maintenancePath=Join-Path $DataDirectory 'filemapper.db.maintenance.json'
try {
    $maintenanceLock=[IO.File]::Open((Join-Path $DataDirectory 'filemapper.db.maintenance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $installerToken=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $tokenHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($installerToken))).ToLowerInvariant()
    Write-FileMapperJson $maintenancePath @{databasePath=(Join-Path $DataDirectory 'filemapper.db');ownerProcessId=$PID;ownerStartTimeUtcTicks=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;tokenHash=$tokenHash}
    $writerLock=[IO.File]::Open((Join-Path $DataDirectory 'filemapper.db.writer.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        if($oldProcess){Stop-Process -Id $oldProcess.Id;$oldProcess.WaitForExit();$stoppedPrevious=$true}
        $database=Join-Path $DataDirectory 'filemapper.db'
        $databaseExisted=Test-Path -LiteralPath $database
        if($databaseExisted) {
            Invoke-FileMapperInstallerCli (Join-Path $cli 'filemapper.exe') @('backup','--database',$database,'--destination',(Join-Path $backup 'filemapper.db')) $installerToken
        }
        $backupReady=$true
    } finally {$writerLock.Dispose()}
    $stamp=[DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
    $newProcess=Start-Process -FilePath (Join-Path $service 'FileMapper.Service.exe') -WorkingDirectory $service -ArgumentList @('--data',('"'+$DataDirectory+'"'),'--port',$Port,'--installer-session','true') -Environment @{FILEMAPPER_INSTALLER_TOKEN=$installerToken} -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $DataDirectory "service-$stamp.stdout.log") -RedirectStandardError (Join-Path $DataDirectory "service-$stamp.stderr.log")
    $null=Wait-FileMapperHealth $newProcess $Port $StartupTimeoutSeconds
    $module=Get-FileMapperChildPath ([IO.Path]::GetFullPath($ModuleRoot)) $release.version
    $moduleBackup=Join-Path $backup 'module';New-Item -ItemType Directory -Path $moduleBackup | Out-Null
    if(Test-Path -LiteralPath $module){Copy-Item -Path (Join-Path $module 'FileMapper.*') -Destination $moduleBackup}
    New-Item -ItemType Directory -Force -Path $module | Out-Null
    $moduleTouched=$true
    Copy-Item -Path (Join-Path $versionDirectory 'module/*') -Destination $module -Force
    $installation=[ordered]@{processId=$newProcess.Id;url="http://127.0.0.1:$Port";installRoot=$InstallRoot;dataDirectory=$DataDirectory;tokenFile=(Join-Path $DataDirectory 'service.token');version=$release.version;release=$release.releaseChannel;packageDirectory=$versionDirectory;serviceDirectory=$service;cliDirectory=$cli;manifestHash=$hash;signing=$release.signing.status;previousPackageDirectory=$(if($previous -and $previous.ContainsKey('packageDirectory')){$previous.packageDirectory}else{$null});backupDirectory=$backup}
    $temporary=$manifestPath+'.new';Write-FileMapperJson $temporary $installation;[IO.File]::Move($temporary,$manifestPath,$true)
    Write-Output "Activated FileMapper $($release.version) ($($release.signing.status)) at http://127.0.0.1:$Port; process $($newProcess.Id)."
    Write-Output "Restart: & '$versionDirectory\operations\Start-Local.ps1' -DataDirectory '$DataDirectory'"
    Write-Output "CLI: $cli\filemapper.exe"
} catch {
    $failure=$_
    try {
        if($newProcess){$newProcess.Refresh();if(-not $newProcess.HasExited){Stop-Process -Id $newProcess.Id;$newProcess.WaitForExit()}}
        if($backupReady) {
            if($databaseExisted) {
                Invoke-FileMapperInstallerCli (Join-Path $cli 'filemapper.exe') @('restore','--database',(Join-Path $DataDirectory 'filemapper.db'),'--source',(Join-Path $backup 'filemapper.db')) $installerToken
            } else {
                $writerLock=[IO.File]::Open((Join-Path $DataDirectory 'filemapper.db.writer.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
                try { foreach($name in @('filemapper.db','filemapper.db-wal','filemapper.db-shm')) { $target=Get-FileMapperChildPath $DataDirectory $name;if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target} } } finally {$writerLock.Dispose()}
            }
        }
        if($moduleTouched) {
            Get-ChildItem -LiteralPath $module -Filter 'FileMapper.*' -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
            Copy-Item -Path (Join-Path $moduleBackup '*') -Destination $module
        }
        if($previousBytes){[IO.File]::WriteAllBytes($manifestPath,$previousBytes)}
        if($stoppedPrevious){ & (Join-Path $PSScriptRoot 'Start-Local.ps1') -DataDirectory $DataDirectory -InstallerToken $installerToken -StartupTimeoutSeconds $StartupTimeoutSeconds }
    } catch {
        throw "Activation failed ($($failure.Exception.Message)); recovery is incomplete ($($_.Exception.Message)). Preserved backup: $backup. Collect diagnostics before restarting."
    }
    throw "Activation failed; recovery completed for the state changed by this attempt. $($failure.Exception.Message)"
} finally {
    if($maintenanceLock){
        try {if((Test-Path -LiteralPath $maintenancePath) -and (Get-Content -LiteralPath $maintenancePath -Raw | ConvertFrom-Json).tokenHash -eq $tokenHash){Remove-Item -LiteralPath $maintenancePath}}
        finally {$maintenanceLock.Dispose()}
    }
}
