param([switch]$RunSmoke)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts\GW_AgingTool_win-x64'
$qa = Join-Path $repo 'artifacts\verification'
New-Item -ItemType Directory -Force -Path $qa | Out-Null
$aliases = @('Test.exe','Other\ControlPanel.exe','Other\ViewLOG.exe','Reboot\Reboot.exe','Sleep\Sleep.exe','BurnInTest\BurnInTest.exe','FurMark\FurMarkTest.exe','3DMark\3DMarkTest.exe')
$results = @()
function Run-Probe([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(120000)) { $process.Kill($true); $process.WaitForExit(); throw ('Verification timed out: ' + $Executable) }
        if ($process.ExitCode -ne 0) { throw ('Verification process failed: ' + $Executable + ', exit ' + $process.ExitCode) }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
foreach ($alias in $aliases) {
    $output = Join-Path $qa ('alias-' + [IO.Path]::GetFileNameWithoutExtension($alias) + '.txt')
    $probeExit = Run-Probe (Join-Path $release $alias) @('--alias-probe', ('"' + $output + '"'))
    if (!(Test-Path -LiteralPath $output)) { throw ('Alias failed: ' + $alias) }
    $lines = Get-Content -LiteralPath $output
    if ($lines[0] -ne [IO.Path]::GetFileNameWithoutExtension($alias).ToLowerInvariant()) { throw ('Wrong module: ' + $alias) }
    $results += [pscustomobject]@{ Module = $alias; ExitCode = $probeExit; SharedDirectory = $lines[1] }
}
$hashCount = 0
foreach ($line in Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt')) {
    if ($line -match '^([a-f0-9]{64})  (.+)$') {
        $actual = (Get-FileHash -LiteralPath (Join-Path $release $Matches[2]) -Algorithm SHA256).Hash
        if ($actual -ne $Matches[1]) { throw ('Release hash mismatch: ' + $Matches[2]) }
        $hashCount++
    }
}
$workspace = Split-Path -Parent $repo
$manifestPath = Join-Path $workspace 'GW_TEST_逆向研究\evidence\recovery-manifest.json'
$originals = @()
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $actual = (Get-FileHash -LiteralPath (Join-Path (Join-Path $workspace 'GW_TEST') $file.input) -Algorithm SHA256).Hash
        if ($actual -ne $file.input_sha256) { throw ('Reference binary changed: ' + $file.input) }
        $originals += [pscustomobject]@{ File = $file.input; Unchanged = $true }
    }
}
if ($RunSmoke) {
    $smoke = Join-Path $qa 'smoke-final'
    Run-Probe (Join-Path $release 'Test.exe') @('--smoke', ('"' + $smoke + '"')) | Out-Null
    $smokeResult = Get-Content -LiteralPath (Join-Path $smoke 'smoke-result.json') -Raw | ConvertFrom-Json
    if (!$smokeResult.Simulation -or $smokeResult.HardwarePassed -or $smokeResult.Outcome -ne 'Pass') { throw 'Published smoke result must be a completed simulation only' }
}
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$verificationDate = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'China Standard Time').ToString('yyyy-MM-dd')
$summary = [ordered]@{ Date = $verificationDate; Version = $version; ReleaseFilesVerified = $hashCount; Aliases = $results; ReferenceBinaries = $originals }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $qa 'release-verification.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
