# Local verification gate (source of truth while GitHub Actions may be restricted).
# Usage: ./build.ps1
# Windows only. On Linux do not run this script; restore/build/format only (see AGENTS.md).
# Exits non-zero on failure.

$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
    Write-Error "build.ps1 is Windows-only. On Linux run restore, format --verify-no-changes, and build; do not treat tests as the merge gate."
    exit 1
}

$sln = Get-ChildItem -Path . -Filter *.slnx -File -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $sln) {
    Write-Error "No .slnx found. This repo uses Txfio.slnx only (no .sln)."
    exit 1
}

$slnPath = $sln.FullName
Write-Host "Using solution: $slnPath"

dotnet restore $slnPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet format $slnPath --verify-no-changes
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $slnPath --no-restore -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test $slnPath --no-build -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "build.ps1 completed successfully."
