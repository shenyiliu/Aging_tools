param([switch]$RunSmoke)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts\GW_AgingTool_win-x64'
$qa = Join-Path $repo 'artifacts\verification'
New-Item -ItemType Directory -Force -Path $qa | Out-Null
$aliases = @('Test.exe','Other\ControlPanel.exe','Other\ViewLOG.exe','Reboot\Reboot.exe','Sleep\Sleep.exe','BurnInTest\BurnInTest.exe','FurMark\FurMarkTest.exe','3DMark\3DMarkTest.exe')
$results = @()
foreach ($alias in $aliases) {
    $output = Join-Path $qa ('alias-' + [IO.Path]::GetFileNameWithoutExtension($alias) + '.txt')
    $probe = Start-Process -FilePath (Join-Path $release $alias) -ArgumentList @('--alias-probe', ('"' + $output + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($probe.ExitCode -ne 0 -or !(Test-Path -LiteralPath $output)) { throw ('Alias failed: ' + $alias) }
    $lines = Get-Content -LiteralPath $output
    if ($lines[0] -ne [IO.Path]::GetFileNameWithoutExtension($alias).ToLowerInvariant()) { throw ('Wrong module: ' + $alias) }
    $results += [pscustomobject]@{ Module = $alias; ExitCode = $probe.ExitCode; SharedDirectory = $lines[1] }
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
    $probe = Start-Process -FilePath (Join-Path $release 'Test.exe') -ArgumentList @('--smoke', ('"' + $smoke + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($probe.ExitCode -ne 0) { throw 'Published UI/simulation smoke failed' }
}
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$summary = [ordered]@{ Date = '2026-11-10'; Version = $version; ReleaseFilesVerified = $hashCount; Aliases = $results; ReferenceBinaries = $originals }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $qa 'release-verification.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8
