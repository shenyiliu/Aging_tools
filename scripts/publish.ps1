param([string]$OutputDirectory, [string]$PackagesDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'verify-tools.ps1')
if (!$?) { throw 'Bundled component verification failed' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\GW_AgingTool_win-x64' }
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { $sdk = $dotnet.Source } else { $sdk = Join-Path $env:LOCALAPPDATA 'Programs\dotnet\dotnet.exe' }
$restoreArgs = @()
if ($PackagesDirectory) { $restoreArgs = @('--packages', $PackagesDirectory) }
& $sdk restore (Join-Path $repo 'src\Aging.WinForms\Aging.WinForms.csproj') -r win-x64 --nologo @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Restore before publish failed' }
& $sdk clean (Join-Path $repo 'src\Aging.WinForms\Aging.WinForms.csproj') -c Release -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'Clean before publish failed' }
Get-ChildItem -LiteralPath (Join-Path $repo 'src\Aging.WinForms\obj') -Filter apphost.exe -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
# Keep prior default releases recoverable and publish into an empty folder, avoiding stale copies.
$defaultOutput = Join-Path $repo 'artifacts\GW_AgingTool_win-x64'
if ([IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') -eq [IO.Path]::GetFullPath($defaultOutput).TrimEnd('\') -and (Test-Path -LiteralPath $OutputDirectory)) {
    Move-Item -LiteralPath $OutputDirectory -Destination ($defaultOutput + '.previous-' + [guid]::NewGuid().ToString('N'))
}
& $sdk publish (Join-Path $repo 'src\Aging.WinForms\Aging.WinForms.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $OutputDirectory --nologo @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$expectedVersion = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version + '.0'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $OutputDirectory 'Test.exe')).FileVersion.Trim() -ne $expectedVersion) { throw 'Published executable version does not match the requested version' }
Copy-Item -LiteralPath (Join-Path $repo 'engine-profiles.example.json') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\验收与差异.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\安装与多机型部署.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\真实功能整改记录.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\真实短测结果_20261110.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\原版老化行为对照_20261110.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\CoreTemp温度桥接.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\厂商风扇适配.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\REA复刻与组件复用.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\重装原版组件复查_20261110.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\ZIP安装说明.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\0.2.2修复与验收记录.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\0.2.4安装错误32修复记录.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\0.2.5登录逻辑移除与启动修复.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\0.2.6组件补齐与打包记录.md') -Destination $OutputDirectory -Force
foreach ($name in @('0.3.0修复与验收记录.md', '原版老化行为对照_20261112.md')) {
    Copy-Item -LiteralPath (Join-Path $repo ('docs\' + $name)) -Destination $OutputDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'src\Aging.WinForms\Branding') -Destination $OutputDirectory -Recurse -Force
$toolsRoot = Join-Path $repo 'Tools'
if (!(Test-Path -LiteralPath (Join-Path $toolsRoot 'reuse-manifest.json'))) { throw 'Reusable components are missing. Run scripts/copy-reference-tools.ps1 first.' }
$componentManifest = Get-Content -LiteralPath (Join-Path $toolsRoot 'reuse-manifest.json') -Raw | ConvertFrom-Json
# Publish only the reviewed aging component inventory; unrelated legacy tools may exist in the developer copy.
foreach ($relative in @('reuse-manifest.json') + @($componentManifest.Files.Path) + @($componentManifest.GeneratedFiles.Path)) {
    $sourceFile = Join-Path $toolsRoot $relative
    $targetFile = Join-Path (Join-Path $OutputDirectory 'Tools') $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetFile) | Out-Null
    Copy-Item -LiteralPath $sourceFile -Destination $targetFile -Force
}
foreach ($relative in @('FurMark\engine-manifest.json', 'FurMark\ENGINE-LICENSE-NOTE.md')) {
    $sourceFile = Join-Path $toolsRoot $relative
    if (Test-Path -LiteralPath $sourceFile) {
        $targetFile = Join-Path (Join-Path $OutputDirectory 'Tools') $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetFile) | Out-Null
        Copy-Item -LiteralPath $sourceFile -Destination $targetFile -Force
    }
}
# Evidence is diagnostic data only: no engine binaries, executable wrappers or reusable active checkpoints.
$evidenceFiles = @(
    'artifacts\reverse\20261112\rea-evidence.json',
    'artifacts\reverse\20261112\recovery-manifest.json',
    'artifacts\reverse\20261112\GPU-AND-RECOVERY-NOTES.md',
    'artifacts\validation\20261112\core-final.trx',
    'artifacts\validation\20261112\windows-final.trx',
    'artifacts\validation\20261112\resume-validation.md',
    'artifacts\validation\20261112\resume-task-integration-elevated.trx',
    'artifacts\validation\20261112\resume-cleanup-verification.txt',
    'artifacts\acceptance\20261112-load-summary.json',
    'artifacts\acceptance\20261112-load-curves.svg',
    'artifacts\acceptance\20261112-load-curves.png',
    'artifacts\acceptance\20261112-furmark-final\application-crash-events.json'
)
foreach ($relative in $evidenceFiles) {
    $source = Join-Path $repo $relative
    if (!(Test-Path -LiteralPath $source)) { throw ('Missing current evidence: ' + $relative) }
}
foreach ($case in @('20261112-hardware-elevated', '20261112-furmark', '20261112-furmark-final', '20261112-software-final')) {
    $caseRoot = Join-Path $repo ('artifacts\acceptance\' + $case)
    foreach ($source in Get-ChildItem -LiteralPath $caseRoot -Recurse -File | Where-Object {
        $_.Name -eq 'result.json' -or $_.Name -eq 'load-telemetry.tsv' -or $_.Name -eq 'GW_CoreTemp.log' -or
        $_.Name -eq 'result.log' -or $_.Name -eq 'engine.txt' -or $_.Name -eq 'executed.bits' -or
        $_.Name -like '*.command.json' -or $_.Name -like '*.stdout.txt' -or $_.Name -like '*.stderr.txt' -or
        $_.Name -like 'FurMark_*.txt' -or $_.Name -eq 'FurMark-Scores.txt' -or $_.Name -like 'furmark-gpu-*.csv'
    }) {
        $evidenceFiles += [IO.Path]::GetRelativePath($repo, $source.FullName)
    }
}
# Keep original evidence paths inside an archive: installing their deep directory tree
# otherwise exceeds Win32 path limits under ordinary long or Chinese installation paths.
$archive = [IO.Compression.ZipFile]::Open((Join-Path $OutputDirectory 'Evidence.zip'), [IO.Compression.ZipArchiveMode]::Create)
try {
    $evidenceHashes = @()
    foreach ($relative in ($evidenceFiles | Sort-Object -Unique)) {
        $source = Join-Path $repo $relative
        $entryName = $relative.Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $source, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $evidenceHashes += '{0}  {1}' -f (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant(), $entryName
    }
    $writer = [IO.StreamWriter]::new($archive.CreateEntry('SHA256SUMS.txt').Open())
    try { $writer.WriteLine($evidenceHashes -join "`n") } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
$iconRoot = Join-Path $toolsRoot 'Other\ICON'
$assetRoot = Join-Path $OutputDirectory 'Assets'
New-Item -ItemType Directory -Force -Path $assetRoot | Out-Null
if (Test-Path -LiteralPath $iconRoot) {
    Get-ChildItem -LiteralPath $iconRoot -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $assetRoot -Recurse -Force }
}
$sdkVersion = (& $sdk --version).Trim()
$sdkHome = Split-Path -Parent $sdk
$hostModel = Join-Path $sdkHome ('sdk\' + $sdkVersion + '\Microsoft.NET.HostModel.dll')
[Reflection.Assembly]::LoadFrom($hostModel) | Out-Null
$hostTemplate = Get-ChildItem -LiteralPath (Join-Path $sdkHome 'packs\Microsoft.NETCore.App.Host.win-x64') -Filter 'apphost.exe' -Recurse | Sort-Object FullName -Descending | Select-Object -First 1
foreach ($alias in @('Other\ControlPanel.exe','Other\ViewLOG.exe','Reboot\Reboot.exe','Sleep\Sleep.exe','BurnInTest\BurnInTest.exe','FurMark\FurMarkTest.exe','3DMark\3DMarkTest.exe')) {
    $aliasPath = Join-Path $OutputDirectory $alias
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $aliasPath) | Out-Null
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($hostTemplate.FullName, $aliasPath, '..\Test.dll', $true, (Join-Path $OutputDirectory 'Test.exe'), $false, $false, $null)
}
# Preserve the supplied component copies and their manifests, including separate reference backups.
Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName)
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
} | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Output $OutputDirectory
