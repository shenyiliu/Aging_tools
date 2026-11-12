param([switch]$RunSmoke)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts\GW_AgingTool_win-x64'
& (Join-Path $PSScriptRoot 'verify-tools.ps1') -ToolsDirectory (Join-Path $release 'Tools')
if (!$?) { throw 'Published component verification failed' }
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
$evidenceCount = 0
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $release 'Evidence.zip'))
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('SHA256SUMS.txt').Open())
    try { $evidenceManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $evidenceNames = @{}
    foreach ($line in ($evidenceManifest -split '\r?\n' | Where-Object { $_.Length -gt 0 })) {
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid evidence checksum record' }
        $expectedHash = $Matches[1]; $entryName = $Matches[2]
        if ($evidenceNames.ContainsKey($entryName)) { throw 'Duplicate evidence checksum record' }
        $entry = $archive.GetEntry($entryName)
        if (!$entry) { throw ('Missing evidence: ' + $entryName) }
        $stream = $entry.Open()
        try { $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($actualHash -ne $expectedHash) { throw ('Evidence hash mismatch: ' + $entryName) }
        $evidenceNames[$entryName] = $true
        $evidenceCount++
    }
    if ($evidenceCount -eq 0 -or $archive.Entries.Count -ne $evidenceCount + 1) { throw 'Evidence archive manifest is incomplete' }
} finally { $archive.Dispose() }
$workspace = Split-Path -Parent $repo
$manifestPath = Join-Path $repo 'artifacts\reverse\20261112\recovery-manifest.json'
$originals = @()
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $actual = (Get-FileHash -LiteralPath (Join-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) 'GW_TEST') $file.input) -Algorithm SHA256).Hash
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
$deploymentProbe = Join-Path $qa 'independent-deployment.json'
Run-Probe (Join-Path $release 'Test.exe') @('--deployment-probe', ('"' + $deploymentProbe + '"'), '--data', ('"' + (Join-Path $qa ('independent-data-' + [guid]::NewGuid().ToString('N'))) + '"')) | Out-Null
$independent = Get-Content -LiteralPath $deploymentProbe -Raw | ConvertFrom-Json
if ($independent.Version -ne $version -or $independent.Options.ReferenceRoot -ne (Join-Path $release 'Tools')) { throw 'Independent initialization used the wrong suite or version' }
foreach ($key in @('BurnInPath', 'PwrTestPath', 'VideoPath')) {
    $path = $independent.Options.$key
    if (!$path.StartsWith((Join-Path $release 'Tools') + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path)) { throw ('Independent bundled path missing: ' + $key) }
}
foreach ($excluded in @('Tools\SFT\SFTClassic.exe', 'Tools\3DMark\3DMarkTest.ini', 'Tools\FurMark\engine\FurMark.exe')) {
    if (Test-Path -LiteralPath (Join-Path $release $excluded)) { throw ('Excluded production component present: ' + $excluded) }
}
$verificationDate = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'China Standard Time').ToString('yyyy-MM-dd')
$summary = [ordered]@{ Date = $verificationDate; Version = $version; ReleaseFilesVerified = $hashCount; EvidenceFilesVerified = $evidenceCount; Aliases = $results; ReferenceBinaries = $originals; IndependentInitializationVerified=$true; ExcludedComponentsAbsent=$true }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $qa 'release-verification.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
