<!--
Thanks for contributing! See CONTRIBUTING.md for the constraints this project works under
(standard Unity packages only, zero-allocation tick loop, FFI lifetime rules).
CONTRIBUTING.md に本プロジェクトの制約が記載されています。日本語でも英語でも構いません。
-->

## What does this change? / 変更内容

<!-- What it does and why. Link any related issue: Fixes #123 -->

## How was it verified? / 検証方法

<!--
Which checks ran, and on what. If you could not run them, say so —
that is useful information, not a problem.
実行した確認と環境。実行できなかった場合はその旨をお書きください。
-->

- [ ] `./scripts/run-tests.sh` (EditMode, PlayMode, standalone player) — close the Editor first; a skip is not a pass
- [ ] Unity Test Runner in the Editor
- [ ] Tried it in a real project / 実プロジェクトで動作確認
- [ ] Not run — see notes below / 未実行（理由は下記）

**Environment / 環境:** Unity version, render pipeline (URP / BRP), platform

## Checklist

- [ ] Scoped to one thing. Unrelated fixes are easier to review as their own PR.
- [ ] Follows the style of the files it touches.
- [ ] Docs updated if behaviour changed.
- [ ] Anything that exists twice changed twice — a page and its translation, a `.sh` and its `.ps1`.
- [ ] No new third-party dependencies (standard Unity packages only).
- [ ] The Tick / mesh-update path still allocates no GC memory.
- [ ] `JobHandle.Complete()` is called before any native resource is freed.
- [ ] Generated code (FFI bindings, FlatBuffers accessors) regenerated with the scripts and committed, never hand-edited.

## Anything reviewers should know? / レビュー時の注意点

<!-- Trade-offs you made, alternatives you rejected, parts you are unsure about. -->
