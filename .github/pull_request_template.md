## Summary

-

## Related

Closes #<issue-number>

- Design: docs/design.md
- Phase:

<!--
GitHub に Issue を関連付けるには Closing キーワードが必須。
- 有効: 本文中の単独行 `Closes #12`（推奨）/ `Fixes #12` / `Resolves #12`
- 無効になりやすい: 箇条書きだけ（`- Closes #12`）や URL のみ
PR 作成後、GitHub UI で Development / Linked issues に Issue が出ていることを確認すること。
-->

## Test plan

-

## Verification

- [ ] Windows で `./build.ps1` を実行した（GHA が使えない場合はこれが必須ゲート）

## Risk / Rollback

- Risk:
- Rollback: N/A

## Checklist

- [ ] Conventional Commits 形式のタイトル
- [ ] Related に単独行の `Closes #N`（または Fixes / Resolves）があり、GitHub 上で Issue が Linked になっている
- [ ] 設計契約を変える場合は設計 PR が先行、または本 PR がドキュメントのみの例外に該当
- [ ] 1 Issue ≈ 1 PR（基盤バッチ例外を除く）
- [ ] 公開 API を足したら XML ドキュメントがある（または N/A）
- [ ] 新しいテストに前提・手順・期待がある（または N/A）
- [ ] `src/` の日本語 XML コメントを足す・変えた場合、Gemini レビューを反映した（または N/A）
