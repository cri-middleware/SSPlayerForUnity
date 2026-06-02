using UnityEngine;
using UnityEditor;
using SpriteStudio.Runtime;

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
        private bool _isPlaying = true;

        private void OnEnable()
        {
            var asset = (SpriteStudioAnimeAsset)target;
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                _internalPlayer = new SpriteStudioInternalPlayer();
                _internalPlayer.LoadSsab(asset.SsabData);
                
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
            base.OnInspectorGUI();

            EditorGUILayout.Space();
            if (GUILayout.Button("Instantiate in Scene", GUILayout.Height(30)))
            {
                InstantiatePlayer((SpriteStudioAnimeAsset)target, _currentAnimationIndex);
            }
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

            if (_isPlaying)
            {
                _internalPlayer.Tick(dt, false);
                _internalPlayer.ScheduleMeshUpdate(dt);
                _internalPlayer.ApplyToMesh(_previewMesh);
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
            if (asset.Materials != null && asset.Materials.Count > 0)
            {
                for (int i = 0; i < _previewMesh.subMeshCount; i++)
                {
                    var m = i < asset.Materials.Count ? asset.Materials[i] : asset.Materials[0];
                    if (m != null)
                    {
                        _previewRenderUtility.DrawMesh(_previewMesh, Matrix4x4.identity, m, i);
                    }
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
