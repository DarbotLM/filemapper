Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-FileMapperJson([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 100) + "`n", [Text.UTF8Encoding]::new($false))
}
function New-FileMapperPrivateDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item=Get-Item -LiteralPath $Path -Force
        if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Private data directories must be ordinary directories, not reparse points.'}
        return
    }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $security=[Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($identity);$security.SetAccessRuleProtection($true,$false)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path),$security)
}
function Get-FileMapperChildPath([string]$Root, [string]$Relative) {
    $base = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    if ([IO.Path]::IsPathRooted($Relative)) { throw "Package path must be relative: $Relative" }
    $resolved = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    $comparison=if([OperatingSystem]::IsWindows()){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    $prefix=if([IO.Path]::EndsInDirectorySeparator($base)){$base}else{$base + [IO.Path]::DirectorySeparatorChar}
    if (-not $resolved.StartsWith($prefix, $comparison)) { throw "Path escapes package: $Relative" }
    return $resolved
}
function Get-FileMapperFileList([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse file is not allowed in a release: $($_.FullName)" }
        [ordered]@{path=[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
}
function Test-FileMapperPackage([string]$PackageDirectory, [switch]$RequireSignature, [string]$SignerThumbprint) {
    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
    if ((Get-Item -LiteralPath $packageRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package root must not be a reparse point.' }
    if (Get-ChildItem -LiteralPath $packageRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Release directories must not contain reparse points.' }
    $manifestPath = Join-Path $packageRoot 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'FileMapper') { throw 'Unsupported release manifest.' }
    if ($manifest.version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'A numeric package version is required.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        if (-not $seen.Add($entry.path)) { throw "Duplicate package path: $($entry.path)" }
        $path = Get-FileMapperChildPath $packageRoot $entry.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package file: $($entry.path)" }
        if ((Get-Item -LiteralPath $path).Length -ne $entry.length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package hash mismatch: $($entry.path)" }
    }
    foreach ($file in Get-FileMapperFileList $packageRoot) {
        if ($file.path -notin @('release-manifest.json','release-manifest.p7s') -and -not $seen.Contains($file.path)) { throw "Unmanifested package file: $($file.path)" }
    }
    $signaturePath = Join-Path $packageRoot 'release-manifest.p7s'
    if (Test-Path -LiteralPath $signaturePath) {
        Add-Type -AssemblyName System.Security.Cryptography.Pkcs
        $content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($manifestPath))
        $signed = [Security.Cryptography.Pkcs.SignedCms]::new($content,$true)
        $signed.Decode([IO.File]::ReadAllBytes($signaturePath))
        $signed.CheckSignature($false)
        if ($signed.SignerInfos.Count -ne 1) { throw 'Exactly one trusted package signer is required.' }
        $actualThumbprint = $signed.SignerInfos[0].Certificate.Thumbprint
        if ($SignerThumbprint -and $actualThumbprint -ne $SignerThumbprint) { throw 'Package signer does not match the requested publisher.' }
        if ($manifest.signing.status -ne 'signed' -or $manifest.signing.thumbprint -ne $actualThumbprint) { throw 'Manifest signing declaration does not match signature.' }
    } elseif ($RequireSignature -or $manifest.signing.status -eq 'signed') { throw 'A valid trusted release signature is required.' }
    if ($RequireSignature -and -not $SignerThumbprint) { throw 'Production verification requires an explicitly pinned publisher thumbprint.' }
    return $manifest
}
function Test-FileMapperListenerOwner([int]$ProcessId,[int]$Port) {
    if(-not ('FileMapper.Release.WindowsTcpNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace FileMapper.Release {
    public static class WindowsTcpNative {
        // https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable
        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
        public static bool OwnsListener(int processId, int port) {
            int size=0;
            uint error=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0); // AF_INET, TCP_TABLE_OWNER_PID_LISTENER
            if(error!=0 && error!=122) throw new Win32Exception((int)error);
            for(int attempt=0;attempt<3;attempt++) {
                if(size<4 || size>64*1024*1024) throw new InvalidOperationException("Invalid native TCP table size.");
                int allocated=size;
                IntPtr table=Marshal.AllocHGlobal(allocated);
                try {
                    error=GetExtendedTcpTable(table,ref size,false,2,3,0);
                    if(error==122) continue;
                    if(error!=0) throw new Win32Exception((int)error);
                    int count=Marshal.ReadInt32(table);
                    if(count<0 || count>(allocated-4)/24) throw new InvalidOperationException("Invalid native TCP table rows.");
                    for(int index=0;index<count;index++) {
                        IntPtr row=IntPtr.Add(table,4+24*index); // Six DWORD fields in MIB_TCPROW_OWNER_PID.
                        int rawPort=Marshal.ReadInt32(row,8);
                        int localPort=((rawPort&255)<<8)|((rawPort>>8)&255);
                        // OWNER_PID_LISTENER is the authoritative listener filter; do not reinterpret its redundant dwState field.
                        if(Marshal.ReadInt32(row,4)==0x0100007f && localPort==port && Marshal.ReadInt32(row,20)==processId) return true;
                    }
                    return false;
                } finally {Marshal.FreeHGlobal(table);}
            }
            throw new InvalidOperationException("Native TCP listener table changed repeatedly; retry activation.");
        }
    }
}
'@
    }
    return [FileMapper.Release.WindowsTcpNative]::OwnsListener($ProcessId,$Port)
}
function Wait-FileMapperHealth([Diagnostics.Process]$Process, [int]$Port, [int]$Seconds=30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) { throw "FileMapper exited during startup (code $($Process.ExitCode))." }
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 2
            if ($health.product -eq 'FileMapper' -and $health.status -eq 'ok') {
                $hasProcessId=$health.PSObject.Properties.Name -contains 'processId'
                $reportedProcessMatches=(-not $hasProcessId) -or $health.processId -eq $Process.Id
                if($reportedProcessMatches -and $IsWindows) {
                    # JSON alone is not authoritative: verify actual OS listener ownership for every Windows response.
                    if(Test-FileMapperListenerOwner $Process.Id $Port){return $health}
                } elseif($reportedProcessMatches -and $hasProcessId) {
                    return $health
                }
            }
        } catch [System.Net.Http.HttpRequestException] { }
        catch [System.Threading.Tasks.TaskCanceledException] { }
        Start-Sleep -Milliseconds 200
    }
    throw "FileMapper did not become healthy within $Seconds seconds."
}
function Invoke-FileMapperInstallerCli([string]$Executable,[string[]]$Arguments,[string]$InstallerToken) {
    $start=[Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $start.ArgumentList.Add('--installer-session')
    $start.Environment['FILEMAPPER_INSTALLER_TOKEN']=$InstallerToken
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $process.WaitForExit();$null=$stdout.GetAwaiter().GetResult();$detail=$stderr.GetAwaiter().GetResult()
    try {if($process.ExitCode){throw "Installer database command failed: $detail"}} finally {$process.Dispose()}
}
