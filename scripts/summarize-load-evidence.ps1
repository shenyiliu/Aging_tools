param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Python = '',
    [string]$Validator = '',
    [string]$DotNet = '',
    [switch]$RequireComplete
)
$ErrorActionPreference = 'Stop'
if (!$Python) {
    $runtimePython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    $Python = if (Test-Path -LiteralPath $runtimePython) { $runtimePython } else { (Get-Command python -ErrorAction Stop).Source }
}
$arguments = @((Join-Path $PSScriptRoot 'summarize-load-evidence.py'), '--root', [IO.Path]::GetFullPath($Root))
if ($Validator) { $arguments += @('--validator', [IO.Path]::GetFullPath($Validator)) }
if ($DotNet) { $arguments += @('--dotnet', [IO.Path]::GetFullPath($DotNet)) }
if ($RequireComplete) { $arguments += '--require-complete' }
& $Python @arguments
if ($LASTEXITCODE -ne 0) { throw "负载证据汇总失败（退出码 $LASTEXITCODE）；原始证据未修改。" }
