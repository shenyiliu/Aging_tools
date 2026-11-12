param([string]$ZipPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$baseName = 'GMK_AgingTool_Setup_' + $version + '_win-x64'
if (!$ZipPath) { $ZipPath = Join-Path $repo ('artifacts\installer\' + $baseName + '.zip') }
$expectedNames = @(($baseName + '.exe'), 'ZIP安装说明.md', '安装与多机型部署.md', '重装原版组件复查_20261110.md', '0.2.2修复与验收记录.md', '0.2.4安装错误32修复记录.md', '0.2.5登录逻辑移除与启动修复.md', '0.2.6组件补齐与打包记录.md', '0.3.0修复与验收记录.md', '原版老化行为对照_20261112.md', 'SHA256SUMS.txt')
$outerHash = (Get-Content -LiteralPath ($ZipPath + '.sha256') -Raw).Trim()
if ($outerHash -notmatch '^([a-f0-9]{64})  (.+)$' -or $Matches[2] -ne [IO.Path]::GetFileName($ZipPath)) { throw 'Invalid ZIP checksum record' }
if ((Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash -ne $Matches[1]) { throw 'ZIP checksum mismatch' }
$zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    if (@(Compare-Object ($names | Sort-Object) ($expectedNames | Sort-Object)).Count -gt 0) { throw 'Unexpected, duplicate, or missing ZIP entry' }
    $reader = [IO.StreamReader]::new($zip.GetEntry('SHA256SUMS.txt').Open())
    try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $verified = @{}
    foreach ($line in ($manifest -split '\r?\n' | Where-Object { $_.Length -gt 0 })) {
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid inner checksum record' }
        $expectedHash = $Matches[1]; $name = $Matches[2]
        if ($name -eq 'SHA256SUMS.txt' -or $verified.ContainsKey($name)) { throw 'Invalid or duplicate manifest entry' }
        $entry = $zip.GetEntry($name)
        if (!$entry) { throw ('Missing entry: ' + $name) }
        $stream = $entry.Open()
        try { $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($actualHash -ne $expectedHash) { throw ('ZIP payload checksum mismatch: ' + $name) }
        $verified[$name] = $actualHash
    }
    if ($verified.Count -ne $expectedNames.Count - 1) { throw 'Incomplete ZIP checksum manifest' }
} finally { $zip.Dispose() }
$setup = Join-Path (Split-Path -Parent $ZipPath) ($baseName + '.exe')
if ($verified[$baseName + '.exe'] -ne (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'ZIP contains a different setup' }
$qa = Join-Path $repo ('artifacts\verification\zip-' + $version + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa | Out-Null
Expand-Archive -LiteralPath $ZipPath -DestinationPath $qa
$expandedSetup = Join-Path $qa ($baseName + '.exe')
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($expandedSetup).FileVersion.Trim()
if ($fileVersion -ne ($version + '.0')) { throw 'Extracted setup version mismatch' }
foreach ($entry in $verified.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $qa $entry.Key) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw ('Extracted file mismatch: ' + $entry.Key) }
}
[ordered]@{ Version=$version; ZipPath=$ZipPath; ZipSHA256=(Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant(); SetupSHA256=$verified[$baseName + '.exe']; EntriesVerified=$expectedNames.Count; ExtractionVerified=$true; SetupVersion=$fileVersion; InstallerLifecycleTest=$false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $qa 'result.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $qa 'result.json')
