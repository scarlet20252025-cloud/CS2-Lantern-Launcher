param([switch]$Portable, [switch]$Check)
$ErrorActionPreference = 'Stop'
$localSdk = Join-Path $PSScriptRoot 'tools\dotnet\dotnet.exe'
$sdk = if (Test-Path $localSdk) { $localSdk } else { 'dotnet' }
Push-Location $PSScriptRoot
try {
    if ($Check) {
        & $sdk run --project tests/Lantern.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    if ($Portable) {
        & $sdk publish src/Lantern.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/portable
    } else { & $sdk build src/Lantern.App -c Release }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
