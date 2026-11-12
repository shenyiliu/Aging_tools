param([string]$ToolsDirectory, [switch]$VerifySource)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$ToolsDirectory) { $ToolsDirectory = Join-Path $repo 'Tools' }
$root = [IO.Path]::GetFullPath($ToolsDirectory).TrimEnd('\')
$manifest = Get-Content -LiteralPath (Join-Path $root 'reuse-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.FileCount -ne @($manifest.Files).Count -or $manifest.FileCount -eq 0) { throw 'Invalid component count' }
$seen = @{}
$bytes = 0L
foreach ($record in @($manifest.Files) + @($manifest.GeneratedFiles)) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $record.Path))
    if (!$path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($path)) { throw ('Invalid or duplicate component path: ' + $record.Path) }
    $seen[$path] = $true
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing component: ' + $record.Path) }
    if ((Get-Item -LiteralPath $path).Length -ne $record.Bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $record.SHA256) { throw ('Component checksum mismatch: ' + $record.Path) }
}
foreach ($record in $manifest.Files) {
    $bytes += $record.Bytes
    if ($VerifySource) {
        $relative = if ($record.SourcePath) { $record.SourcePath } else { $record.Path }
        if ((Get-FileHash -LiteralPath (Join-Path $manifest.Source $relative) -Algorithm SHA256).Hash -ne $record.SHA256) { throw ('Reference source changed: ' + $relative) }
    }
}
if ($bytes -ne $manifest.Bytes) { throw 'Component byte count mismatch' }
foreach ($relative in @('BurnInTest\BurnInTest\bit.exe','BurnInTest\BurnInTest\LastUsed_0.bitcfg','BurnInTest\BurnInTest\LastUsed_1.bitcfg','BurnInTest\BurnInTest\LastUsed_2.bitcfg','Sleep\pwrtest_x64.exe','Other\Tools\CoreTemp\CoreTemp_x64.exe','SFT\TestVideo.mp4','Reboot\cpufan\IP3\CPUFUN.cmd','Reboot\cpufan\WB\CPUFUN.cmd','Reboot\cpufan\Ed\CPUFUN.cmd','Other\Config.ini')) {
    if (!$seen.ContainsKey((Join-Path $root $relative))) { throw ('Required component is not in the manifest: ' + $relative) }
}
$config = Get-Content -LiteralPath (Join-Path $root 'Other\Config.ini') -Raw
$sections = [regex]::Matches($config, '(?m)^\s*\[([^\]]+)\]') | ForEach-Object { $_.Groups[1].Value }
if (@($sections | Where-Object { $_ -notin @('Main','AgingSwitch','Reboot','Sleep','S3','S4','CS','BurnIn','FurMark','3DMark','Video') }).Count -gt 0 -or $config -notmatch '(?m)^AfterAgingScript=\s*$') { throw 'Bundled configuration must contain only aging settings without factory completion actions' }
[ordered]@{ ToolsDirectory=$root; SourceFilesVerified=$manifest.FileCount; GeneratedFilesVerified=@($manifest.GeneratedFiles).Count; SourceVerified=[bool]$VerifySource; MissingEngines=@($manifest.Missing) } | ConvertTo-Json
