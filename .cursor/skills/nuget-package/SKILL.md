---
name: nuget-package
description: >-
  Check and update the Txfio NuGet package against the NuGet authoring best practices.
  Use when editing package metadata, Txfio.csproj pack properties, README install steps,
  packing a nupkg, publishing to nuget.org, or when the user mentions NuGet, nuget.org, or package authoring.
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
- When `VersionPrefix` changes, update `PackageReleaseNotes` in the same change. State breaking changes, features, and fixes, or link to the place that does
- The publish-ready README installs with `dotnet add package Txfio`. Do not tell readers to clone the repo instead, and do not say the package is already listed on nuget.org until a push of that version has succeeded
- Do not push to nuget.org unless the user supplies an API key and asks

## Do

- Pack the SDK-style project with `dotnet pack`
- Keep a short `Description`
- Set `Copyright`, `PackageProjectUrl`, tags, release notes, and the readme
- Ship an SPDX license expression for this MIT package

## Consider

- Reserving the `Txfio` id prefix on nuget.org is a maintainer action on the site. Do not block packing on it, and do not try to reserve it from the repo
- Add `PackageIcon` only when a 128×128 transparent PNG is already in the repo. Do not generate an icon. Never set `IconUrl`
- Source Link is on: `Microsoft.SourceLink.GitHub` with `PrivateAssets` `All`, `PublishRepositoryUrl`, `EmbedUntrackedSources`, `IncludeSymbols`, and `SymbolPackageFormat` `snupkg`. `ContinuousIntegrationBuild` is set when `GITHUB_ACTIONS` is true

## Avoid and don't

- Avoid a package reference that demands an exact version (`Version="[1.2.3]"`)
- Do not set `LicenseUrl` or `IconUrl`

## Check the packed package

After `dotnet pack src/Txfio/Txfio.csproj -c Release`, read the `.nuspec` inside `src/Txfio/bin/Release/Txfio.<version>.nupkg` and confirm all of these:

- `description`, `authors`, `copyright`, `license type="expression"` `MIT`, `projectUrl`, `repository` with `type="git"`, a `commit`, `tags`, `readme`, `releaseNotes`
- no `iconUrl`. `licenseUrl` may still point at `licenses.nuget.org` because pack writes that from the SPDX expression. That is not the deprecated `PackageLicenseUrl` property
- `README.md` is in the package
