#!/usr/bin/env bash
# Compiles every C# block of README.md, README.ja.md, and CHANGELOG.md
# against src/Txfio, so a sample that does not build fails before it is
# published (the README is packed into the nupkg and cannot change later).
# Each block becomes one method. A block that uses `tx.` without declaring
# `ITransaction tx` gets it as a parameter; `using` directives move to the
# top; `record` and `class` declarations move to the enclosing class.
# Run from the repository root: .github/scripts/readme-samples-check.sh
set -euo pipefail

root="$(pwd -W 2>/dev/null || pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cat > "$work/ReadmeSamples.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- Samples are fragments: unused locals and async methods without await are fine. -->
    <NoWarn>CS1998;CS0168;CS0219</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$root/src/Txfio/Txfio.csproj" />
  </ItemGroup>
</Project>
EOF

count=0
for doc in README.md README.ja.md CHANGELOG.md; do
    prefix="$(echo "${doc%.md}" | tr '.' '_')"
    tr -d '\r' < "$doc" | awk -v prefix="$prefix" -v dir="$work" '
        /^ *```csharp *$/ { n++; inside = 1; body = ""; types = ""; usings = ""; next }
        /^ *```/ && inside {
            inside = 0
            file = sprintf("%s/%s_%02d.cs", dir, prefix, n)
            signature = (body ~ /ITransaction tx/ || body !~ /tx\./) ? "()" : "(ITransaction tx)"
            printf "using System.IO.Compression;\nusing System.Text;\nusing System.Text.Json;\nusing Txfio;\n%s\n", usings > file
            printf "// %s, C# block %d\nfile static class Sample\n{\n%s    public static async Task Run%s\n    {\n%s    }\n}\n", prefix, n, types, signature, body >> file
            close(file)
            next
        }
        inside && /^ *using [A-Za-z.]+;$/ { sub(/^ */, ""); usings = usings $0 "\n"; next }
        inside && /^ *(sealed |public )*(record|class) / { types = types "    " $0 "\n"; next }
        inside { body = body "        " $0 "\n" }
    '
done

count="$(find "$work" -name '*.cs' | wc -l)"
[[ "$count" -gt 0 ]] || { echo "readme-samples-check: no C# block found" >&2; exit 1; }

if ! dotnet build "$work/ReadmeSamples.csproj" -nologo -v q -consoleLoggerParameters:NoSummary > "$work/build.log" 2>&1; then
    grep -oE '[A-Za-z_]+_[0-9]+\.cs\([0-9,]+\): error [A-Z]+[0-9]+: [^[]*' "$work/build.log" | sort -u >&2 \
        || cat "$work/build.log" >&2
    echo "readme-samples-check: a C# sample does not compile (see the file and block above)" >&2
    exit 1
fi

echo "readme-samples-check: $count C# blocks compile"
