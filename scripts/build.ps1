#!/usr/bin/env pwsh
# Build DunhillPrintStudio.exe — single file, self-contained, no .NET runtime needed.
#
# Run on a Windows PC with .NET 8 SDK installed:
#   .\scripts\build.ps1
#
# Output: bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\DunhillPrintStudio.exe

$ErrorActionPreference = "Stop"

Write-Host "==> Restoring NuGet packages..." -ForegroundColor Cyan
dotnet restore

Write-Host "==> Building release single-file..." -ForegroundColor Cyan
dotnet publish `
    -c Release `
    -r win-x64 `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    --verbosity minimal

$exe = "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\DunhillPrintStudio.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Build succeeded but exe not found at $exe"
    exit 1
}

$size = (Get-Item $exe).Length / 1MB
Write-Host ""
Write-Host "==> Build complete." -ForegroundColor Green
Write-Host "    Path: $exe"
Write-Host "    Size: $([math]::Round($size, 1)) MB"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Copy $exe to the warehouse PC"
Write-Host "  2. Install WinUSB driver with Zadig (https://zadig.akeo.ie/)"
Write-Host "  3. Plug in ZR300I, launch the .exe, click Settings -> Connect"
