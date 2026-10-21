#!/usr/bin/env bash
# Installs the packed Txfio nupkg into a new console project, the way a user
# does, and runs the README Getting started flow against a temporary work
# folder. The project is outside the Txfio namespace and has no
# InternalsVisibleTo, so a public API that consumers cannot call (0.1.0
# shipped `Txfio.BeginAsync`, which does not compile after `using Txfio;`,
# #337) fails here.
# Run from the repository root after a pack:
# .github/scripts/package-smoke-test.sh 0.2.0 src/Txfio/bin/Release
set -euo pipefail

version="${1:?usage: package-smoke-test.sh VERSION DIR}"
dir="${2:?usage: package-smoke-test.sh VERSION DIR}"
fail() {
    echo "package-smoke-test: $*" >&2
    exit 1
}

[[ -f "$dir/Txfio.$version.nupkg" ]] || fail "$dir/Txfio.$version.nupkg is missing"

to_native() {
    (cd "$1" && (pwd -W 2>/dev/null || pwd))
}
source_dir="$(to_native "$dir")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/app" "$work/folder" "$work/packages"
work_native="$(to_native "$work")"

# Only the local package, and a private package cache: a cached copy of the
# same version from nuget.org must not stand in for the package under test.
cat > "$work/app/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$source_dir" />
  </packageSources>
</configuration>
EOF

cat > "$work/app/ConsumerSmoke.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RestorePackagesPath>$work_native/packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Txfio" Version="[$version]" />
  </ItemGroup>
</Project>
EOF

# The Getting started code of README.md, in a namespace outside Txfio.
cat > "$work/app/Program.cs" <<'EOF'
using Txfio;

namespace ConsumerSmoke;

internal static class Program
{
    private static async Task<int> Main()
    {
        string work = Environment.GetEnvironmentVariable("TXFIO_SMOKE_FOLDER")
            ?? throw new InvalidOperationException("TXFIO_SMOKE_FOLDER is not set");

        await TxFileIO.RecoverAsync(work);

        await using (ITransaction tx = await TxFileIO.BeginAsync(work))
        {
            await tx.WriteAllTextAsync("a.txt", "hello");
            CommitReport report = await tx.CommitAsync();
            if (report.Result != CommitResult.Succeeded)
            {
                Console.Error.WriteLine("CommitAsync returned " + report.Result);
                return 1;
            }
        }

        string text = await File.ReadAllTextAsync(Path.Combine(work, "a.txt"));
        if (text != "hello")
        {
            Console.Error.WriteLine("a.txt has \"" + text + "\" after the commit");
            return 1;
        }

        if (TxFileIO.IsStagingName("a.txt") || !TxFileIO.IsStagingName("a.txt." + Guid.NewGuid().ToString("D") + ".txnew"))
        {
            Console.Error.WriteLine("IsStagingName gave a wrong answer");
            return 1;
        }

        return 0;
    }
}
EOF

if ! TXFIO_SMOKE_FOLDER="$work_native/folder" dotnet run --project "$work/app/ConsumerSmoke.csproj" -v q > "$work/run.log" 2>&1; then
    grep -E 'error|Error|returned|has "' "$work/run.log" >&2 || cat "$work/run.log" >&2
    fail "the packed Txfio $version does not work from a consumer project"
fi

echo "package-smoke-test: Txfio $version installs and runs Getting started from a consumer project"
