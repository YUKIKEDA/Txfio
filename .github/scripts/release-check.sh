#!/usr/bin/env bash
# Checks that the tree is ready to publish VERSION: the project version, the
# release notes, an undated changelog section, no existing tag or release
# branch, and no existing nuget.org version.
# Run from the repository root: .github/scripts/release-check.sh 0.2.0
set -euo pipefail

version="${1:?usage: release-check.sh VERSION}"
project=src/Txfio/Txfio.csproj
fail() {
    echo "release-check: $*" >&2
    exit 1
}

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "version '$version' is not MAJOR.MINOR.PATCH"

project_version="$(dotnet msbuild "$project" -getProperty:Version | tr -d '\r')"
[[ "$project_version" == "$version" ]] || fail "$project Version is $project_version, not $version"

# The property starts with the line break after the XML tag; drop leading blank lines.
notes="$(dotnet msbuild "$project" -getProperty:PackageReleaseNotes | tr -d '\r' | sed -e '/[^[:space:]]/,$!d')"
[[ "$notes" == "$version "* ]] || fail "PackageReleaseNotes must start with '$version ' (update it with VersionPrefix)"

heading="## [$version]"
count="$(tr -d '\r' < CHANGELOG.md | grep -cxF "$heading" || true)"
[[ "$count" == 1 ]] || fail "CHANGELOG.md needs exactly one undated '$heading' line (found $count)"
if grep -qF "## [$version] - " CHANGELOG.md; then
    fail "CHANGELOG.md already dates $version"
fi
grep -qF "[$version]: https://github.com/YUKIKEDA/Txfio/" CHANGELOG.md \
    || fail "CHANGELOG.md has no link reference for [$version]"

if git rev-parse -q --verify "refs/tags/v$version" >/dev/null \
    || git ls-remote --exit-code --tags origin "refs/tags/v$version" >/dev/null 2>&1; then
    fail "tag v$version already exists"
fi
if git ls-remote --exit-code --heads origin "release/v$version" >/dev/null 2>&1; then
    fail "branch release/v$version already exists"
fi

published="$(curl -fsS https://api.nuget.org/v3-flatcontainer/txfio/index.json)"
if grep -qF "\"$version\"" <<<"$published"; then
    fail "nuget.org already has Txfio $version"
fi

echo "release-check: $version is ready"
