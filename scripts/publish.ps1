param([string]$OutputDirectory, [string]$PackagesDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\GW_AgingTool_win-x64' }
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { $sdk = $dotnet.Source } else { $sdk = Join-Path $env:LOCALAPPDATA 'Programs\dotnet\dotnet.exe' }
$restoreArgs = @()
if ($PackagesDirectory) { $restoreArgs = @('--packages', $PackagesDirectory) }
& $sdk publish (Join-Path $repo 'src\Aging.WinForms\Aging.WinForms.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $OutputDirectory --nologo @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
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
$toolsRoot = Join-Path $repo 'Tools'
if (!(Test-Path -LiteralPath (Join-Path $toolsRoot 'reuse-manifest.json'))) { throw 'Reusable components are missing. Run scripts/copy-reference-tools.ps1 first.' }
Copy-Item -LiteralPath $toolsRoot -Destination $OutputDirectory -Recurse -Force
$iconRoot = Join-Path (Split-Path -Parent $repo) 'GW_TEST\Other\ICON'
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
# Keep original third-party engines separate: no license, driver or factory-service files are copied.
Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName)
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
} | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Output $OutputDirectory
