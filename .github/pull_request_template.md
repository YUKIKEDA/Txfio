## Summary

-

## Related

Closes #<issue-number>

- Design: docs/design.md
- Phase:

<!--
A closing keyword is required to link the Issue on GitHub.
- Works: `Closes #12` on its own line in the body (recommended) / `Fixes #12` / `Resolves #12`
- Often fails: only a bullet (`- Closes #12`) or only a URL
After opening the PR, check that the Issue appears under Development / Linked issues in the GitHub UI.
-->

## Test plan

-

## Verification

- [ ] Ran `./build.ps1` on Windows (the required gate while GitHub Actions cannot run)

## Risk / Rollback

- Risk:
- Rollback: N/A

## Checklist

- [ ] Title follows Conventional Commits
- [ ] `## Related` has `Closes #N` (or Fixes / Resolves) on its own line, and GitHub shows the Issue as linked
- [ ] A design PR came first if this changes a design contract, or this PR is the docs-only exception
- [ ] 1 Issue ≈ 1 PR (except the foundation batch exception)
- [ ] New public API has XML documentation (or N/A)
- [ ] New tests have Given / When / Then (or N/A)
- [ ] Title, body, comments, and docs are in English; changed bilingual documents have their `*.ja.md` updated in this PR (or a follow-up Issue is linked) (or N/A)
