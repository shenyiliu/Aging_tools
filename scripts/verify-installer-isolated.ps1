param([string]$Compiler, [string]$BaselineDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $env:LOCALAPPDATA 'Programs\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path -Parent $sdk
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (!$Compiler) { $Compiler = Join-Path $repo 'artifacts\build-inno\ISCC.exe' }
if (!$BaselineDirectory) { $BaselineDirectory = Join-Path $repo 'artifacts\installer-baseline-0.2.3' }
$release = Join-Path $repo 'artifacts\GW_AgingTool_win-x64'
$qa = Join-Path $repo ('artifacts\verification\installer-isolated-' + $version + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa | Out-Null
$fixture = Join-Path $env:LOCALAPPDATA ('GMK-InstallerQA-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $fixture '程序 中文 空格'
$data = Join-Path $fixture '用户数据'
$exe = Join-Path $target 'Test.exe'
$uninstaller = Join-Path $target 'unins000.exe'
$appGuid = [guid]::NewGuid().ToString().ToUpperInvariant()
$appId = '{{' + $appGuid + '}'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + $appGuid + '}_is1'
$productionKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E8AC1C37-B197-4F44-91F5-3F2CE5941382}_is1'
$ownTask = ''; $otherTask = ''; $folder = $null; $service = $null; $completed = $false
$previousWorker = $env:GW_AGING_WORKER_EXE
function Production-State {
    $state = foreach ($hive in @([Microsoft.Win32.Registry]::CurrentUser, [Microsoft.Win32.Registry]::LocalMachine)) {
        $key = $hive.OpenSubKey($productionKey)
        if ($key) { try { $hive.Name + '|' + $key.GetValue('DisplayVersion') + '|' + $key.GetValue('InstallLocation') } finally { $key.Dispose() } }
    }
    return ($state -join "`n")
}
$originalProductionState = Production-State
function Run-Exit([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(120000)) { $process.Kill($true); $process.WaitForExit(); throw ('Installer QA timed out: ' + $Executable) }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Run-Checked([string]$Executable, [string[]]$Arguments) {
    $code = Run-Exit $Executable $Arguments
    if ($code -ne 0) { throw ('Exit ' + $code + ': ' + $Executable) }
}
function Build-Fixture([string]$Payload, [string]$Tag) {
    $payloadInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Payload 'Test.exe'))
    $payloadVersion = [Version]$payloadInfo.FileVersion.Trim()
    $fixtureVersion = '{0}.{1}.{2}' -f $payloadVersion.Major, $payloadVersion.Minor, $payloadVersion.Build
    $output = Join-Path $qa $Tag
    New-Item -ItemType Directory -Path $output | Out-Null
    & $Compiler ('/DQAAppId=' + $appId) ('/DAppVersion=' + $fixtureVersion) ('/DSourceDir=' + $Payload) ('/DOutputDir=' + $output) (Join-Path $repo 'installer\GWAgingTool.iss') *> (Join-Path $qa ($Tag + '-compile.log'))
    if ($LASTEXITCODE -ne 0) { throw ('QA compilation failed: ' + $Tag) }
    return Join-Path $output ('GMK_AgingTool_Setup_' + $fixtureVersion + '_win-x64.exe')
}
function Install-Args([string]$LogName) {
    return @('/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $target + '"'),('/LOG="' + (Join-Path $qa $LogName) + '"'))
}
function Data-Hashes {
    $hashes = @{}
    Get-ChildItem -LiteralPath $data -File -Recurse | ForEach-Object { $hashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    return $hashes
}
function Assert-Hashes([hashtable]$Hashes) {
    foreach ($entry in $Hashes.GetEnumerator()) {
        if (!(Test-Path -LiteralPath $entry.Key) -or (Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash -ne $entry.Value) { throw ('Unexpected file change: ' + $entry.Key) }
    }
}
function Register-FixtureTask([string]$Name, [string]$Command) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $safeCommand = [Security.SecurityElement]::Escape($Command)
    $xml = '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Principals><Principal id="Author"><UserId>' + $sid + '</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><Enabled>false</Enabled></Settings><Actions Context="Author"><Exec><Command>' + $safeCommand + '</Command></Exec></Actions></Task>'
    $task = $folder.RegisterTask($Name, $xml, 6, $sid, $null, 3, $null)
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null
}
try {
    $setup = Build-Fixture $release 'current'
    $baselineSetup = Build-Fixture $BaselineDirectory 'baseline'
    Run-Checked $baselineSetup (Install-Args 'baseline-install.log')
    $baselineVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion.Trim()
    if ([Version]$baselineVersion -ge [Version]($version + '.0')) { throw 'Baseline payload must be older than the current version' }
    $baselineSmoke = Join-Path $fixture '旧版演练验收'
    Run-Checked $exe @('--smoke',('"' + $baselineSmoke + '"'))
    $data = Join-Path $baselineSmoke 'data'
    $probe = Join-Path $qa 'baseline-probe.json'
    Run-Checked $exe @('--deployment-probe',('"' + $probe + '"'),'--data',('"' + $data + '"'))
    $baselineOptions = Get-Content -LiteralPath (Join-Path $data 'options.json') -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Force -Path (Join-Path $data 'LOG') | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'LOG\keep.txt') -Value 'must survive upgrade and uninstall' -Encoding utf8
    $userHashes = Data-Hashes
    $payloadHashes = @{}
    Get-ChildItem -LiteralPath $target -File -Recurse | ForEach-Object { $payloadHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $lockCases = @()
    foreach ($relative in @('Test.exe', 'Aging.Windows.dll', 'Tools\BurnInTest\BurnInTest\bit.exe')) {
        $lockedPath = Join-Path $target $relative
        # Exactly the reported scenario: reads remain allowed; replacement/deletion does not.
        $lock = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $logName = 'blocked-' + [IO.Path]::GetFileName($relative) + '.log'
            $exitCode = Run-Exit $setup (Install-Args $logName)
            if ($exitCode -eq 0) { throw 'Upgrade must fail before extraction when a payload file denies replacement' }
            $log = Get-Content -LiteralPath (Join-Path $qa $logName) -Raw
            if ($log -notmatch 'GMK_INSTALL_LOCK_BLOCKED:' -or $log -notmatch 'Windows error 32' -or $log -match 'Starting the installation process\.') { throw 'Lock must be caught before the installation process starts' }
            Assert-Hashes $payloadHashes
            Assert-Hashes $userHashes
            if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion.Trim() -ne $baselineVersion) { throw 'Blocked upgrade changed baseline version' }
            $lockCases += [pscustomobject]@{ File=$relative; ExitCode=$exitCode; Error=32; NoPayloadChanges=$true }
        } finally { $lock.Dispose() }
    }
    Run-Checked $setup (Install-Args 'upgrade-after-unlock.log')
    Assert-Hashes $userHashes
    $probe = Join-Path $qa 'after-upgrade.json'
    Run-Checked $exe @('--deployment-probe',('"' + $probe + '"'),'--data',('"' + $data + '"'))
    $state = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    if ($state.Version -ne $version -or !$state.RuntimeDirectory.StartsWith($target,[StringComparison]::OrdinalIgnoreCase) -or $state.Options.ReferenceRoot -ne (Join-Path $target 'Tools')) { throw 'Upgraded payload, bundled runtime, or tools path incorrect' }
    # Loading old options adds current GPU defaults and clears a removed legacy SFT path.
    # Check every prior semantic setting before rebasing the accepted migration for uninstall QA.
    $oldOptionsPath = Join-Path $data 'options.json'
    $stableUserHashes = $userHashes.Clone()
    $stableUserHashes.Remove($oldOptionsPath); $stableUserHashes.Remove($oldOptionsPath + '.bak')
    Assert-Hashes $stableUserHashes
    $oldOptions = $baselineOptions
    foreach ($property in $oldOptions.PSObject.Properties) {
        if ($property.Name -eq 'SftPath' -and $state.Options.SftPath -eq '') { continue }
        $beforeValue = ConvertTo-Json -InputObject $property.Value -Depth 20 -Compress
        $afterValue = ConvertTo-Json -InputObject $state.Options.($property.Name) -Depth 20 -Compress
        if ($beforeValue -ne $afterValue) { throw ('Option changed during migration: ' + $property.Name) }
    }
    $userHashes = Data-Hashes
    $payloadCount = 0
    foreach ($line in Get-Content -LiteralPath (Join-Path $target 'SHA256SUMS.txt')) {
        if ($line -match '^([a-f0-9]{64})  (.+)$') {
            if ((Get-FileHash -LiteralPath (Join-Path $target $Matches[2]) -Algorithm SHA256).Hash -ne $Matches[1]) { throw ('Installed hash mismatch: ' + $Matches[2]) }
            $payloadCount++
        }
    }
    $indicator = [Threading.Mutex]::new($false,'Local\GW_AgingTool_Running')
    try {
        $blockedInstall = Run-Exit $setup (Install-Args 'blocked-running.log')
        $blockedUninstall = Run-Exit $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
        if ($blockedInstall -eq 0 -or $blockedUninstall -eq 0 -or !(Test-Path -LiteralPath $exe)) { throw 'Running application protection failed' }
    } finally { $indicator.Dispose() }
    Run-Checked $exe @('--smoke',('"' + (Join-Path $qa 'smoke') + '"'))
    $smoke = Get-Content -LiteralPath (Join-Path $qa 'smoke\smoke-result.json') -Raw | ConvertFrom-Json
    if (!$smoke.Simulation -or $smoke.HardwarePassed -or $smoke.Outcome -ne 'Pass') { throw 'Installed UI smoke must pass only as simulation' }
    $env:GW_AGING_WORKER_EXE = $exe
    & $sdk test (Join-Path $repo 'tests\Aging.Windows.Tests\Aging.Windows.Tests.csproj') -c Release --no-build --filter 'FullyQualifiedName~Worker' --logger 'trx;LogFileName=installed-worker.trx' --results-directory $qa *> (Join-Path $qa 'worker.log')
    if ($LASTEXITCODE -ne 0) { throw 'Installed worker tests failed' }
    $service = New-Object -ComObject 'Schedule.Service'; $service.Connect(); $folder = $service.GetFolder('\')
    $ownTask = 'GW_AgingTool_InstallerQA_' + [guid]::NewGuid().ToString('N')
    $otherTask = 'GW_AgingTool_InstallerQA_' + [guid]::NewGuid().ToString('N')
    Register-FixtureTask $ownTask $exe
    Register-FixtureTask $otherTask (Join-Path $fixture 'Other\Test.exe')
    Run-Checked $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $qa 'uninstall.log') + '"'))
    for ($attempt = 0; $attempt -lt 30 -and (Test-Path -LiteralPath $exe); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath $exe) { throw 'Uninstall left the application executable' }
    Assert-Hashes $userHashes
    $remainingKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
    if ($remainingKey) { $remainingKey.Dispose(); throw 'QA uninstall registration remained' }
    $ownRemained = $false
    try { $task = $folder.GetTask($ownTask); $ownRemained = $true; [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null } catch { if ($_.Exception.ToString() -notmatch '0x80070002') { throw } }
    if ($ownRemained) { throw 'Installed-path resume task remained' }
    $task = $folder.GetTask($otherTask); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null
    if ((Production-State) -ne $originalProductionState) { throw 'Production installation registration changed' }
    [ordered]@{ Version=$version; IsolatedAppId=$appGuid; Fixture=$fixture; BaselineVersion=$baselineVersion; LockCases=$lockCases; UpgradeAfterUnlock=$true; ChineseAndSpacePath=$true; BundledRuntime=$true; InstalledPayloadFilesVerified=$payloadCount; InstalledUISimulationPassed=$true; InstalledWorkerTestsPassed=2; RunningUpgradeBlocked=($blockedInstall -ne 0); RunningUninstallBlocked=($blockedUninstall -ne 0); UserFilesPreserved=$userHashes.Count; OwnTaskRemoved=$true; UnrelatedTaskPreserved=$true; Uninstalled=$true; ProductionRegistrationUnchanged=$true; Scope='Current-user isolated app identity; same installer code and complete payload; QA compression and shortcuts differ from production.' } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $qa 'result.json') -Encoding utf8
    $completed = $true
    Get-Content -LiteralPath (Join-Path $qa 'result.json')
} finally {
    if ($null -eq $previousWorker) { Remove-Item Env:GW_AGING_WORKER_EXE -ErrorAction SilentlyContinue } else { $env:GW_AGING_WORKER_EXE = $previousWorker }
    if (!$completed -and (Test-Path -LiteralPath $uninstaller)) { Run-Exit $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') | Out-Null }
    if ($folder) {
        foreach ($name in @($ownTask,$otherTask) | Where-Object { $_ }) { try { $folder.DeleteTask($name,0) } catch { if ($_.Exception.ToString() -notmatch '0x80070002') { throw } } }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null
    }
    if ($service) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null }
}
