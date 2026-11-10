param([string]$Compiler, [switch]$SkipPublish, [string]$PackagesDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts\GW_AgingTool_win-x64'
if (!$SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') -PackagesDirectory $PackagesDirectory; if (!$?) { throw 'Publish failed' } }
if (!$Compiler) {
    $choices = @((Join-Path $env:LOCALAPPDATA 'Programs\GW-AgingTool-BuildTools\Inno Setup 7\ISCC.exe'), 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 7\ISCC.exe')
    $Compiler = $choices | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$Compiler -or !(Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6.3+ or 7 and pass -Compiler <ISCC.exe>.' }
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$output = Join-Path $repo 'artifacts\installer'
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $Compiler ("/DAppVersion=" + $version) ("/DSourceDir=" + $release) ("/DOutputDir=" + $output) (Join-Path $repo 'installer\GWAgingTool.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$setup = Join-Path $output ("GW_AgingTool_Setup_" + $version + '_win-x64.exe')
(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($setup) | Set-Content -LiteralPath ($setup + '.sha256') -Encoding utf8
Get-Item -LiteralPath $setup | Select-Object FullName,Length
