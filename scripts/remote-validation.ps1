param(
    [ValidateSet('Inventory', 'Install', 'Start', 'Execute', 'Status', 'Collect', 'Cleanup', 'VerifyRelease', 'VerifyReboot')]
    [string]$Action = 'Status',
    [string]$Root = 'C:\AgingLab\20261113',
    [ValidateSet('software', 'burnin0', 'burnin1', 'burnin2', 'burnin3', 's3', 's4', 'reboot')]
    [string]$Case = 'software'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$install = Join-Path $Root 'App'
$evidence = Join-Path $Root 'Evidence'
$taskPrefix = 'GMK-Aging-Remote-' + (Split-Path -Leaf $Root) + '-'
$taskName = $taskPrefix + $Case
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

switch ($Action) {
    'Inventory' {
        $inventory = [ordered]@{
            Time = (Get-Date).ToString('o')
            OS = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, OSArchitecture, LastBootUpTime
            Computer = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, TotalPhysicalMemory
            CPU = Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
            GPU = Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion
            Power = @(& powercfg.exe /a)
            Sessions = @(& quser.exe)
            ExistingApp = @(Get-Process Test,bit,FurMark,CoreTemp -ErrorAction SilentlyContinue | Select-Object Name, Id, SessionId, Path)
        }
        $inventory | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'inventory.json') -Encoding UTF8
        $inventory | ConvertTo-Json -Depth 8
    }
    'Install' {
        if (Test-Path -LiteralPath $install) { throw 'This validation install directory already exists. Use a new root.' }
        $setup = Join-Path $Root 'setup.exe'
        Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Format-List
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/ALLUSERS', ('/DIR="' + $install + '"'), ('/LOG="' + (Join-Path $evidence 'install.log') + '"'))
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Installer exited $($process.ExitCode)" }
        Expand-Archive -LiteralPath (Join-Path $Root 'harness.zip') -DestinationPath $install
        $exe = Join-Path $install 'Test.exe'
        # First invocation deliberately has no previously generated options.json.
        $fresh = Start-Process -FilePath $exe -ArgumentList @('--data', ('"' + (Join-Path $evidence 'fresh-data') + '"'), '--compatibility-report', ('"' + (Join-Path $evidence 'fresh-compatibility.json') + '"')) -WindowStyle Hidden -Wait -PassThru
        $probe = Start-Process -FilePath $exe -ArgumentList @('--data', ('"' + (Join-Path $evidence 'probe-data') + '"'), '--deployment-probe', ('"' + (Join-Path $evidence 'deployment-probe.json') + '"')) -WindowStyle Hidden -Wait -PassThru
        [ordered]@{ Installed = $install; Version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion; FreshCompatibilityExit = $fresh.ExitCode; DeploymentProbeExit = $probe.ExitCode } | ConvertTo-Json
    }
    'Start' {
        if (Get-Process Aging.E2E,bit -ErrorAction SilentlyContinue) { throw 'Another validation or BurnIn engine is active. Wait for it to finish.' }
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw "Task already exists: $taskName" }
        $output = Join-Path $evidence $Case
        if (Test-Path -LiteralPath $output) { throw 'This case output already exists; preserve the evidence and use a new case/root.' }
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $PSCommandPath + '" -Action Execute -Root "' + $Root + '" -Case ' + $Case
        $taskAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $install
        $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -Priority 4 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $principal -Settings $settings | Out-Null
        Start-ScheduledTask -TaskName $taskName
        [ordered]@{ Started = $taskName; Case = $Case; Output = $output } | ConvertTo-Json
    }
    'Execute' {
        $output = Join-Path $evidence $Case
        New-Item -ItemType Directory -Path $output | Out-Null
        $started = Get-Date
        try {
            (Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-General';Id=12} -MaxEvents 1).ToXml() |
                Set-Content (Join-Path $output 'kernel-start-before.xml') -Encoding UTF8
            @('Test.exe', 'Test.dll', 'Aging.Core.dll', 'Aging.Windows.dll', 'Aging.E2E.dll') | ForEach-Object {
                $binary = Join-Path $install $_
                [ordered]@{ Name=$_; SHA256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash; Version=(Get-Item -LiteralPath $binary).VersionInfo.FileVersion }
            } | ConvertTo-Json | Set-Content (Join-Path $output 'binaries.json') -Encoding UTF8
            $arguments = @('"' + $output + '"')
            if ($Case -like 'burnin*') { $arguments += @('--hardware-only', '--burnin-mode', $Case.Substring(6)) }
            if ($Case -in @('s3', 's4', 'reboot')) { $arguments += @('--power-item', @{s3='S3';s4='S4';reboot='Reboot'}[$Case]) }
            $process = Start-Process -FilePath (Join-Path $install 'Aging.E2E.exe') -ArgumentList $arguments -WorkingDirectory $install -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $output 'stdout.txt') -RedirectStandardError (Join-Path $output 'stderr.txt')
            (Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-General';Id=12} -MaxEvents 1).ToXml() |
                Set-Content (Join-Path $output 'kernel-start-after.xml') -Encoding UTF8
            [ordered]@{ Started = $started.ToString('o'); Finished = (Get-Date).ToString('o'); ExitCode = $process.ExitCode; Case = $Case } | ConvertTo-Json | Set-Content (Join-Path $output 'execution.json') -Encoding UTF8
        }
        catch {
            $_ | Out-String | Set-Content (Join-Path $output 'execution-error.txt') -Encoding UTF8
            throw
        }
    }
    'Status' {
        Get-ScheduledTask -TaskName ($taskPrefix + '*') -ErrorAction SilentlyContinue | ForEach-Object {
            $info = $_ | Get-ScheduledTaskInfo
            [ordered]@{ Task = $_.TaskName; State = $_.State.ToString(); LastTaskResult = $info.LastTaskResult } | ConvertTo-Json -Compress
        }
        Get-Process Aging.E2E,Test,bit,CoreTemp -ErrorAction SilentlyContinue | Select-Object Name, Id, CPU, SessionId, Path | ConvertTo-Json -Compress
        foreach ($name in @('software', 'burnin0', 'burnin1', 'burnin2', 'burnin3', 's3', 's4', 'reboot')) {
            foreach ($file in @('execution.json', 'execution-error.txt', 'result.json')) {
                $path = Join-Path (Join-Path $evidence $name) $file
                if (Test-Path -LiteralPath $path) { Write-Output $path; Get-Content -LiteralPath $path -Raw }
            }
        }
    }
    'VerifyReboot' {
        $output = Join-Path $evidence 'reboot'
        $prepared = Get-Content -LiteralPath (Join-Path $output 'reboot-prepared.json') -Raw | ConvertFrom-Json
        $envelope = Get-Content -LiteralPath (Join-Path $prepared.ResumeDataRoot 'active-run.json') -Raw | ConvertFrom-Json
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($envelope.Payload))).Replace('-','') }
        finally { $sha.Dispose() }
        if ($actual -ne $envelope.Sha256) { throw 'Reboot checkpoint checksum mismatch' }
        $run = $envelope.Payload | ConvertFrom-Json
        $bootEvent = Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-General';Id=12} -MaxEvents 1
        $bootEvent.ToXml() | Set-Content (Join-Path $output 'current-kernel-start.xml') -Encoding UTF8
        [xml]$bootXml = $bootEvent.ToXml()
        $startTime = ($bootXml.Event.EventData.Data | Where-Object {$_.Name -eq 'StartTime'}).'#text'
        $bootId = 'kernel-start-v1|' + $bootEvent.RecordId + '|' + ([datetimeoffset]::Parse($startTime)).UtcDateTime.Ticks
        $remaining = @(Get-ScheduledTask -TaskName ('GW_AgingTool_*_' + $prepared.RunId.Replace('-','')) -ErrorAction SilentlyContinue)
        $functional = $run.RunId -eq $prepared.RunId -and $run.Revision -eq $envelope.Revision -and
            $run.ResumeExecutable -eq (Join-Path $install 'Test.exe') -and $run.ResumeVersion -eq 1 -and
            !$run.Simulation -and $run.IsTerminal -and
            $run.Items.Count -eq 1 -and $run.Items[0].CompletedCycles -eq 1 -and
            $prepared.ExpectedBootId -like 'kernel-start-v1|*' -and $bootId -ne $prepared.ExpectedBootId -and
            $run.PendingOperation -eq '' -and $remaining.Count -eq 0
        $passed = $functional -and $run.HardwarePassed -and $run.Outcome -eq 'Pass'
        $result = [ordered]@{ Case='ProductionRebootAndLogonResume'; Passed=$passed; AutomaticResumePassed=$functional; HardwarePassed=$run.HardwarePassed; Outcome=$run.Outcome; Phase=$run.Phase; CompletedCycles=$run.Items[0].CompletedCycles; RunId=$run.RunId; PreviousBootId=$prepared.ExpectedBootId; CurrentBootId=$bootId; ResumeExecutable=$run.ResumeExecutable; ResumeTaskRemoved=($remaining.Count -eq 0); CheckpointSHA256=$actual; LogDirectory=$run.LogDirectory; Time=(Get-Date).ToString('o') }
        $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'resume-verification.json') -Encoding UTF8
        Get-WinEvent -FilterHashtable @{ LogName='System'; StartTime=[datetime]$prepared.PreparedAt; Id=1074,6005,6006,41,12,13 } -ErrorAction SilentlyContinue |
            ForEach-Object { $_.ToXml() } | ConvertTo-Json | Set-Content (Join-Path $output 'reboot-system-events.json') -Encoding UTF8
        $result | ConvertTo-Json
        if (!$passed) { throw 'Reboot or production logon-resume verification has not passed; inspect the durable checkpoint.' }
    }
    'VerifyRelease' {
        $output = Join-Path $evidence 'release-verification'
        if (Test-Path -LiteralPath $output) { throw 'Release verification output already exists; preserve it.' }
        New-Item -ItemType Directory -Path $output | Out-Null
        $hashCount = 0
        foreach ($line in Get-Content -LiteralPath (Join-Path $install 'SHA256SUMS.txt') -Encoding UTF8) {
            if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid release checksum record' }
            $expectedHash = $Matches[1]
            $relative = $Matches[2]
            $target = [IO.Path]::GetFullPath((Join-Path $install $relative))
            if (!$target.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release checksum path escapes installation' }
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expectedHash) { throw ('Release hash mismatch: ' + $relative) }
            $hashCount++
        }
        if ($hashCount -eq 0) { throw 'Empty release checksum manifest' }
        function Invoke-ReleaseProbe([string]$Executable, [string[]]$ProbeArguments) {
            $process = Start-Process -FilePath $Executable -ArgumentList $ProbeArguments -WorkingDirectory $install -WindowStyle Hidden -PassThru
            try {
                if (!$process.WaitForExit(60000)) { Stop-Process -Id $process.Id -Force; throw ('Release probe timed out: ' + $Executable) }
                if ($process.ExitCode -ne 0) { throw ('Release probe failed: ' + $Executable + ', exit ' + $process.ExitCode) }
            }
            finally { $process.Dispose() }
        }
        $aliases = @('Test.exe','Other\ControlPanel.exe','Other\ViewLOG.exe','Reboot\Reboot.exe','Sleep\Sleep.exe','BurnInTest\BurnInTest.exe','FurMark\FurMarkTest.exe','3DMark\3DMarkTest.exe')
        foreach ($alias in $aliases) {
            $report = Join-Path $output ('alias-' + [IO.Path]::GetFileNameWithoutExtension($alias) + '.txt')
            Invoke-ReleaseProbe (Join-Path $install $alias) @('--alias-probe', ('"' + $report + '"'))
            $lines = Get-Content -LiteralPath $report
            if ($lines[0] -ne [IO.Path]::GetFileNameWithoutExtension($alias).ToLowerInvariant()) { throw ('Wrong alias: ' + $alias) }
        }
        $freshData = Join-Path $output 'fresh-data'
        Invoke-ReleaseProbe (Join-Path $install 'Test.exe') @('--data', ('"' + $freshData + '"'), '--compatibility-report', ('"' + (Join-Path $output 'fresh-compatibility.json') + '"'))
        if (Test-Path -LiteralPath $freshData) { throw 'Read-only compatibility report modified fresh configuration' }
        $probePath = Join-Path $output 'deployment-probe.json'
        Invoke-ReleaseProbe (Join-Path $install 'Test.exe') @('--data', ('"' + (Join-Path $output 'probe-data') + '"'), '--deployment-probe', ('"' + $probePath + '"'))
        $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
        $version = (Get-Item -LiteralPath (Join-Path $install 'Test.exe')).VersionInfo.FileVersion.Trim()
        if ($version -ne ($probe.Version + '.0') -or $probe.Options.ReferenceRoot -ne (Join-Path $install 'Tools')) { throw 'Installed version or bundled tool root is incorrect' }
        foreach ($key in @('BurnInPath','PwrTestPath','VideoPath')) {
            $path = $probe.Options.$key
            if (!$path.StartsWith((Join-Path $install 'Tools') + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path)) { throw ('Missing installed component: ' + $key) }
        }
        [ordered]@{ Version=$probe.Version; Installed=$install; ReleaseFilesVerified=$hashCount; AliasesVerified=$aliases.Count; FreshCompatibilityReadOnly=$true; IndependentInitializationVerified=$true; Time=(Get-Date).ToString('o') } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
        Get-Content -LiteralPath (Join-Path $output 'result.json') -Raw
    }
    'Collect' {
        $archivePath = Join-Path $Root 'evidence.zip'
        if (Test-Path -LiteralPath $archivePath) { throw 'Evidence archive already exists; preserve it before collecting again.' }
        Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $evidence 'remote-validation.ps1') -Force
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem -LiteralPath $evidence -File -Recurse | Where-Object { $_.FullName -notmatch '\\engine\\|\\file-migration-' } | ForEach-Object {
                $relative = $_.FullName.Substring($evidence.Length + 1).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally { $archive.Dispose() }
        Get-Item -LiteralPath $archivePath | Select-Object FullName, Length
        Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Format-List
    }
    'Cleanup' {
        Get-ScheduledTask -TaskName ($taskPrefix + '*') -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.State -eq 'Running') { throw "Validation still running: $($_.TaskName)" }
            $_ | Unregister-ScheduledTask -Confirm:$false
        }
    }
}
