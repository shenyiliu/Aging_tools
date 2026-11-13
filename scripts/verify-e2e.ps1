param([switch]$Hardware, [switch]$SkipBuild, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tests\Aging.E2E\Aging.E2E.csproj'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$sdk = if ($dotnet) { $dotnet.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\dotnet\dotnet.exe' }
if (!(Test-Path -LiteralPath $sdk)) { throw 'Install .NET SDK 10 first.' }
$env:DOTNET_ROOT = Split-Path -Parent $sdk
if (!$SkipBuild) {
    & $sdk build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'E2E build failed' }
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts\verification\e2e-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6)) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$arguments = @('"' + $OutputDirectory + '"')
if ($Hardware) { $arguments += '--hardware' }
$process = Start-Process -FilePath (Join-Path $repo 'tests\Aging.E2E\bin\Release\net10.0-windows\Aging.E2E.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
$result = Join-Path $OutputDirectory 'result.json'
if (!(Test-Path -LiteralPath $result)) { throw 'E2E result not produced' }
$cases = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
$cases | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0 -or @($cases | Where-Object { !$_.Passed }).Count -gt 0) { throw "E2E failed; see $result" }
Write-Output "E2E passed: $($cases.Count) cases; evidence: $result"
