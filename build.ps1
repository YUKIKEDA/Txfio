# Local verification gate (source of truth while GitHub Actions may be restricted).
# Usage: ./build.ps1
# Windows only. On Linux do not run this script; run restore/format/build/test directly (see AGENTS.md).
# Exits non-zero on failure.

$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
    Write-Error "build.ps1 is Windows-only. On Linux run restore, format --verify-no-changes, build, and test; Linux tests are a pre-PR check, not the merge gate."
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

# Stress tests (tests/Txfio.Stress) are outside this gate. Run that project with dotnet test explicitly.
$testProject = Join-Path $PSScriptRoot "tests\Txfio.Tests\Txfio.Tests.csproj"
dotnet test $testProject --no-build -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "build.ps1 completed successfully."
