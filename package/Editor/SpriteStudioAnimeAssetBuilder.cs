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
        // インポート時の詳細ログ (`[SS-IMPORT]`)。既定は静か。ssab / ssqb 双方の importer が参照する。
        // Window/SpriteStudio/Verbose Import Log でトグル (EditorPrefs 永続)。
        private const string VerboseLogPrefKey = "SpriteStudio.VerboseImportLog";
        internal static bool VerboseImportLog
        {
            get => EditorPrefs.GetBool(VerboseLogPrefKey, false);
            set => EditorPrefs.SetBool(VerboseLogPrefKey, value);
        }

        private const string VerboseLogMenu = "Window/SpriteStudio/Verbose Import Log";

        [MenuItem(VerboseLogMenu)]
        private static void ToggleVerboseImportLog() => VerboseImportLog = !VerboseImportLog;

        [MenuItem(VerboseLogMenu, true)]
        private static bool ToggleVerboseImportLogValidate()
        {
            Menu.SetChecked(VerboseLogMenu, VerboseImportLog);
            return true;
        }

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
        /// アセット名 (相対パス or ファイル名) をプロジェクト内の指定型アセットへ解決する共通ロジック。
        /// 相対ディレクトリ付き → ファイル名のみ の順でフォールバック探索し、見つかった実体を out で返す。
        /// onDependency が渡された場合、探索したパスを依存として通知する (importer 用)。
        /// 戻り値は最有力候補パス (未配置でも返る)。
        /// </summary>
        internal static string ResolveAssetPath<T>(string baseDir, string name, out T asset, Action<string> onDependency)
            where T : UnityEngine.Object
        {
            asset = null;
            if (string.IsNullOrEmpty(name)) return null;

            string path = Path.Combine(baseDir, name).Replace("\\", "/");
            onDependency?.Invoke(path);
            asset = AssetDatabase.LoadAssetAtPath<T>(path);

            if (asset == null)
            {
                string altPath = Path.Combine(baseDir, Path.GetFileName(name)).Replace("\\", "/");
                if (altPath != path)
                {
                    onDependency?.Invoke(altPath);
                    var alt = AssetDatabase.LoadAssetAtPath<T>(altPath);
                    if (alt != null)
                    {
                        path = altPath;
                        asset = alt;
                    }
                }
            }
            return path;
        }

        /// <summary>テクスチャ名 (image_path / external name) をプロジェクトパスへ解決する (<see cref="ResolveAssetPath{T}"/>)。</summary>
        internal static string ResolveTexturePath(string baseDir, string texName, out Texture2D tex, Action<string> onDependency)
            => ResolveAssetPath(baseDir, texName, out tex, onDependency);

        /// <summary>音声ファイル名 (SoundFile.file_path) をプロジェクト内の AudioClip へ解決する (<see cref="ResolveAssetPath{T}"/>)。</summary>
        internal static string ResolveAudioClipPath(string baseDir, string fileName, out AudioClip clip, Action<string> onDependency)
            => ResolveAssetPath(baseDir, fileName, out clip, onDependency);

        internal static string ResolveSsabPath(string baseDir, string animePackName, out SpriteStudioAnimeAsset asset, Action<string> onDependency)
        {
            asset = null;
            if (string.IsNullOrEmpty(animePackName)) return null;

            string filename = animePackName;
            if (!filename.EndsWith(".ssab", StringComparison.OrdinalIgnoreCase))
            {
                if (filename.EndsWith(".ssae", StringComparison.OrdinalIgnoreCase))
                {
                    filename = Path.ChangeExtension(filename, ".ssab");
                }
                else
                {
                    filename += ".ssab";
                }
            }

            string path = Path.Combine(baseDir, filename).Replace("\\", "/");
            onDependency?.Invoke(path);
            asset = AssetDatabase.LoadAssetAtPath<SpriteStudioAnimeAsset>(path);

            if (asset == null)
            {
                // 直下に無い場合のフォールバック: プロジェクト全体から同名 .ssab を探す。
                // FindAssets の名前検索は「部分一致 + 順序非保証」なので、そのまま guids[0] を採ると
                // 同名/類似名アセットがあるとマシンやインポート順で結果が変わる (非決定的インポート)。
                // (a) ファイル名が厳密一致するものだけに絞り、(b) パスでソートして決定的に選ぶ。
                // (c) 複数該当はグローバル状態依存の曖昧ケースなので警告する。
                string stem = Path.GetFileNameWithoutExtension(filename);
                string[] guids = AssetDatabase.FindAssets(stem + " t:SpriteStudioAnimeAsset");
                var matches = new List<string>();
                foreach (var g in guids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    if (string.Equals(Path.GetFileName(p), filename, StringComparison.OrdinalIgnoreCase))
                        matches.Add(p);
                }
                matches.Sort(StringComparer.Ordinal);
                if (matches.Count > 0)
                {
                    if (matches.Count > 1)
                    {
                        Debug.LogWarning($"[SSPlayer] Anime pack '{animePackName}' は複数候補に一致しました ({matches.Count} 件: {string.Join(", ", matches)})。決定的に '{matches[0]}' を使用します。参照元アセットの隣に .ssab を置くと曖昧さを避けられます。");
                    }
                    path = matches[0];
                    onDependency?.Invoke(path);
                    asset = AssetDatabase.LoadAssetAtPath<SpriteStudioAnimeAsset>(path);
                }
            }

            return path;
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
            // 入力検証: コンバータ出力以外 (拡張子だけ .ssab の別物など) を弾き、原因が分かるエラーにする。
            // 識別子は先頭オフセット(4)+マジック(4) を読むので最低 8 バイト必要。
            if (asset.SsabData.Length < 8 || !ss.format.SsAnimeBinary.SsAnimeBinaryBufferHasIdentifier(bb))
            {
                ctx.LogImportError($"[SpriteStudioSsabImporter] Not a valid .ssab (FlatBuffers 'SSAB' identifier missing): {ctx.assetPath}");
                ctx.AddObjectToAsset("MainAsset", asset);
                ctx.SetMainObject(asset);
                return;
            }
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
            string baseDir = Path.GetDirectoryName(ctx.assetPath);
            
            asset.PackHash = ssab.NameHash;

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

            // 5b. 音声 (SoundList/SoundFile) → AudioClip の解決と『口』への格納。
            //     ランタイム再生は未実装だが、再生時に必要な「アドレス用ハッシュ2つ + AudioClip 実体」
            //     だけを ScriptableObject に持たせる (名前/パス/再生長は SsabData にあるので重複させない)。
            //     AudioClip はテクスチャと同様にハードリファレンス + 依存登録してビルド落ち (ストリッピング)
            //     を防ぐ。解決できない音声はテクスチャ同様に警告してスキップする (= Sounds は再生可能分のみ)。
            int soundListLen = ssab.SoundListsLength;
            for (int li = 0; li < soundListLen; li++)
            {
                var sl = ssab.SoundLists(li);
                if (!sl.HasValue) continue;
                var soundList = sl.Value;

                int fileLen = soundList.TableDataLength;
                for (int fi = 0; fi < fileLen; fi++)
                {
                    var sf = soundList.TableData(fi);
                    if (!sf.HasValue) continue;
                    var soundFile = sf.Value;
                    if (string.IsNullOrEmpty(soundFile.FilePath)) continue;

                    ResolveAudioClipPath(baseDir, soundFile.FilePath, out var clip, ctx.DependsOnSourceAsset);
                    if (clip == null)
                    {
                        Debug.LogWarning($"[SpriteStudioSsabImporter] AudioClip not found for '{soundFile.FilePath}' (sound '{soundFile.Name}')");
                        continue;
                    }

                    asset.Sounds.Add(new SpriteStudioAnimeAsset.SoundClipEntry
                    {
                        SoundListNameHash = soundList.NameHash,
                        SoundNameHash = soundFile.NameHash,
                        Clip = clip,
                    });
                }
            }

            // 6. 外部アニメーション (Instance) への依存解決。
            //    external_instances ベクタはコンバータが省略しうる (実際 Ringo_tyu で空だった) ため、
            //    parts の PartTypeInstance を直接走査して別パック参照を集める (権威ある参照元)。
            //    外部アセットが更に参照する別パックも辿る (transitive)。
            {
                var seenAssets = new HashSet<SpriteStudioAnimeAsset>();
                var seenPacks = new HashSet<string>();
                CollectExternalReferences(asset.SsabData, baseDir, asset.PackHash, ctx, asset.ExternalReferences, seenAssets, seenPacks);
            }

            if (VerboseImportLog)
                Debug.Log($"[SS-IMPORT] {ctx.assetPath} -> textures:{asset.Textures.Count} materials:{asset.Materials.Count} extRefs:{asset.ExternalReferences.Count} sounds:{asset.Sounds.Count}");

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

        /// <summary>
        /// ssab が参照する別パック (Instance) を再帰的に集めて outRefs へ追加する。
        /// external_instances ベクタ (あれば) と parts の PartTypeInstance の両方から参照パック名を集め、
        /// ResolveSsabPath でプロジェクト内アセットへ解決する。external_instances はコンバータが
        /// 省略しうるため parts 走査が主経路。外部アセットが更に参照する別パックも辿る (transitive)。
        /// seenAssets / seenPacks で重複・循環を排除する。
        /// </summary>
        private static void CollectExternalReferences(byte[] ssabData, string baseDir, uint ownPackHash,
            AssetImportContext ctx, List<SpriteStudioAnimeAsset> outRefs,
            HashSet<SpriteStudioAnimeAsset> seenAssets, HashSet<string> seenPacks)
        {
            if (ssabData == null || ssabData.Length == 0) return;

            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            var ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);

            var packNames = new List<string>();

            int extLen = ssab.ExternalInstancesLength;
            for (int i = 0; i < extLen; i++)
            {
                var e = ssab.ExternalInstances(i);
                if (e.HasValue && !string.IsNullOrEmpty(e.Value.AnimePackName))
                    packNames.Add(e.Value.AnimePackName);
            }

            int partsLen = ssab.PartsLength;
            for (int i = 0; i < partsLen; i++)
            {
                var p = ssab.Parts(i);
                if (!p.HasValue || p.Value.PartTypeType != ss.format.PartType.PartTypeInstance) continue;
                var pt = p.Value.PartTypeAsPartTypeInstance();
                // 同一パック参照 (self) は実行時に self 解決するので外部参照に積まない。
                if (pt.RefAnimePackHash == ownPackHash) continue;
                if (!string.IsNullOrEmpty(pt.RefAnimePack)) packNames.Add(pt.RefAnimePack);
            }

            foreach (var packName in packNames)
            {
                if (string.IsNullOrEmpty(packName) || !seenPacks.Add(packName)) continue;
                ResolveSsabPath(baseDir, packName, out var extAsset, ctx.DependsOnSourceAsset);
                if (extAsset == null || !seenAssets.Add(extAsset)) continue;
                outRefs.Add(extAsset);

                // transitive: 外部アセットが更に参照する別パックも辿る (外部の baseDir で再帰)。
                string extDir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(extAsset));
                CollectExternalReferences(extAsset.SsabData, extDir, extAsset.PackHash, ctx, outRefs, seenAssets, seenPacks);
            }
        }
    }
}
