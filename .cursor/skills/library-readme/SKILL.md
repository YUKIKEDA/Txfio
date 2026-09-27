---
name: library-readme
description: Write a library README in the style of well-known .NET libraries. Use when writing or rewriting README.md (and README.ja.md) for a library, or when the user mentions how to write a README, user-facing explanations, or getting started.
---

# Library README

Write only behavior that has shipped. If there is a separate source of truth for the contract, the README is for deciding whether to use the library; do not describe unimplemented APIs. This repository's README is in English (`README.md`), with a Japanese translation (`README.ja.md`) that has the same sections in the same order ([`docs/language.md`](../../../docs/language.md)). Change both in the same PR.

READMEs used as references: FluentValidation (the one-line summary of strongly-typed validation), R3, Polly, Spectre.Console, Humanizer, Dapper, Refit, Serilog, FluentAssertions, BenchmarkDotNet.

## Structure

In this order. Do not leave empty sections.

1. **One sentence**. What the library does. It may include who it is for. Do not mention implementation names yet (temporary file names, internal folders)
2. **The smallest code**. If there is no install step, one sentence on how to reference it. The code can be copied and pasted, and shows only the shortest success path in that language
3. **What happens in that code**. Two to four sentences. The meaning of the return value may be a table
4. **What it cannot do**. Before the feature list. Rule out the failures readers are likely to assume away
5. **List of operations**. A verb and one line each. No overloads. Details go in later sections or the source of truth
6. **An example that shows what is special**. One piece of code that shows behavior the success path does not
7. **How each operation works**. After the list, describe steps a user can follow without reading the code. This repository has no separate user guide. Draw a diagram when the flow branches. Do not repeat the same fact in a table and in prose
8. **Comparison**. Only when there really are alternatives the reader already knows. First write "when it is better / when it is worse", and keep the table's criteria consistent
9. **Links to the source of truth**, build, license. Leave the contribution steps to CONTRIBUTING; do not copy them into the README

## Code examples

- Aim for 15 lines or fewer in the first example
- The second example shows only what is hard to see without this library
- Exception names and messages must match the strings in the implementation
- Use only sample paths and API names that exist

## Do not

- Put badges, sponsors, or the code of conduct at the top
- Put `dotnet add package` in the README before that version's package metadata is ready to publish. The publish-ready install line is `dotnet add package Txfio` ([nuget-package](../nuget-package/SKILL.md))
- Add behavior that is not in the source of truth (the design)
- Make process progress (Issue numbers, phases) the center of the text. Mention it briefly only when the publishing state changes how users get the library
