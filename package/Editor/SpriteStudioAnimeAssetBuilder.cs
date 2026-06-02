using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;
using System;
using System.IO;
using System.Collections.Generic;
using SpriteStudio.Runtime;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// .ssab の FlatBuffers 解釈とテクスチャ解決の共通ロジック、および
    /// SpriteStudioSsabImporter 用の SpriteStudioAnimeAsset 構築処理。
    /// importer と SpriteStudioAssetPostprocessor で重複していた parse / パス解決 / 重複排除を
    /// ここへ集約する。
    ///
    /// マテリアル方針は従来どおり: テクスチャ単位の「共有 .mat」を使う (Spine-Unity 方式)。
    /// importer は隣接する共有 .mat があれば参照し、無ければ暫定の埋め込みマテリアルを使う。
    /// 共有 .mat の自動生成自体は SpriteStudioAssetPostprocessor が担当する。
    /// </summary>
    internal static class SpriteStudioAnimeAssetBuilder
    {
        /// <summary>解決済みのテクスチャ参照 (cellmap / external_texture)。</summary>
        internal struct TextureRef
        {
            public string TexName;    // ssab 内の元の名前
            public string TexPath;    // 解決済みプロジェクトパス (未配置なら最有力候補)
            public Texture2D Texture; // 未配置なら null
            public string MatPath;    // 共有マテリアルのパス (ChangeExtension(TexPath, ".mat"))
        }

        internal static Shader ResolveShader(Shader overrideShader)
        {
            if (overrideShader != null) return overrideShader;

            // Shader.Find はインポート順によって null を返すことがあるため、パスから直接ロードする
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.spritestudio.ssruntime/Runtime/Shaders/SpriteStudioDefault.shader");
            if (shader == null) shader = Shader.Find("SpriteStudio/Default");
            return shader;
        }

        /// <summary>
        /// テクスチャ名 (image_path / external name) をプロジェクトパスへ解決する。
        /// ファイル名のみ/相対ディレクトリ付きの両方をフォールバックで探索する。
        /// onDependency が渡された場合、探索したパスを依存として通知する (importer 用)。
        /// </summary>
        internal static string ResolveTexturePath(string baseDir, string texName, out Texture2D tex, Action<string> onDependency)
        {
            tex = null;
            if (string.IsNullOrEmpty(texName)) return null;

            string texPath = Path.Combine(baseDir, texName).Replace("\\", "/");
            onDependency?.Invoke(texPath);
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            if (tex == null)
            {
                string altPath = Path.Combine(baseDir, Path.GetFileName(texName)).Replace("\\", "/");
                if (altPath != texPath)
                {
                    onDependency?.Invoke(altPath);
                    var altTex = AssetDatabase.LoadAssetAtPath<Texture2D>(altPath);
                    if (altTex != null)
                    {
                        texPath = altPath;
                        tex = altTex;
                    }
                }
            }
            return texPath;
        }

        /// <summary>
        /// .ssab が参照する全テクスチャ (cellmaps + external_textures) を、
        /// 解決済みパスで重複排除して列挙する。
        /// onDependency が渡された場合、走査したテクスチャパスを依存として通知する (importer 用)。
        /// </summary>
        internal static List<TextureRef> EnumerateTextures(byte[] ssabData, string baseDir, Action<string> onDependency = null)
        {
            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            return CollectTextures(ssab, baseDir, onDependency);
        }

        private static List<TextureRef> CollectTextures(ss.format.SsAnimeBinary ssab, string baseDir, Action<string> onDependency)
        {
            var result = new List<TextureRef>();
            var seen = new HashSet<string>();

            // cellmaps: 通常パーツ等が参照する主テクスチャ
            int cellmapLen = ssab.CellmapsLength;
            for (int i = 0; i < cellmapLen; i++)
            {
                var cm = ssab.Cellmaps(i);
                if (cm.HasValue) TryAddTexture(result, seen, baseDir, cm.Value.ImagePath, onDependency);
            }

            // external_textures: TextureChange アトリビュート用の差し替えテクスチャ
            int extLen = ssab.ExternalTexturesLength;
            for (int i = 0; i < extLen; i++)
            {
                var extTex = ssab.ExternalTextures(i);
                if (extTex.HasValue) TryAddTexture(result, seen, baseDir, extTex.Value.Name, onDependency);
            }
            return result;
        }

        private static void TryAddTexture(List<TextureRef> result, HashSet<string> seen, string baseDir, string texName, Action<string> onDependency)
        {
            if (string.IsNullOrEmpty(texName)) return;

            string texPath = ResolveTexturePath(baseDir, texName, out var tex, onDependency);
            if (!seen.Add(texPath)) return; // 解決済みパスで重複排除

            result.Add(new TextureRef
            {
                TexName = texName,
                TexPath = texPath,
                Texture = tex,
                MatPath = Path.ChangeExtension(texPath, ".mat"),
            });
        }

        /// <summary>SpriteStudioSsabImporter から呼ばれ、SpriteStudioAnimeAsset を構築する。</summary>
        internal static void Build(SpriteStudioSsabImporter importer, AssetImportContext ctx)
        {
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();

            // 1. .ssab バイナリの保持 (ランタイムのメモリ供給元)
            asset.SsabData = File.ReadAllBytes(ctx.assetPath);

            // 2. パース
            var bb = new Google.FlatBuffers.ByteBuffer(asset.SsabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            string baseDir = Path.GetDirectoryName(ctx.assetPath);

            // 3. テクスチャ依存の登録とマテリアル割り当て (ビルド落ち・Addressables対策)
            var shader = ResolveShader(importer.OverrideShader);
            var textures = CollectTextures(ssab, baseDir, ctx.DependsOnSourceAsset);

            // 解決済みテクスチャパス → Materials のインデックス
            var pathToMaterialIndex = new Dictionary<string, int>();

            foreach (var t in textures)
            {
                if (t.Texture == null)
                {
                    Debug.LogWarning($"[SpriteStudioSsabImporter] Texture not found for '{t.TexName}' (tried '{t.TexPath}')");
                    continue;
                }

                asset.Textures.Add(t.Texture);

                if (!importer.GenerateMaterials) continue;

                // 共有 .mat があればそれを使う (postprocessor が自動生成する想定)。
                // .mat 生成時に再インポートされるよう依存登録する。
                ctx.DependsOnSourceAsset(t.MatPath);
                var externalMat = AssetDatabase.LoadAssetAtPath<Material>(t.MatPath);
                if (externalMat != null)
                {
                    asset.Materials.Add(externalMat);
                    pathToMaterialIndex[t.TexPath] = asset.Materials.Count - 1;
                }
                else if (shader != null)
                {
                    // 共有 .mat が生成されるまでの暫定埋め込みマテリアル
                    var mat = new Material(shader) { name = t.Texture.name + "_Material", mainTexture = t.Texture };
                    ctx.AddObjectToAsset("Mat_" + asset.Materials.Count, mat);
                    asset.Materials.Add(mat);
                    pathToMaterialIndex[t.TexPath] = asset.Materials.Count - 1;
                }
            }

            // 4. DrawBatch.TextureHash → Material インデックスの対応表を構築する。
            //    どのハッシュが実行時に来ても引けるよう、cellmap の NameHash/ImagePathHash と
            //    external の NameHash を全て (= 同じテクスチャの解決先マテリアル) に対応づける。
            if (importer.GenerateMaterials && pathToMaterialIndex.Count > 0)
            {
                var hashKeys = new List<uint>();
                var hashMaterialIndices = new List<int>();
                var hashSeen = new HashSet<uint>();

                int cellmapLen = ssab.CellmapsLength;
                for (int i = 0; i < cellmapLen; i++)
                {
                    var cm = ssab.Cellmaps(i);
                    if (!cm.HasValue) continue;
                    string path = ResolveTexturePath(baseDir, cm.Value.ImagePath, out _, null);
                    if (path != null && pathToMaterialIndex.TryGetValue(path, out int mi))
                    {
                        RecordHash(hashKeys, hashMaterialIndices, hashSeen, cm.Value.NameHash, mi);
                        RecordHash(hashKeys, hashMaterialIndices, hashSeen, cm.Value.ImagePathHash, mi);
                    }
                }

                int extLen = ssab.ExternalTexturesLength;
                for (int i = 0; i < extLen; i++)
                {
                    var extTex = ssab.ExternalTextures(i);
                    if (!extTex.HasValue) continue;
                    string path = ResolveTexturePath(baseDir, extTex.Value.Name, out _, null);
                    if (path != null && pathToMaterialIndex.TryGetValue(path, out int mi))
                    {
                        RecordHash(hashKeys, hashMaterialIndices, hashSeen, extTex.Value.NameHash, mi);
                    }
                }

                asset.TextureHashKeys = hashKeys.ToArray();
                asset.TextureHashMaterialIndices = hashMaterialIndices.ToArray();
            }

            // 5. アニメーション名の抽出
            int animLen = ssab.AnimationsLength;
            asset.AnimationNames = new string[animLen];
            for (int i = 0; i < animLen; i++)
            {
                var anim = ssab.Animations(i);
                if (anim.HasValue) asset.AnimationNames[i] = anim.Value.Name;
            }

            Debug.Log($"[SS-IMPORT] {ctx.assetPath} -> textures:{asset.Textures.Count} materials:{asset.Materials.Count} hashes:{(asset.TextureHashKeys != null ? asset.TextureHashKeys.Length : 0)}");

            ctx.AddObjectToAsset("MainAsset", asset);
            ctx.SetMainObject(asset);
        }

        /// <summary>ハッシュ→マテリアル対応を記録する。0 (未設定) や既出ハッシュは無視 (先勝ち)。</summary>
        private static void RecordHash(List<uint> keys, List<int> indices, HashSet<uint> seen, uint hash, int materialIndex)
        {
            if (hash == 0) return;
            if (!seen.Add(hash)) return;
            keys.Add(hash);
            indices.Add(materialIndex);
        }
    }
}
