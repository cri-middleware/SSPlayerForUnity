<!--
Thanks for contributing! See CONTRIBUTING.md for the constraints this project works under
(standard Unity packages only, zero-allocation tick loop, FFI lifetime rules).
CONTRIBUTING.md に本プロジェクトの制約が記載されています。
-->

## What does this change? / 変更内容

<!-- What it does and why. Link any related issue: Fixes #123 -->

## How was it verified? / 検証方法

<!--
Which tests ran, and on what. If you could not run them (no Unity, no SDK libs), say so —
that is useful information, not a problem.
実行したテストと環境。実行できなかった場合はその旨をお書きください。
-->

- [ ] EditMode tests
- [ ] PlayMode tests
- [ ] Tried it in a real project / 実プロジェクトで動作確認
- [ ] Not run — see notes below / 未実行（理由は下記）

**Environment / 環境:** Unity version, render pipeline (URP / BRP), platform

## Checklist

- [ ] Scoped to one thing. Unrelated fixes are easier to review as their own PR.
- [ ] Follows the surrounding code style.
- [ ] No new third-party dependencies (standard Unity packages only).
- [ ] The Tick / mesh-update path still allocates no GC memory.
- [ ] `JobHandle.Complete()` is called before any native resource is freed.
- [ ] Docs updated if behaviour changed — both `docs/en/` and `docs/ja/`.
- [ ] `CHANGELOG.md` updated if this is user-visible.

## Anything reviewers should know? / レビュー時の注意点

<!-- Trade-offs you made, alternatives you rejected, parts you are unsure about. -->
