$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& dotnet build PagefileMonitor.sln -c Release -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& './tests/bin/x64/Release/net48/PagefileMonitor.Tests.exe'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
New-Item -ItemType Directory -Force release | Out-Null
foreach ($taskProject in @('Core','Service','Desktop','Recovery')) {
    Get-ChildItem -LiteralPath "src/$taskProject/bin/x64/Release/net48" -File |
        Where-Object { $_.Extension -in @('.exe','.dll','.config') } |
        Copy-Item -Destination release -Force
}
Copy-Item -LiteralPath commerce.json -Destination release -Force
Write-Host 'Release ready: release/PagefileMonitor.exe'
New-Item -ItemType Directory -Force release/tests | Out-Null
Get-ChildItem -LiteralPath 'tests/bin/x64/Release/net48' -File |
    Where-Object { $_.Extension -in @('.exe','.dll','.config') } |
    Copy-Item -Destination release/tests -Force
