param([string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { $sdk = $dotnet.Source } else { $sdk = Join-Path $env:LOCALAPPDATA 'Programs\dotnet\dotnet.exe' }
if (!(Test-Path -LiteralPath $sdk)) { throw 'Install .NET SDK 10 first.' }
& $sdk build (Join-Path $repo 'AgingTool.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if (!$SkipTests) {
    $env:DOTNET_ROOT = Split-Path -Parent $sdk
    & $sdk test (Join-Path $repo 'tests\Aging.Tests\Aging.Tests.csproj') -c $Configuration --no-build --logger 'trx;LogFileName=core-tests.trx' --results-directory (Join-Path $repo 'artifacts\verification')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    & $sdk test (Join-Path $repo 'tests\Aging.Windows.Tests\Aging.Windows.Tests.csproj') -c $Configuration --no-build --logger 'trx;LogFileName=windows-tests.trx' --results-directory (Join-Path $repo 'artifacts\verification')
    if ($LASTEXITCODE -ne 0) { throw 'Windows adapter tests failed' }
}
