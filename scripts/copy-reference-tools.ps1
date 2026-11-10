param([string]$ReferenceRoot, [string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$ReferenceRoot) { $ReferenceRoot = Join-Path (Split-Path -Parent $repo) 'GW_TEST' }
if (!$Destination) { $Destination = Join-Path $repo 'Tools' }
$sourceRoot = [IO.Path]::GetFullPath($ReferenceRoot)
$targetRoot = [IO.Path]::GetFullPath($Destination)
if ($sourceRoot.Equals($targetRoot, [StringComparison]::OrdinalIgnoreCase) -or $targetRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be outside the reference suite.' }
New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
# Keep complete engine/helper directories, but omit the original host/wrappers,
# factory provisioning, old logs and uninstallers. Never execute copied tools here.
$entries = @('BurnInTest\BurnInTest', 'Sleep', 'Reboot\cpufan', 'SFT', 'Other\Tools\CoreTemp', 'Other\ICON', '3DMark', 'FurMark')
$records = [Collections.Generic.List[object]]::new()
foreach ($entry in $entries) {
    $directory = Join-Path $sourceRoot $entry
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { continue }
    $enumeration = [IO.EnumerationOptions]::new()
    $enumeration.RecurseSubdirectories = $true
    $enumeration.AttributesToSkip = [IO.FileAttributes]::ReparsePoint
    foreach ($source in [IO.Directory]::EnumerateFiles($directory, '*', $enumeration)) {
        $name = [IO.Path]::GetFileName($source)
        if ($name -match '^unins\d*\.' -or $name -match '\.log$' -or ($entry -eq 'Sleep' -and $name -ieq 'Sleep.exe')) { continue }
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $source)
        $target = Join-Path $targetRoot $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        $digest = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { Copy-Item -LiteralPath $source -Destination $target -Force }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { throw "Copy verification failed: $relative" }
        $records.Add([ordered]@{ Path=$relative; Bytes=(Get-Item -LiteralPath $source -Force).Length; SHA256=$digest })
    }
}
foreach ($entry in @('Other\Language.ini','Reboot\Language.ini')) {
    $source = Join-Path $sourceRoot $entry
    if (!(Test-Path -LiteralPath $source)) { continue }
    $target = Join-Path $targetRoot $entry
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    $records.Add([ordered]@{Path=$entry;Bytes=(Get-Item -LiteralPath $source).Length;SHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()})
}
# Reuse only aging settings. The factory host configuration is never bootstrapped.
$configPath = Join-Path $sourceRoot 'Other\Config.ini'
$selected = @('AgingSwitch','Reboot','Sleep','BurnIn','FurMark','3DMark','Video')
$config = [Collections.Generic.List[string]]::new()
$config.Add('[Main]'); $config.Add('AgingFailedStop=N'); $config.Add('ShutdownAfterAging=N'); $config.Add('AfterAgingScript='); $config.Add('TestWithMaxVolume=Y'); $config.Add('AgingInNextBoot=N'); $config.Add('')
$include = $false
foreach ($line in Get-Content -LiteralPath $configPath) {
    if ($line -match '^\s*\[([^\]]+)\]') { $include = $selected -contains $Matches[1] }
    if ($include) { $config.Add($line) }
}
$config | Set-Content -LiteralPath (Join-Path $targetRoot 'Other\Config.ini') -Encoding utf8
$totalBytes = 0L
foreach ($record in $records) { $totalBytes += $record['Bytes'] }
[ordered]@{ Source='GW_TEST'; CreatedAt=[DateTimeOffset]::Now.ToString('o'); FileCount=$records.Count; Bytes=$totalBytes; Files=$records; Missing=@('FurMark executable engine','3DMark executable engine','Original BurnIn/FurMark/3DMark wrappers'); Generated=@('Other\Config.ini: aging settings only') } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $targetRoot 'reuse-manifest.json') -Encoding utf8
Write-Output "Copied and SHA256-verified $($records.Count) reusable files into $targetRoot"
