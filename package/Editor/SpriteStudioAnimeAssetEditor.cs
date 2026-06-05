using UnityEngine;
using UnityEditor;
using SpriteStudio.Runtime;
using System.IO;

namespace SpriteStudio.Editor
{
    [CustomEditor(typeof(SpriteStudioAnimeAsset))]
    public class SpriteStudioAnimeAssetEditor : UnityEditor.Editor
    {
        private PreviewRenderUtility _previewRenderUtility;
        private SpriteStudioInternalPlayer _internalPlayer;
        private Mesh _previewMesh;
        private double _lastTime;
        private int _currentAnimationIndex = 0;
        private bool _isPlaying = false; // 既定は一時停止 (フレーム0表示)。再生は Play ボタン。エフェクトはフレーム0で空になる点に注意
        private bool _isFirstFrame = true;

        private void OnEnable()
        {
            var asset = (SpriteStudioAnimeAsset)target;
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                _internalPlayer = new SpriteStudioInternalPlayer();
                // Instance パーツの子アニメを解決できるよう resolver を渡す (プレビューでも instance を表示)。
                _internalPlayer.LoadSsab(asset.SsabData, asset.ResolveExternalSsab);
                
                if (asset.AnimationNames != null && asset.AnimationNames.Length > 0)
                {
                    _internalPlayer.SetupAnimation(asset.AnimationNames[_currentAnimationIndex]);
                    _internalPlayer.Play();
                }

                _previewMesh = new Mesh();
                _previewMesh.name = "PreviewMesh";
                _previewMesh.MarkDynamic();
            }
            _lastTime = EditorApplication.timeSinceStartup;
        }

        private void OnDisable()
        {
            if (_internalPlayer != null)
            {
                _internalPlayer.Dispose();
                _internalPlayer = null;
            }
            if (_previewMesh != null)
            {
                DestroyImmediate(_previewMesh);
                _previewMesh = null;
            }
            if (_previewRenderUtility != null)
            {
                _previewRenderUtility.Cleanup();
                _previewRenderUtility = null;
            }
        }

        public override void OnInspectorGUI()
        {
            // Unity disables GUI.enabled for imported assets. We need to re-enable it for our custom buttons.
            bool wasEnabled = GUI.enabled;
            GUI.enabled = true;

            string sspjPath = SpriteStudioSourceMap.GetSspjForSsab(AssetDatabase.GetAssetPath(target));
            bool hasSspj = !string.IsNullOrEmpty(sspjPath) && File.Exists(sspjPath);

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Source SSPJ", EditorStyles.boldLabel);
            
            if (hasSspj)
            {
                GUILayout.Label(sspjPath, EditorStyles.wordWrappedMiniLabel);
                
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Open SSPJ"))
                {
                    Application.OpenURL("file://" + sspjPath);
                }
                
                if (GUILayout.Button("Reconvert"))
                {
                    SpriteStudioImporterWindow.RunConvertDirect(sspjPath);
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUIStyle warningStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
                warningStyle.normal.textColor = Color.yellow;
                GUILayout.Label("Source SSPJ not found in map or doesn't exist.", warningStyle);

                // リンク切れ修復: .sspj を選び直し、同じ出力フォルダの .ssab をまとめて再リンク+再変換
                if (GUILayout.Button("Relink SSPJ..."))
                {
                    SpriteStudioReconvertMenu.RelinkSsab(AssetDatabase.GetAssetPath(target));
                }
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space();

            if (GUILayout.Button("Instantiate in Scene", GUILayout.Height(30)))
            {
                InstantiatePlayer((SpriteStudioAnimeAsset)target, _currentAnimationIndex);
            }

            GUI.enabled = wasEnabled;

            EditorGUILayout.Space();
            base.OnInspectorGUI();
        }

        [MenuItem("Assets/SpriteStudio/Instantiate Player", false, 10)]
        public static void CreatePlayerFromContextMenu()
        {
            if (Selection.activeObject is SpriteStudioAnimeAsset asset)
            {
                InstantiatePlayer(asset, 0);
            }
        }

        [MenuItem("Assets/SpriteStudio/Instantiate Player", true)]
        public static bool ValidateCreatePlayerFromContextMenu()
        {
            return Selection.activeObject is SpriteStudioAnimeAsset;
        }

        private static void InstantiatePlayer(SpriteStudioAnimeAsset asset, int animationIndex)
        {
            var go = new GameObject(asset.name);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            if (asset.AnimationNames != null && asset.AnimationNames.Length > 0)
            {
                player.AnimationName = asset.AnimationNames[Mathf.Clamp(animationIndex, 0, asset.AnimationNames.Length - 1)];
            }
            
            if (asset.Materials != null && asset.Materials.Count > 0)
            {
                var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = asset.Materials.ToArray();
            }

            Undo.RegisterCreatedObjectUndo(go, "Create SpriteStudioPlayer");
            Selection.activeGameObject = go;
        }

        public override bool HasPreviewGUI()
        {
            return _internalPlayer != null;
        }

        public override GUIContent GetPreviewTitle()
        {
            return new GUIContent("Animation Preview");
        }

        public override void OnPreviewSettings()
        {
            var asset = (SpriteStudioAnimeAsset)target;
            if (asset == null || asset.AnimationNames == null || asset.AnimationNames.Length == 0) return;

            if (GUILayout.Button(_isPlaying ? "Pause" : "Play", EditorStyles.miniButton, GUILayout.Width(50)))
            {
                _isPlaying = !_isPlaying;
            }

            EditorGUI.BeginChangeCheck();
            _currentAnimationIndex = EditorGUILayout.Popup(_currentAnimationIndex, asset.AnimationNames, GUILayout.Width(150));
            if (EditorGUI.EndChangeCheck())
            {
                _internalPlayer.SetupAnimation(asset.AnimationNames[_currentAnimationIndex]);
                _internalPlayer.Play();
                _isPlaying = true;
            }
        }

        public override void OnInteractivePreviewGUI(Rect r, GUIStyle background)
        {
            if (Event.current.type != EventType.Repaint) return;

            if (_previewRenderUtility == null)
            {
                _previewRenderUtility = new PreviewRenderUtility();
                _previewRenderUtility.camera.orthographic = true;
                _previewRenderUtility.camera.nearClipPlane = -1000f;
                _previewRenderUtility.camera.farClipPlane = 1000f;
            }

            double currentTime = EditorApplication.timeSinceStartup;
            float dt = (float)(currentTime - _lastTime);
            _lastTime = currentTime;

            if (_isPlaying || _isFirstFrame)
            {
                _internalPlayer.Tick(_isPlaying ? dt : 0f, false);
                _internalPlayer.ScheduleMeshUpdate(_isPlaying ? dt : 0f);
                // テクスチャ単位にサブメッシュ分割し、下のループで hash からマテリアルを解決する。
                _internalPlayer.ApplyToMesh(_previewMesh, true);
                _isFirstFrame = false;
            }

            _previewRenderUtility.BeginPreview(r, background);

            // Frame the camera based on mesh bounds
            if (_previewMesh.bounds.size.magnitude > 0.1f)
            {
                float size = Mathf.Max(_previewMesh.bounds.extents.x, _previewMesh.bounds.extents.y);
                _previewRenderUtility.camera.orthographicSize = Mathf.Max(size * 1.2f, 10f); // Add 20% padding
                _previewRenderUtility.camera.transform.position = _previewMesh.bounds.center + new Vector3(0, 0, -10f);
            }
            else
            {
                _previewRenderUtility.camera.orthographicSize = 100f;
                _previewRenderUtility.camera.transform.position = new Vector3(0, 0, -10f);
            }

            var asset = (SpriteStudioAnimeAsset)target;
            // Materials が空でも Instance の子は外部参照側のマテリアルを使うため、guard で全スキップせず
            // submesh ごとに ResolveMaterialForHash (自身→外部参照) で解決する (シーンの UpdateSubmeshMaterials と同じ)。
            Material fallback = (asset.Materials != null && asset.Materials.Count > 0) ? asset.Materials[0] : null;
            for (int i = 0; i < _previewMesh.subMeshCount; i++)
            {
                // Resolve the per-(texture, blend) material (incl. external refs and the white base
                // for cell-less Shape submeshes), matching the scene player's UpdateSubmeshMaterials.
                uint hash = i < _internalPlayer.SubmeshCount ? _internalPlayer.GetSubmeshTextureHash(i) : 0u;
                byte blend = i < _internalPlayer.SubmeshCount ? _internalPlayer.GetSubmeshBlendType(i) : (byte)0;
                var m = asset.ResolveMaterial(hash, blend) ?? fallback;
                if (m != null)
                {
                    // submesh ごとに別 DrawMesh だと半透明の距離ソートで描画順が入れ替わる。
                    // submesh index 順 (= draw_order) に微小 Z オフセットを与え、奥→手前で正しく重ねる。
                    // (ortho なので Z は画面位置に影響せず、半透明ソートの距離だけを決める)
                    var xf = Matrix4x4.Translate(new Vector3(0f, 0f, -i * 0.1f));
                    _previewRenderUtility.DrawMesh(_previewMesh, xf, m, i);
                }
            }

            _previewRenderUtility.camera.Render();
            Texture resultRender = _previewRenderUtility.EndPreview();
            
            // Draw background grid (optional, but good for transparency)
            // Here we just use the background style provided by unity
            GUI.DrawTexture(r, resultRender, ScaleMode.StretchToFill, false);

            if (_isPlaying)
            {
                // Force continuous repaint so the animation plays smoothly
                Repaint();
            }
        }
    }
}
