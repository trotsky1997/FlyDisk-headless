# SPDX-License-Identifier: GPL-3.0-or-later
# Run ONLY in the disposable GitHub Actions Windows runner. Never touches disk attributes.
param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Fixture)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'This SCM integration test is restricted to disposable CI runners.'}
$name='FlyDiskHeadless'
$install=Join-Path $env:ProgramFiles $name
$data=Join-Path $env:ProgramData $name
$fixtureData=Join-Path $env:ProgramData 'FlyDiskHeadlessServiceTest'
if((Test-Path $install) -or (Test-Path $data) -or (Get-Service $name -ErrorAction SilentlyContinue)){throw 'Test isolation failed: existing installation.'}
function Wait-State([string]$state){
 $until=(Get-Date).AddSeconds(30)
 do {$controller=Get-Service $name;try{if([string]$controller.Status -eq $state){return}}finally{$controller.Dispose()};Start-Sleep -Milliseconds 250} while((Get-Date)-lt $until)
 throw "Service did not reach $state"
}
function Invoke-Cli([string[]]$arguments){& $script:exe @arguments;if($LASTEXITCODE -ne 0){throw "CLI failed: $arguments"}}
New-Item -ItemType Directory -Path $install | Out-Null
Copy-Item -Path (Join-Path $Package '*') -Destination $install -Recurse
$exe=Join-Path $install 'FlyDisk.Headless.exe'
$config=Join-Path $env:RUNNER_TEMP 'flydisk-test-config.json'
@{SchemaVersion=1;DiskIdentity='NO-SUCH-CI-DISK|NEVER-MATCH';ExpectedSizeBytes=1073741824;CacheVolume='C:';CacheSizeBytes=67108864;RiskAccepted=$true} | ConvertTo-Json | Set-Content $config -Encoding utf8
try{
 Invoke-Cli -arguments @('install','--config',$config)
 Wait-State 'Stopped'
 $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
 if($svc.StartMode -ne 'Manual' -or $svc.StartName -ne 'LocalSystem' -or $svc.PathName -notlike '*" service'){throw 'Incorrect service registration'}
 Invoke-Cli -arguments @('status')
 # Failed startup must be observable and must not mutate any physical disk.
 $before=@(Get-Disk | Select-Object Number,IsOffline | ConvertTo-Json -Compress)
 & $exe start
 if($LASTEXITCODE -eq 0){throw 'Nonexistent disk unexpectedly started'}
 Wait-State 'Stopped'
 $after=@(Get-Disk | Select-Object Number,IsOffline | ConvertTo-Json -Compress)
 if(($before -join '') -ne ($after -join '')){throw 'Failed preflight modified disk state'}
 $state=Get-Content (Join-Path $data 'status.json') -Raw | ConvertFrom-Json
 if($state.RecoveryRequired -or $state.Phase -ne 'failed'){throw 'Incorrect preflight failure state'}
 Invoke-Cli -arguments @('uninstall')
 Start-Sleep -Seconds 2
 # Real SCM exercises the exact production ServiceBase wrapper with a non-disk backend.
 $fixturePath=(Resolve-Path -LiteralPath $Fixture).ProviderPath
 & sc.exe create $name binPath= "`"$fixturePath`" --service-fixture" start= demand
 if($LASTEXITCODE -ne 0){throw 'Fixture registration failed'}
 Start-Service $name
 Wait-State 'Running'
 if(-not(Test-Path (Join-Path $fixtureData 'started'))){throw 'OnStart not executed'}
 Set-Content (Join-Path $fixtureData 'refuse-stop') 'busy'
 & sc.exe stop $name
 Start-Sleep -Seconds 3
 Wait-State 'StopPending'
 if(Test-Path (Join-Path $fixtureData 'stopped')){throw 'Refused stop reported success'}
 Remove-Item -LiteralPath (Join-Path $fixtureData 'refuse-stop')
 Wait-State 'Stopped'
 if(-not(Test-Path (Join-Path $fixtureData 'stopped'))){throw 'OnStop not executed'}
 & sc.exe delete $name
 if($LASTEXITCODE -ne 0){throw 'Fixture delete failed'}
 Write-Output 'PASS actual SCM install/start/refused-stop/stop/uninstall; no physical disk mutations'
}
finally{
 # No forced process kill and no recursive filesystem deletion; the runner itself is disposable.
 $svc=Get-Service $name -ErrorAction SilentlyContinue
 if($svc){Remove-Item -LiteralPath (Join-Path $fixtureData 'refuse-stop') -ErrorAction SilentlyContinue;Stop-Service $name -ErrorAction Continue;& sc.exe delete $name}
}
