# Builds SQL Smart Complete for SSMS: runs the engine tests, builds the extension and
# the installer, and copies SqlSmartCompleteSetup.exe to ..\dist.
#   powershell -ExecutionPolicy Bypass -File ssms\build.ps1 [-Server localhost -Database MyDb]
param(
    [string]$Server,
    [string]$Database,
    [string]$SsmsIde = "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "== Engine tests"
$testArgs = @()
if ($Server -and $Database) { $testArgs = @('--', $Server, $Database) }
dotnet run -c Release --project "$root\SqlSmartComplete.Tests" @testArgs
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

Write-Host "== Extension and installer"
dotnet build "$root\Setup\Setup.csproj" -c Release "/p:SsmsIde=$SsmsIde"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$dist = Join-Path (Split-Path -Parent $root) 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\Setup\bin\Release\net472\SqlSmartCompleteSetup.exe" $dist -Force
Write-Host "== Done: $dist\SqlSmartCompleteSetup.exe"
