---
name: nuget-package
description: >-
  Check and update the Txfio NuGet package against the NuGet authoring best practices, and pack it for publish.
  Use when editing package metadata, Txfio.csproj pack properties, README install steps,
  packing a nupkg, publishing to nuget.org, or when the user mentions NuGet, nuget.org, package authoring, or ContinuousIntegrationBuild.
---

# NuGet package

The guide is [Package authoring best practices](https://learn.microsoft.com/nuget/create-packages/package-authoring-best-practices). Source Link details are in [.NET library guidance: Source Link](https://learn.microsoft.com/dotnet/standard/library-guidance/sourcelink).

One packable project: `src/Txfio/Txfio.csproj`. `tests/Txfio.Tests`, `tests/Txfio.Stress`, and `tests/Txfio.TestSupport` stay `IsPackable` false.

## Repository choices

- `PackageId` is `Txfio`. Do not switch it to `Txfs`; that id is already taken (`docs/design.md`)
- `VersionPrefix` is SemVer. `0.x` may include breaking changes (`CONTRIBUTING.md`). Do not add a prerelease label unless the issue says the release is a preview. `0.1.0` is the first public version (`v0.1.0`)
- `Authors` is `YUKIKEDA`, the name on `LICENSE`. Do not invent a different display name
- `Copyright` is the copyright line in `LICENSE`, including the year. Change the two together
- License is `PackageLicenseExpression` `MIT`. Never `LicenseUrl`
- `PackageReadmeFile` is `README.md` only (`docs/language.md`). Update `README.ja.md` in the same change; it is not packed
- `PackageTags` in the project file are semicolon-separated
- When `VersionPrefix` changes, update `PackageReleaseNotes` and `CHANGELOG.md` in the same change. `CHANGELOG.md` has the full notes in one undated `## [{version}]` section (breaking changes first, each with how to move) and its link reference. `PackageReleaseNotes` is a short summary for the nuget.org page that starts with the version and links to `CHANGELOG.md`
- The publish-ready README installs with `dotnet add package Txfio`. Do not tell readers to clone the repo instead, and do not say the package is already listed on nuget.org until a push of that version has succeeded
- A version is published only by the `Release` workflow ([Publish](#publish)). Agents do not run it, and do not tag, create a Release, or push to nuget.org by hand

## Do

- Pack the SDK-style project with `dotnet pack`
- Keep a short `Description`
- Set `Copyright`, `PackageProjectUrl`, tags, release notes, and the readme
- Ship an SPDX license expression for this MIT package

## Consider

- Reserving the `Txfio` id prefix on nuget.org is a maintainer action on the site. Do not block packing on it, and do not try to reserve it from the repo
- Add `PackageIcon` only when a 128×128 transparent PNG is already in the repo. Do not generate an icon. Never set `IconUrl`
- Source Link is on: `Microsoft.SourceLink.GitHub` with `PrivateAssets` `All`, `PublishRepositoryUrl`, `EmbedUntrackedSources`, `IncludeSymbols`, and `SymbolPackageFormat` `snupkg`. The project sets `ContinuousIntegrationBuild` when `GITHUB_ACTIONS` is true. A local pack still passes the property, as in [Pack and check locally](#pack-and-check-locally)

## Avoid and don't

- Avoid a package reference that demands an exact version (`Version="[1.2.3]"`)
- Do not set `LicenseUrl` or `IconUrl`

## Publish

The `Release` workflow (`.github/workflows/release.yml`) is the only way a version reaches nuget.org. `CONTRIBUTING.md` "Releasing" has the steps and the one-time setup. In short, a human runs it from the Actions tab on `main` with the version, and it:

- checks the version, the release notes, the undated changelog section, and that neither the tag, the release branch, nor the nuget.org version exists (`.github/scripts/release-check.sh`)
- runs both jobs of `build.yml` on that commit, and a dry-run pack with its checks
- dates the changelog heading on a release commit (branch `release/v{version}`), tags it, packs and checks it (`.github/scripts/release-check-package.sh`), and pushes the branch and the tag
- pushes the nupkg (with its snupkg) using a short-lived key from nuget.org Trusted Publishing
- creates the GitHub Release from the changelog section (`.github/scripts/release-notes.sh`), and opens a pull request that brings the dated heading to `main`

No API key is stored anywhere. Ready to publish means a merged pull request has set `VersionPrefix`, `PackageReleaseNotes`, and the undated `CHANGELOG.md` section, and running `Release` is the only step left.

nuget.org does not overwrite or delete a version. Unlisting hides that version from search. An exact version restore still works. See [Deleting packages](https://learn.microsoft.com/nuget/nuget-org/policies/deleting-packages).

## Pack and check locally

To check package changes before they merge, pack as the workflow does:

```text
dotnet pack src/Txfio/Txfio.csproj -c Release -p:ContinuousIntegrationBuild=true
```

That property turns on deterministic source paths. The PDB Source Link map is `/_/*` to `https://raw.githubusercontent.com/YUKIKEDA/Txfio/<commit>/*`. Without it, the DLL and the PDB contain the local source path, such as `D:\home\...`.

The outputs are `src/Txfio/bin/Release/Txfio.<version>.nupkg` and `Txfio.<version>.snupkg` in the same directory. `.github/scripts/release-check-package.sh <version> <commit> src/Txfio/bin/Release` runs the automatic part of the checks below.

## Check the packed package

Read the `.nuspec` inside `src/Txfio/bin/Release/Txfio.<version>.nupkg` and confirm all of these:

- `description`, `authors`, `copyright`, `license type="expression"` `MIT`, `projectUrl`, `repository` with `type="git"`, a `commit` equal to `HEAD`, `tags`, `readme`, `releaseNotes`
- no `iconUrl`. `licenseUrl` may still point at `licenses.nuget.org` because pack writes that from the SPDX expression. That is not the deprecated `PackageLicenseUrl` property
- `README.md` is in the package
- The DLL and the PDB contain no drive letter path and no `C:\Users`. The PDB document map is `/_/*` to the raw GitHub URL of that commit
