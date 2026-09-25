# Security Policy

## Supported Versions

SSPlayerForUnity is in beta. Fixes are issued against the latest release only — if you are on an
older one, please reproduce on the current release before reporting.

| Version | Supported |
|---|---|
| Latest `7.0.x` release | ✅ |
| Anything older | ❌ |

## Reporting a Vulnerability

**Please do not open a public GitHub Issue for a security problem.** Report it privately through the
official inquiry form:

👉 [**CRI Middleware Inquiry Form (English)**](https://www.webtech.co.jp/contact/en.html)

The English form has no file upload. If you need to send a reproduction, use the [Japanese Help
Center form](https://www.webtech.co.jp/help/ja/spritestudio7/inquiries/ssplayer_tool/), which takes a zip of up to 8 MB.

Please include:

- What the impact is — crash, out-of-bounds read, arbitrary code execution, information disclosure.
- Steps to reproduce, ideally with a minimal `.ssab` / `.sspj` and the smallest amount of C# needed.
- The player version, the SpriteStudio SDK version (`scripts/SDK_VERSION.txt`), your Unity version,
  and the target platform.
- Whether the input that triggers it comes from a trusted source in your project or from somewhere
  untrusted (downloaded content, user-supplied files).

We will confirm receipt, tell you whether we can reproduce it, and let you know when a fix ships.
Please give us a chance to release that fix before disclosing publicly.

### Which repository to report to

Playback is driven by a native Rust runtime shared with the other SpriteStudio players. If the same
problem reproduces on a different SpriteStudio player, it is most likely in that shared core, which
lives in SpriteStudio-SDK. Report it here all the same and say so; we will route it.

## Scope Notes

The player loads binary animation data (`.ssab`, `.ssqb`) and hands it to native code. Treat these as
you would any other binary asset:

- **`.ssab` validation is currently partial.** `LoadSsab` checks the `"SSAB"` file identifier before
  handing the buffer to the native runtime; it does not yet fully verify the buffer's internal
  structure. A malformed or deliberately crafted `.ssab` may therefore cause an out-of-bounds read in
  native code rather than a clean error. Restoring full structural verification is tracked in
  [ROADMAP.md](./ROADMAP.md) and is blocked on an upstream FlatBuffers fix.
- Because of the above, **loading `.ssab` from an untrusted source is not currently a supported
  configuration.** If your game downloads animation data at runtime, treat it as trusted content:
  serve it over a channel you control and verify its integrity yourself.

Reports about the above known gap are welcome, but it is already tracked — a report that
demonstrates impact beyond it is more useful.

---

# セキュリティポリシー

## サポート対象バージョン

SSPlayerForUnity は beta 版です。修正は最新リリースに対してのみ提供されます。古いバージョンをご利用の場合は、最新リリースで再現することをご確認のうえご報告ください。

| バージョン | サポート |
|---|---|
| 最新の `7.0.x` リリース | ✅ |
| それ以前 | ❌ |

## 脆弱性の報告

**セキュリティに関する問題は、公開の GitHub Issue に投稿しないでください。** 再現データの添付も可能なヘルプセンターより、非公開でご報告ください。

👉 [**ヘルプセンター（お問い合わせフォーム）**](https://www.webtech.co.jp/help/ja/spritestudio7/inquiries/ssplayer_tool/)

ご報告には以下を含めてください。

- 影響の内容（クラッシュ、境界外読み取り、任意コード実行、情報漏洩など）
- 再現手順。可能であれば最小限の `.ssab` / `.sspj` と、必要最小限の C# コード
- プレイヤーのバージョン、SpriteStudio SDK のバージョン（`scripts/SDK_VERSION.txt`）、Unity のバージョン、対象プラットフォーム
- 引き金となる入力が、プロジェクト内の信頼できるデータか、信頼できない経路（ダウンロードコンテンツ、ユーザーが用意したファイル）由来か

受領の確認、再現可否、修正版の公開時期についてご連絡します。修正を公開するまでの間、公表をお待ちいただけますようお願いします。

### 報告先リポジトリの判断

再生処理は、他の SpriteStudio Player と共通のネイティブ Rust ランタイムが担っています。他の
SpriteStudio Player でも同じ問題が再現する場合、SpriteStudio-SDK にある共通コア側の問題である可能性が高いため、その旨を添えて本リポジトリへご報告ください。こちらで振り分けます。

## 既知の注意点

本プレイヤーはバイナリのアニメーションデータ（`.ssab` / `.ssqb`）を読み込み、ネイティブコードへ渡します。他のバイナリアセットと同様にお取り扱いください。

- **`.ssab` の検証は現時点で部分的です。** `LoadSsab` はネイティブランタイムへ渡す前に `"SSAB"` のファイル識別子のみを確認しており、バッファ内部の構造は完全には検証していません。そのため、破損した、あるいは意図的に細工された `.ssab` は、エラーとして弾かれずにネイティブコードでの境界外読み取りを引き起こす可能性があります。完全な構造検証の復旧は [ROADMAP.md](./ROADMAP.md) で管理しており、
  FlatBuffers 側の修正待ちです。
- 上記の理由により、**信頼できない入手元の `.ssab` の読み込みは現時点でサポート対象外**です。実行時にアニメーションデータをダウンロードする構成では、信頼できるコンテンツとして扱ってください（自社管理の経路で配信し、完全性の検証はアプリ側で行ってください）。

上記の既知の課題に関するご報告も歓迎しますが、すでに把握している内容です。これを超える影響を示すご報告のほうが有用です。
