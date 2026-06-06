using System.Runtime.CompilerServices;

// テスト (ホワイトボックス) から internal な UpdateManager / ISpriteStudioRenderable 等へ
// アクセスできるようにする。文書化済み不変条件 (tick 中の add/remove、ゼロアロケーション、
// domain-reload リセット等) を直接検証するために必要。Unity のアセンブリは既定で
// strong-name されないため public key は不要。
[assembly: InternalsVisibleTo("SpriteStudio.Runtime.Tests")]
[assembly: InternalsVisibleTo("SpriteStudio.Runtime.EditModeTests")]
