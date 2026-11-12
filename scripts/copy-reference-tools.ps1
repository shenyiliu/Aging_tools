param([string]$ReferenceRoot, [string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$ReferenceRoot) {
    $reinstalled = Join-Path ([Environment]::GetFolderPath('Desktop')) 'GW_TEST'
    $ReferenceRoot = if (Test-Path -LiteralPath $reinstalled -PathType Container) { $reinstalled } else { Join-Path (Split-Path -Parent $repo) 'GW_TEST' }
}
if (!$Destination) { $Destination = Join-Path $repo 'Tools' }
$sourceRoot = [IO.Path]::GetFullPath($ReferenceRoot)
$targetRoot = [IO.Path]::GetFullPath($Destination)
if ($sourceRoot.Equals($targetRoot, [StringComparison]::OrdinalIgnoreCase) -or $targetRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be outside the reference suite.' }
foreach ($required in @('Other\Config.ini','BurnInTest\BurnInTest\bit.exe','Sleep\pwrtest_x64.exe','Other\Tools\CoreTemp\CoreTemp_x64.exe','SFT\TestVideo.mp4')) {
    if (!(Test-Path -LiteralPath (Join-Path $sourceRoot $required) -PathType Leaf)) { throw "Required reference component is missing: $required" }
}
New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
# Keep complete engine/helper directories, but omit the original host/wrappers,
# factory provisioning, old logs and uninstallers. Never execute copied tools here.
$entries = @('BurnInTest\BurnInTest', 'Sleep', 'Reboot\cpufan', 'Other\Tools\CoreTemp', 'Other\ICON', '3DMark', 'FurMark')
$records = [Collections.Generic.List[object]]::new()
foreach ($entry in $entries) {
    $directory = Join-Path $sourceRoot $entry
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { continue }
    $enumeration = [IO.EnumerationOptions]::new()
    $enumeration.RecurseSubdirectories = $true
    $enumeration.AttributesToSkip = [IO.FileAttributes]::ReparsePoint
    foreach ($source in [IO.Directory]::EnumerateFiles($directory, '*', $enumeration)) {
        $name = [IO.Path]::GetFileName($source)
        if ($name -match '^unins\d*\.' -or $name -match '\.log$' -or ($entry -eq 'Sleep' -and $name -ieq 'Sleep.exe') -or $name -in @('FurMarkTest.exe','3DMarkTest.exe','3DMarkTest.ini')) { continue }
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $source)
        $target = Join-Path $targetRoot $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        $digest = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { Copy-Item -LiteralPath $source -Destination $target -Force }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { throw "Copy verification failed: $relative" }
        $records.Add([ordered]@{ Path=$relative; Bytes=(Get-Item -LiteralPath $source -Force).Length; SHA256=$digest })
    }
}
foreach ($entry in @('Other\Language.ini','Reboot\Language.ini','SFT\TestVideo.mp4')) {
    $source = Join-Path $sourceRoot $entry
    if (!(Test-Path -LiteralPath $source)) { continue }
    $target = Join-Path $targetRoot $entry
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    $records.Add([ordered]@{Path=$entry;Bytes=(Get-Item -LiteralPath $source).Length;SHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()})
}
# Keep the original stress wrappers as reference copies, separate from our module entry points.
New-Item -ItemType Directory -Force -Path (Join-Path $targetRoot 'OriginalWrappers') | Out-Null
$wrappers = [Collections.Generic.List[object]]::new()
foreach ($entry in @('BurnInTest\BurnInTest.exe','FurMark\FurMarkTest.exe','3DMark\3DMarkTest.exe')) {
    $source = Join-Path $sourceRoot $entry
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $relative = Join-Path 'OriginalWrappers' $entry
    $target = Join-Path $targetRoot $relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
    $digest = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { throw "Copy verification failed: $relative" }
    $record = [ordered]@{ Path=$relative; SourcePath=$entry; Bytes=(Get-Item -LiteralPath $source).Length; SHA256=$digest }
    $records.Add($record)
    $wrappers.Add($record)
}
[ordered]@{ Source=$sourceRoot; Files=$wrappers; Note='Reference copies only; these are not the FurMark/3DMark workload engines.' } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $targetRoot 'OriginalWrappers\manifest.json') -Encoding utf8
# Reuse only aging settings. The factory host configuration is never bootstrapped.
$configPath = Join-Path $sourceRoot 'Other\Config.ini'
$selected = @('AgingSwitch','Reboot','Sleep','S3','S4','CS','BurnIn','FurMark','3DMark','Video')
$config = [Collections.Generic.List[string]]::new()
$config.Add('[Main]'); $config.Add('AgingFailedStop=N'); $config.Add('ShutdownAfterAging=N'); $config.Add('AfterAgingScript='); $config.Add('TestWithMaxVolume=Y'); $config.Add('AgingInNextBoot=N'); $config.Add('')
$include = $false
foreach ($line in Get-Content -LiteralPath $configPath) {
    if ($line -match '^\s*\[([^\]]+)\]') { $include = $selected -contains $Matches[1] }
    if ($include) { $config.Add($line) }
}
$config | Set-Content -LiteralPath (Join-Path $targetRoot 'Other\Config.ini') -Encoding utf8
$generated = foreach ($relative in @('Other\Config.ini','OriginalWrappers\manifest.json')) {
    $path = Join-Path $targetRoot $relative
    [ordered]@{ Path=$relative; Bytes=(Get-Item -LiteralPath $path).Length; SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$missing = @()
if (!(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'FurMark') -Filter 'FurMark.exe' -File -Recurse -ErrorAction SilentlyContinue)) { $missing += 'FurMark executable engine (absent from reference; separately acquired developer engine is not part of this reuse inventory)' }
if (!(Get-ChildItem -LiteralPath (Join-Path $sourceRoot '3DMark') -File -Recurse -ErrorAction SilentlyContinue | Where-Object Name -in @('3DMarkCmd.exe','3DMark11Cmd.exe'))) { $missing += '3DMark executable engine' }
$totalBytes = 0L
foreach ($record in $records) { $totalBytes += $record['Bytes'] }
[ordered]@{ Source=$sourceRoot; CreatedAt=[DateTimeOffset]::Now.ToString('o'); FileCount=$records.Count; Bytes=$totalBytes; Files=$records; Missing=$missing; Generated=@('Other\Config.ini: aging settings only','OriginalWrappers\manifest.json'); GeneratedFiles=@($generated); Note='Original wrappers are preserved separately in Tools/OriginalWrappers. No copied programs were executed during import.' } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $targetRoot 'reuse-manifest.json') -Encoding utf8
Write-Output "Copied and SHA256-verified $($records.Count) reusable files into $targetRoot"
