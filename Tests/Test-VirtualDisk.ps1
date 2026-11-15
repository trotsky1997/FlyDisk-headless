# SPDX-License-Identifier: GPL-3.0-or-later
# Real NTFS -> iSCSI -> FlyDisk -> disposable VHDX integration. CI ONLY.
param([Parameter(Mandatory)][string]$Package,[switch]$AllowLocalDisposableVhd)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowLocalDisposableVhd){throw 'Explicit -AllowLocalDisposableVhd is required outside disposable CI.'}
$tempParent=if($env:GITHUB_ACTIONS -eq 'true'){$env:RUNNER_TEMP}else{$env:TEMP}
$root=Join-Path $tempParent ('flydisk-vhd-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$vhd=Join-Path $root 'source.vhdx'
$install=Join-Path $env:ProgramFiles 'FlyDiskHeadless'
$exe=Join-Path $install 'FlyDisk.Headless.exe'
if(Get-Service FlyDiskHeadless -ErrorAction SilentlyContinue){throw 'Existing service: test refuses to replace it.'}
if(Get-SmbShare -Name 'FlyDiskVhdTest' -ErrorAction SilentlyContinue){throw 'Existing share: test refuses to replace it.'}
$statePath=Join-Path $env:ProgramData 'FlyDiskHeadless\status.json'
$share='FlyDiskVhdTest'
$report=[System.Collections.Generic.List[object]]::new()
$diagnostics=Join-Path (Get-Location) 'artifacts\vhd-diagnostics'
New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
function Assert([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Pass([string]$name){$report.Add(@{test=$name;passed=$true;utc=[DateTime]::UtcNow});Write-Output "PASS $name"}
function Invoke-FlyDisk([string[]]$arguments){$result=& $script:exe @arguments; $exit=$LASTEXITCODE; $result | Write-Host;if($exit -ne 0){throw "CLI failed ($exit): $arguments"}}
function SourceDisk {Get-DiskImage -ImagePath $script:vhd | Get-Disk}
function Wait-ServiceState([string]$state){
 $until=(Get-Date).AddSeconds(30)
 do{$s=Get-Service FlyDiskHeadless;try{if([string]$s.Status -eq $state){return}}finally{$s.Dispose()};Start-Sleep -Milliseconds 250}while((Get-Date)-lt $until)
 throw "Service did not reach $state"
}
function Check-Data([string]$expected){
 Assert (Test-Path $script:file) 'Original drive letter/file did not return'
 Assert ((Get-FileHash -LiteralPath $script:file -Algorithm SHA256).Hash -eq $expected) 'File content changed'
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
public static class DirectFileRead {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr sa,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadFile(SafeFileHandle file,IntPtr buffer,int count,out int read,IntPtr overlap);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint type,uint protection);
 [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr address,UIntPtr size,uint type);
 public static string Hash(string path){
  using(var file=CreateFileW(path,0x80000000,7,IntPtr.Zero,3,0x20000000|0x08000000,IntPtr.Zero)){
   if(file.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   IntPtr buffer=VirtualAlloc(IntPtr.Zero,(UIntPtr)65536,0x3000,4);
   if(buffer==IntPtr.Zero)throw new OutOfMemoryException();
   try{using(var hash=SHA256.Create()){
    byte[] managed=new byte[65536];
    while(true){int read;if(!ReadFile(file,buffer,managed.Length,out read,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());if(read==0)break;
     Marshal.Copy(buffer,managed,0,read);hash.TransformBlock(managed,0,read,null,0);}
    hash.TransformFinalBlock(Array.Empty<byte>(),0,0);return BitConverter.ToString(hash.Hash).Replace("-","");
   }}finally{VirtualFree(buffer,UIntPtr.Zero,0x8000);}
  }
 }
}
'@
try{
 # DiskPart only creates/attaches this unique file. Formatting is additionally bound to its disk-image association.
 @("create vdisk file=`"$vhd`" maximum=1024 type=expandable","select vdisk file=`"$vhd`"","attach vdisk") | Set-Content (Join-Path $root 'create.txt')
 & diskpart.exe /s (Join-Path $root 'create.txt') | Write-Host
 $disk=SourceDisk
 Assert ($disk.Size -eq 1GB -and $disk.PartitionStyle -eq 'RAW' -and -not $disk.IsBoot -and -not $disk.IsSystem) 'Unexpected newly-created VHD identity/layout'
 $sourceNumber=$disk.Number
 $letter=@('R','S','T','U','V','W','X','Y','Z') | Where-Object {-not(Get-Volume -DriveLetter $_ -ErrorAction SilentlyContinue)} | Select-Object -First 1
 Assert ([bool]$letter) 'No free test drive letter'
 Initialize-Disk -Number $sourceNumber -PartitionStyle GPT | Out-Null
 New-Partition -DiskNumber $sourceNumber -UseMaximumSize -DriveLetter $letter | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'FlyDiskDisposable' -Confirm:$false | Out-Null
 $file=$letter+':\probe.bin'
 $bytes=New-Object byte[] (8MB);[Random]::new(2345).NextBytes($bytes)
 $stream=[IO.FileStream]::new($file,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::WriteThrough)
 try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
 $expected=(Get-FileHash $file).Hash
 $inventory=& $exe list | ConvertFrom-Json
 $selected=@($inventory | Where-Object {$_.Disk.DiskNumber -eq $sourceNumber})
 Assert ($selected.Count -eq 1 -and $selected[0].Identity -match '\|.+$') 'VHD lacks a stable identity; refusing to bypass guard'
 $originalIdentity=$selected[0].Identity
 Dismount-DiskImage -ImagePath $vhd | Out-Null
 Mount-DiskImage -ImagePath $vhd | Out-Null
 $reattached=SourceDisk
 $secondInventory=& $exe list | ConvertFrom-Json
 Assert ((@($secondInventory | Where-Object {$_.Disk.DiskNumber -eq $reattached.Number}))[0].Identity -eq $originalIdentity) 'Virtual disk identity changed after reattachment'
 Pass 'VPD83 identity remains stable across VHD detachment/attachment'
 $selected | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $diagnostics 'source.json')
 $config=Join-Path $root 'config.json'
 @{SchemaVersion=1;DiskIdentity=$selected[0].Identity;ExpectedSizeBytes=$selected[0].Disk.SizeBytes;CacheVolume='C:';CacheSizeBytes=64MB;EnableMemoryCache=$false;RiskAccepted=$true;Language='en-US'} | ConvertTo-Json | Set-Content $config -Encoding utf8
 $cacheBefore=@(Get-ChildItem 'C:\FlyDisk.Cache' -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
 Invoke-FlyDisk -arguments @('install','--config',$config)
 Invoke-FlyDisk -arguments @('preflight')
 Assert (-not(SourceDisk).IsOffline) 'Preflight took the source offline'
 Pass 'VHD creation and non-mutating preflight'
 Invoke-FlyDisk -arguments @('start')
 # Exclusive raw access intentionally hides the source from storage-provider association queries.
 Start-Sleep -Seconds 6
 $running=Get-Content $statePath -Raw | ConvertFrom-Json
 Assert ($running.Phase -eq 'running' -and $running.Stats.Cache.DeviceWasOffline) 'Backend did not confirm offline source ownership'
 $clone=Get-Partition -DriveLetter $letter | Get-Disk
 Assert ($clone.BusType -eq 'iSCSI' -and -not $clone.IsOffline) 'Drive letter is not backed by the live iSCSI clone'
 Check-Data $expected
 Pass 'Real iSCSI takeover restores NTFS drive letter and original file'
 Assert ([DirectFileRead]::Hash($file) -eq $expected) 'Cold direct read hash mismatch'
 Start-Sleep -Seconds 6
 $before=Get-Content $statePath -Raw | ConvertFrom-Json
 Assert ([DirectFileRead]::Hash($file) -eq $expected) 'Warm direct read hash mismatch'
 Start-Sleep -Seconds 6
 $after=Get-Content $statePath -Raw | ConvertFrom-Json
 $before | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $diagnostics 'before-warm.json')
 $after | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $diagnostics 'after-warm.json')
 Assert ($after.Stats.Cache.SsdHitBlocksTotal -gt $before.Stats.Cache.SsdHitBlocksTotal) 'Warm unbuffered read did not increment L2 hits'
 Pass 'Unbuffered cold/warm reads prove actual L2 cache hits'
 New-SmbShare -Name $share -Path ($letter+':\') -FullAccess ([Security.Principal.WindowsIdentity]::GetCurrent().Name) | Out-Null
 $unc='\\localhost\'+$share+'\shared.bin'
 [IO.File]::WriteAllBytes($unc,$bytes)
 Assert ((Get-FileHash -LiteralPath $unc).Hash -eq $expected) 'SMB write/read mismatch'
 Remove-Item -LiteralPath $unc
 Remove-SmbShare -Name $share -Force
 Pass 'SMB file write/read/delete through accelerated NTFS'
 Invoke-FlyDisk -arguments @('stop')
 Assert (-not(SourceDisk).IsOffline) 'Graceful stop left source offline'
 Check-Data $expected
 Pass 'Graceful stop returns original disk and intact file'
 Invoke-FlyDisk -arguments @('start')
 Check-Data $expected
 Invoke-FlyDisk -arguments @('stop')
 Check-Data $expected
 Pass 'Existing cache verifies and restarts successfully'
 # Exercise real unbuffered IO with bounded L1 and adaptive read-ahead enabled.
 $installedConfig=Join-Path $env:ProgramData 'FlyDiskHeadless\config.json'
 $adaptiveConfig=Get-Content $installedConfig -Raw | ConvertFrom-Json
 $adaptiveConfig.EnableMemoryCache=$true
 $adaptiveConfig.MemoryCacheMaxBytes=2GB
 $adaptiveConfig.EnableAdaptivePrefetch=$true
 $adaptiveConfig | ConvertTo-Json | Set-Content $installedConfig -Encoding utf8
 $aheadFile=$letter+':\read-ahead.bin'
 $aheadBytes=New-Object byte[] (16MB);[Random]::new(991).NextBytes($aheadBytes)
 $aheadStream=[IO.FileStream]::new($aheadFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::WriteThrough)
 try{$aheadStream.Write($aheadBytes,0,$aheadBytes.Length);$aheadStream.Flush($true)}finally{$aheadStream.Dispose()}
 $aheadHash=(Get-FileHash $aheadFile).Hash
 Invoke-FlyDisk -arguments @('start')
 Assert ([DirectFileRead]::Hash($aheadFile) -eq $aheadHash) 'Read-ahead returned incorrect file data'
 Assert ([DirectFileRead]::Hash($aheadFile) -eq $aheadHash) 'Warm L1 returned incorrect file data'
 Start-Sleep -Seconds 6
 $adaptive=Get-Content $statePath -Raw | ConvertFrom-Json
 $adaptive | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $diagnostics 'adaptive-lru.json')
 Assert ($adaptive.Stats.Cache.L1HitBlocksTotal -gt 0) 'L1 did not serve reads'
 Assert ($adaptive.Stats.Cache.PrefetchUsefulBlocks -gt 0) 'Adaptive prefetch did not serve future demand'
 Assert ($adaptive.Stats.Cache.TotalCacheSize -le 2GB -and $adaptive.Stats.Cache.MemoryCacheMaxBytes -eq 2GB) 'L1 byte budget violated'
 Assert ($adaptive.Stats.Cache.SsdEvictionPolicy -eq 'LRU') 'Wrong SSD eviction policy'
 Remove-Item -LiteralPath $aheadFile
 Invoke-FlyDisk -arguments @('stop')
 Check-Data $expected
 Pass 'Real 2GiB bounded L1 and adaptive read-ahead serve intact NTFS data with LRU'
 # Change the disk outside FlyDisk. Reusing stale L2 must fail and return the physical disk.
 $bytes[0]=$bytes[0] -bxor 255
 [IO.File]::WriteAllBytes($file,$bytes)
 $changed=(Get-FileHash $file).Hash
 $installedConfig=Join-Path $env:ProgramData 'FlyDiskHeadless\config.json'
 $strictConfig=Get-Content $installedConfig -Raw | ConvertFrom-Json
 $strictConfig.RefreshChangedBlocks=$false
 $strictConfig | ConvertTo-Json | Set-Content $installedConfig -Encoding utf8
 & $exe start | Write-Host
 Assert ($LASTEXITCODE -ne 0) 'Stale cache unexpectedly accepted'
 Wait-ServiceState 'Stopped'
 Assert (-not(SourceDisk).IsOffline) 'Verification failure stranded the disk'
 Check-Data $changed
 Pass 'Offline file modification rejects stale cache and restores source'
 $strictConfig.RefreshChangedBlocks=$true
 $strictConfig | ConvertTo-Json | Set-Content $installedConfig -Encoding utf8
 Invoke-FlyDisk -arguments @('start')
 Check-Data $changed
 Start-Sleep -Seconds 6
 $refreshed=Get-Content $statePath -Raw | ConvertFrom-Json
 Assert ($refreshed.Stats.CacheRefresh.RepairedBlocks -gt 0) 'Changed cache blocks were not refreshed'
 Assert ([DirectFileRead]::Hash($file) -eq $changed) 'Refreshed cache returned stale file contents'
 Invoke-FlyDisk -arguments @('stop')
 Check-Data $changed
 Pass 'Full verification refreshes changed cache blocks without modifying source data'
 # Clear ONLY the cache directory created by this isolated test, never an existing cache.
 $created=@(Get-ChildItem 'C:\FlyDisk.Cache' -Directory | Where-Object {$_.FullName -notin $cacheBefore})
 Assert ($created.Count -eq 1) 'Cannot identify disposable cache directory'
 $cachePath=(Resolve-Path -LiteralPath $created[0].FullName).ProviderPath
 Assert ($cachePath.StartsWith('C:\FlyDisk.Cache\') -and $created[0].Name -match '^[A-Fa-f0-9]+$') 'Unexpected cache cleanup path'
 Assert (-not($created[0].Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Cache is a reparse point'
 Remove-Item -LiteralPath $cachePath -Recurse -Force
 Invoke-FlyDisk -arguments @('start')
 Check-Data $changed
 $service=Get-CimInstance Win32_Service -Filter "Name='FlyDiskHeadless'"
 $process=Get-Process -Id $service.ProcessId
 Assert ($process.Path -eq $exe -and $process.Id -gt 0) 'Wrong process for disposable crash injection'
 Stop-Process -Id $process.Id -Force
 Wait-ServiceState 'Stopped'
 & $exe start | Write-Host
 Assert ($LASTEXITCODE -ne 0) 'Crash checkpoint was ignored'
 Invoke-FlyDisk -arguments @('recover')
 Assert (-not(SourceDisk).IsOffline) 'Crash recovery left source offline'
 Check-Data $changed
 Pass 'Forced process crash preserves recovery checkpoint; recover restores intact file'
 Invoke-FlyDisk -arguments @('uninstall')
 $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $diagnostics 'results.json')
}
finally{
 $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $diagnostics 'results.json')
 foreach($path in @($statePath,(Join-Path $env:ProgramData 'FlyDisk\debug-security.log'))){if(Test-Path $path){Copy-Item -LiteralPath $path -Destination $diagnostics -Force}}
 Get-Disk | Select-Object Number,FriendlyName,SerialNumber,BusType,Size,IsOffline | ConvertTo-Json | Set-Content (Join-Path $diagnostics 'disks.json')
 Get-IscsiSession -ErrorAction Continue | Select-Object TargetNodeAddress,IsConnected | ConvertTo-Json | Set-Content (Join-Path $diagnostics 'sessions.json')
 # If a test failed, do not force detach an active disk. The entire runner is destroyed by CI.
}
