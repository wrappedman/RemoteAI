param([switch]$SkipPortable)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build RemoteDesk.sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($name in @('Core', 'Protocol', 'Security')) {
    dotnet run --project "tests/RemoteDesk.$name.Tests" -c Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
dotnet publish src/RemoteDesk.Desktop -c Release -r win-x64 --self-contained false -o run
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $SkipPortable) {
    dotnet publish src/RemoteDesk.Desktop -c Release -r win-x64 --self-contained true -o artifacts/portable -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Compress-Archive -Path artifacts/portable/* -DestinationPath run/RemoteDesk-win-x64-portable.zip -CompressionLevel Optimal -Force
}
Get-ChildItem run -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Name -ne 'BUILD-INFO.md' } | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)  $($_.Name)" } | Set-Content run/SHA256SUMS.txt
Write-Host 'Build and tests passed. See run/README.md.'
