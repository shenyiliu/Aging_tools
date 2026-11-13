param([switch]$SkipBuild, [string]$Compiler, [string]$PackagesDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -Compiler $Compiler -PackagesDirectory $PackagesDirectory
    if (!$?) { throw 'Installer build failed' }
}
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$output = Join-Path $repo 'artifacts\installer'
$baseName = 'GMK_AgingTool_Setup_' + $version + '_win-x64'
$setup = Join-Path $output ($baseName + '.exe')
if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer not found' }
$staging = Join-Path $repo ('artifacts\zip-staging-' + $version + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
Copy-Item -LiteralPath $setup -Destination $staging
foreach ($name in @('ZIP安装说明.md', '安装与多机型部署.md', '重装原版组件复查_20261110.md', '0.2.2修复与验收记录.md', '0.2.4安装错误32修复记录.md', '0.2.5登录逻辑移除与启动修复.md', '0.2.6组件补齐与打包记录.md', '0.3.0修复与验收记录.md', '原版老化行为对照_20261112.md', '目标机测试与修复_20261113.md')) {
    Copy-Item -LiteralPath (Join-Path $repo ('docs\' + $name)) -Destination $staging
}
Get-ChildItem -LiteralPath $staging -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $staging 'SHA256SUMS.txt') -Encoding utf8
$zip = Join-Path $output ($baseName + '.zip')
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $staging -File).FullName -DestinationPath $zip -CompressionLevel Optimal -Force
(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) |
    Set-Content -LiteralPath ($zip + '.sha256') -Encoding utf8
Get-Item -LiteralPath $zip | Select-Object FullName,Length
