#!/usr/bin/env bash
# Checks the packed nupkg and snupkg of VERSION before they are published:
# the version, the commit, the readme, and deterministic paths.
# Run from the repository root after the pack:
# .github/scripts/release-check-package.sh 0.2.0 <commit> src/Txfio/bin/Release
set -euo pipefail

version="${1:?usage: release-check-package.sh VERSION COMMIT DIR}"
commit="${2:?usage: release-check-package.sh VERSION COMMIT DIR}"
dir="${3:?usage: release-check-package.sh VERSION COMMIT DIR}"
nupkg="$dir/Txfio.$version.nupkg"
snupkg="$dir/Txfio.$version.snupkg"
fail() {
    echo "release-check-package: $*" >&2
    exit 1
}

[[ -f "$nupkg" ]] || fail "$nupkg is missing"
[[ -f "$snupkg" ]] || fail "$snupkg is missing"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
unzip -q "$nupkg" -d "$work/pkg"
unzip -q "$snupkg" -d "$work/sym"

nuspec="$work/pkg/Txfio.nuspec"
grep -qF "<version>$version</version>" "$nuspec" || fail "the nuspec version is not $version"
grep -qF "commit=\"$commit\"" "$nuspec" || fail "the nuspec repository commit is not $commit"
grep -qF "<readme>README.md</readme>" "$nuspec" || fail "the nuspec has no readme"
grep -qF "<releaseNotes>$version " "$nuspec" || fail "the nuspec release notes do not start with $version"
if grep -qF "<iconUrl>" "$nuspec"; then
    fail "the nuspec has an iconUrl"
fi
[[ -f "$work/pkg/README.md" ]] || fail "README.md is not in the package"

dll="$work/pkg/lib/net8.0/Txfio.dll"
pdb="$work/sym/lib/net8.0/Txfio.pdb"
[[ -f "$dll" ]] || fail "lib/net8.0/Txfio.dll is not in the package"
[[ -f "$pdb" ]] || fail "lib/net8.0/Txfio.pdb is not in the symbol package"

# ContinuousIntegrationBuild maps the source root to /_/. Without it, the DLL
# records the PDB under the local checkout path instead.
grep -aqF "/_/src/Txfio/obj/Release/net8.0/Txfio.pdb" "$dll" \
    || fail "the DLL does not record a deterministic /_/ PDB path (pack with -p:ContinuousIntegrationBuild=true)"
grep -aqF "https://raw.githubusercontent.com/YUKIKEDA/Txfio/$commit/" "$pdb" \
    || fail "the PDB Source Link does not point at commit $commit"

echo "release-check-package: Txfio $version is ready to publish"
