param([switch]$SkipSmoke, [switch]$AllUsers, [string]$BaselineSetup)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$setup = Join-Path $repo ('artifacts\installer\GW_AgingTool_Setup_' + $version + '_win-x64.exe')
$scope = if ($AllUsers) { 'all-users' } else { 'current-user' }
$qa = Join-Path $repo ('artifacts\verification\installer-' + $version + '\' + $scope)
New-Item -ItemType Directory -Force -Path $qa | Out-Null
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E8AC1C37-B197-4F44-91F5-3F2CE5941382}_is1'
foreach ($hive in @([Microsoft.Win32.Registry]::CurrentUser, [Microsoft.Win32.Registry]::LocalMachine)) {
    $existing = $hive.OpenSubKey($uninstallKey)
    if ($existing) { $existing.Dispose(); throw 'An actual installation already exists. Do not replace it with an installer test.' }
}
if ($AllUsers -and !([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'All-user QA requires an elevated shell.' }
$fixture = Join-Path $env:LOCALAPPDATA ('GW-AgingTool-InstallerQA-' + [guid]::NewGuid().ToString('N'))
$target = if ($AllUsers) { Join-Path $env:ProgramFiles ('GW-AgingTool-InstallerQA-' + [guid]::NewGuid().ToString('N') + '\程序 中文 空格') } else { Join-Path $fixture '程序 中文 空格' }
$data = Join-Path $fixture '用户数据'
$exe = Join-Path $target 'Test.exe'
$uninstaller = Join-Path $target 'unins000.exe'
$service = $null; $folder = $null; $taskNames = @()
function Run-Exit([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
function Run-Checked([string]$Executable, [string[]]$Arguments) {
    $code = Run-Exit $Executable $Arguments
    if ($code -ne 0) { throw ('Exit ' + $code + ': ' + $Executable) }
}
function Assert-UserFiles {
    foreach ($entry in $userHashes.GetEnumerator()) {
        if (!(Test-Path -LiteralPath $entry.Key) -or (Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash -ne $entry.Value) { throw ('User file changed or missing: ' + $entry.Key) }
    }
}
function Register-FixtureTask([string]$Name, [string]$Command) {
    $safeCommand = [Security.SecurityElement]::Escape($Command)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $xml = '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Principals><Principal id="Author"><UserId>' + $sid + '</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><Enabled>false</Enabled></Settings><Actions Context="Author"><Exec><Command>' + $safeCommand + '</Command></Exec></Actions></Task>'
    $task = $folder.RegisterTask($Name, $xml, 6, $sid, $null, 3, $null)
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null
}
$mode = if ($AllUsers) { '/ALLUSERS' } else { '/CURRENTUSER' }
$installArgs = @($mode,'/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $target + '"'),('/LOG="' + (Join-Path $qa 'install.log') + '"'))
$completed = $false
try {
    if ($BaselineSetup) {
        Run-Checked $BaselineSetup $installArgs
        $baselineVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
        if (!$baselineVersion.StartsWith('0.1.0')) { throw 'Upgrade baseline is not authentic 0.1.0 payload' }
        # The old application's --smoke always uses simulation and its own isolated data.
        $baselineSmoke = Join-Path $fixture 'baseline-smoke'
        Run-Checked $exe @('--smoke',('"' + $baselineSmoke + '"'))
        $data = Join-Path $baselineSmoke 'data'
    } else {
        Run-Checked $setup $installArgs
        $probe = Join-Path $qa 'first-run.json'
        Run-Checked $exe @('--deployment-probe',('"'+$probe+'"'),'--data',('"'+$data+'"'))
        $state = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
        $ownedTools = Join-Path $target 'Tools'
        if ($state.Options.ReferenceRoot -ne $ownedTools -or !$state.Options.PwrTestPath.StartsWith($ownedTools + '\', [StringComparison]::OrdinalIgnoreCase) -or $state.Options.ResumeAtLogon) { throw 'Fresh installation must use its own bundled tools without auto-resume settings' }
        if (!(Test-Path -LiteralPath $state.Options.BurnInPath) -or !(Test-Path -LiteralPath $state.Options.VideoPath)) { throw 'Bundled BurnIn or video paths were not discovered' }
        if (!$state.RuntimeDirectory.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) { throw 'Application is not using its bundled runtime' }
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $data 'LOG') | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'LOG\keep-after-upgrade.txt') -Value 'user log must survive upgrade and uninstall' -Encoding utf8
    # Hash actual options, queue, checkpoint and logs, including completed old-run data.
    $userHashes = @{}
    Get-ChildItem -LiteralPath $data -Recurse -File | ForEach-Object { $userHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $installArgs[-1] = '/LOG="' + (Join-Path $qa 'upgrade.log') + '"'
    Run-Checked $setup $installArgs
    Assert-UserFiles
    if (!(Test-Path -LiteralPath (Join-Path $target 'installed.json'))) { throw 'Deployment marker is missing' }
    $probe = Join-Path $qa 'after-upgrade.json'
    Run-Checked $exe @('--deployment-probe',('"'+$probe+'"'),'--data',('"'+$data+'"'))
    $state = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    if ($state.Version -ne $version -or !$state.RuntimeDirectory.StartsWith($target,[StringComparison]::OrdinalIgnoreCase)) { throw 'Incorrect installed version/runtime' }
    Assert-UserFiles
    $manifestCount = 0
    foreach ($line in Get-Content -LiteralPath (Join-Path $target 'SHA256SUMS.txt')) {
        if ($line -match '^([a-f0-9]{64})  (.+)$') {
            if ((Get-FileHash -LiteralPath (Join-Path $target $Matches[2]) -Algorithm SHA256).Hash -ne $Matches[1]) { throw ('Installed payload hash mismatch: ' + $Matches[2]) }
            $manifestCount++
        }
    }
    $hive = if ($AllUsers) { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
    $key = $hive.OpenSubKey($uninstallKey)
    if (!$key) { throw 'Missing installed-apps registration in requested scope' }
    try { if ($key.GetValue('DisplayVersion') -ne $version) { throw 'Uninstall version registration was not upgraded' } } finally { $key.Dispose() }
    $indicator = [Threading.Mutex]::new($false, 'Local\GW_AgingTool_Running')
    try {
        $installArgs[-1] = '/LOG="' + (Join-Path $qa 'blocked-upgrade.log') + '"'
        $blockedInstall = Run-Exit $setup $installArgs
        $blockedUninstall = Run-Exit $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $qa 'blocked-uninstall.log')+'"'))
        if ($blockedInstall -eq 0 -or $blockedUninstall -eq 0 -or !(Test-Path -LiteralPath $exe)) { throw 'Running-application protection failed' }
    } finally { $indicator.Dispose() }
    Run-Checked $exe @('--compatibility-report',('"'+(Join-Path $qa 'compatibility.json')+'"'),'--data',('"'+$data+'"'))
    if (!$SkipSmoke) { Run-Checked $exe @('--smoke',('"'+(Join-Path $qa 'smoke')+'"')) }
    $sdkCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $sdk = if ($sdkCommand) { $sdkCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\dotnet\dotnet.exe' }
    $previousWorker = $env:GW_AGING_WORKER_EXE; $previousDotnetRoot = $env:DOTNET_ROOT
    try {
        $env:GW_AGING_WORKER_EXE = $exe; $env:DOTNET_ROOT = Split-Path -Parent $sdk
        & $sdk test (Join-Path $repo 'tests\Aging.Windows.Tests\Aging.Windows.Tests.csproj') -c Release --no-build --filter 'FullyQualifiedName~Worker' --logger 'trx;LogFileName=installed-worker-tests.trx' --results-directory $qa
        if ($LASTEXITCODE -ne 0) { throw 'Installed worker tests failed' }
    } finally { $env:GW_AGING_WORKER_EXE = $previousWorker; $env:DOTNET_ROOT = $previousDotnetRoot }
    # Disabled, triggerless tasks only: never execute either task.
    $service = New-Object -ComObject 'Schedule.Service'; $service.Connect(); $folder = $service.GetFolder('\')
    $ownTask = 'GW_AgingTool_InstallerTest_' + [guid]::NewGuid().ToString('N')
    $otherTask = 'GW_AgingTool_InstallerTest_' + [guid]::NewGuid().ToString('N')
    $taskNames = @($ownTask, $otherTask)
    Register-FixtureTask $ownTask $exe
    Register-FixtureTask $otherTask (Join-Path $fixture 'OtherVersion\Test.exe')
    Run-Checked $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $qa 'uninstall.log')+'"'))
    for ($attempt = 0; $attempt -lt 30 -and (Test-Path -LiteralPath $exe); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath $exe) { throw 'Application executable remained after uninstall' }
    Assert-UserFiles
    $key = $hive.OpenSubKey($uninstallKey)
    if ($key) { $key.Dispose(); throw 'Uninstall registration remained' }
    $ownStillExists = $false
    try { $task = $folder.GetTask($ownTask); $ownStillExists = $true; [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null } catch { if ($_.Exception.ToString() -notmatch '0x80070002') { throw } }
    if ($ownStillExists) { throw 'Installed-path resume task remained' }
    $task = $folder.GetTask($otherTask); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null
    [ordered]@{ Version=$version; SetupSHA256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant(); Scope=$scope; Fixture=$fixture; InstallDirectory=$target; FreshInstall=(!$BaselineSetup); BaselineVersion=$baselineVersion; CrossVersionUpgrade=[bool]$BaselineSetup; ChineseAndSpacePath=$true; BundledRuntime=$true; PayloadFilesVerified=$manifestCount; RunningUpgradeExit=$blockedInstall; RunningUninstallExit=$blockedUninstall; UserFilesPreserved=$userHashes.Count; InstalledWorkerTests=2; OwnTaskRemoved=$true; UnrelatedTaskPreserved=$true; Uninstalled=$true; SimulationRun=(!$SkipSmoke); TestScope='This Windows 11 x64 host only. Windows 10 and each hardware/driver model still require acceptance.' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $qa 'result.json') -Encoding utf8
    $completed = $true
    Get-Content -LiteralPath (Join-Path $qa 'result.json')
} finally {
    if (!$completed -and (Test-Path -LiteralPath $uninstaller)) { Run-Exit $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') | Out-Null }
    if ($folder) {
        foreach ($name in $taskNames) { try { $folder.DeleteTask($name, 0) } catch { if ($_.Exception.ToString() -notmatch '0x80070002') { throw } } }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null
    }
    if ($service) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null }
}
