using UnityEngine;
using UnityEditor;
using SpriteStudio.Runtime;
using System.Collections.Generic;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Handles drag and drop of a .ssab asset into the Scene view or the Hierarchy view.
    /// Like Spine-Unity, it lets you choose whether to create a SpriteStudioPlayer or a SpriteStudioGraphic for a Canvas.
    /// </summary>
    [InitializeOnLoad]
    public static class SpriteStudioDragAndDrop
    {
        static SpriteStudioDragAndDrop()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyGUI;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            Event e = Event.current;
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                if (HandleDragAndDrop(e, null, sceneView))
                {
                    e.Use();
                }
            }
        }

        private static void OnHierarchyGUI(int instanceID, Rect selectionRect)
        {
            Event e = Event.current;
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
var hoverTarget = EditorUtility.EntityIdToObject(instanceID) as GameObject;
                
                // If an item exists, check whether the mouse cursor is inside that item's Rect
                if (hoverTarget != null && !selectionRect.Contains(e.mousePosition))
                    return;

                // Handle drag and drop within the Hierarchy window
                if (HandleDragAndDrop(e, hoverTarget, null))
                {
                    e.Use();
                }
            }
        }

        private static bool HandleDragAndDrop(Event e, GameObject parent, SceneView sceneView)
        {
            if (DragAndDrop.objectReferences == null || DragAndDrop.objectReferences.Length == 0) 
                return false;

            var assets = new List<SpriteStudioAnimeAsset>();
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj is SpriteStudioAnimeAsset asset)
                {
                    assets.Add(asset);
                }
            }

            if (assets.Count == 0) 
                return false;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();

                Vector3 spawnPosition = Vector3.zero;
                bool hasPosition = false;

                if (sceneView != null)
                {
                    Vector2 mousePos = e.mousePosition;
                    Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
                    
                    if (sceneView.in2DMode)
                    {
                        spawnPosition = ray.origin;
                        spawnPosition.z = 0;
                        hasPosition = true;
                    }
                    else
                    {
                        // In 3D mode, find where the ray intersects the Z=0 plane
                        Plane plane = new Plane(Vector3.forward, Vector3.zero);
                        if (plane.Raycast(ray, out float enter))
                        {
                            spawnPosition = ray.GetPoint(enter);
                            hasPosition = true;
                        }
                        else
                        {
                            spawnPosition = sceneView.camera.transform.position + sceneView.camera.transform.forward * 10f;
                            hasPosition = true;
                        }
                    }
                }

                ShowInstantiateMenu(assets, parent, hasPosition ? spawnPosition : (Vector3?)null);
            }

            return true;
        }

        private static void ShowInstantiateMenu(List<SpriteStudioAnimeAsset> assets, GameObject parent, Vector3? spawnPosition)
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("SpriteStudioPlayer"), false, () => InstantiatePlayers(assets, parent, spawnPosition, false));
            menu.AddItem(new GUIContent("SpriteStudioGraphic (Canvas UI)"), false, () => InstantiatePlayers(assets, parent, spawnPosition, true));
            menu.ShowAsContext();
        }

        private static void InstantiatePlayers(List<SpriteStudioAnimeAsset> assets, GameObject parent, Vector3? spawnPosition, bool isUI)
        {
            var createdObjects = new List<GameObject>();

            foreach (var asset in assets)
            {
                // Generate a unique name (e.g. "Asset", "Asset (1)", "Asset (2)")
                string uniqueName = GameObjectUtility.GetUniqueNameForSibling(parent != null ? parent.transform : null, asset.name);
                GameObject go = new GameObject(uniqueName);
                
                if (parent != null)
                {
                    go.transform.SetParent(parent.transform, false);
                }

                if (isUI)
                {
                    // For UI, if the parent isn't under a Canvas, find a Canvas and parent to it
                    if (parent == null || parent.GetComponentInParent<Canvas>() == null)
                    {
Canvas canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
                        if (canvas != null)
                        {
                            go.transform.SetParent(canvas.transform, false);
                            go.name = GameObjectUtility.GetUniqueNameForSibling(canvas.transform, asset.name);
                        }
                        else
                        {
                            // If no Canvas exists, create a new one
                            var canvasGo = new GameObject("Canvas");
                            canvas = canvasGo.AddComponent<Canvas>();
                            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                            canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
                            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                            Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");
                            
                            go.transform.SetParent(canvas.transform, false);
                            go.name = GameObjectUtility.GetUniqueNameForSibling(canvas.transform, asset.name);
                            
                            if (go.GetComponent<UnityEngine.EventSystems.EventSystem>() == null)
                            {
var eventSystem = UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>();
                                if (eventSystem == null)
                                {
                                    var esGo = new GameObject("EventSystem");
                                    esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                                    esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                                    Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
                                }
                            }
                        }
                    }

                    var graphic = go.AddComponent<SpriteStudioGraphic>();
                    graphic.AnimeAsset = asset;
                    
                    var rectTransform = go.GetComponent<RectTransform>();
                    if (spawnPosition.HasValue && parent == null)
                    {
                        // For a drop into the Scene view with no parent, place it at a sensible spot such as the center.
                        // Canvas scaling makes an exact screen position ambiguous, so anchor it at the parent's origin.
                        rectTransform.anchoredPosition = Vector2.zero;
                    }
                }
                else
                {
                    // Regular SpriteStudioPlayer
                    var player = go.AddComponent<SpriteStudioPlayer>();
                    player.AnimeAsset = asset;
                    
                    if (spawnPosition.HasValue && parent == null)
                    {
                        go.transform.position = spawnPosition.Value;
                    }
                }

                Undo.RegisterCreatedObjectUndo(go, "Create SpriteStudio Object");
                createdObjects.Add(go);
            }

            if (createdObjects.Count > 0)
            {
                Selection.objects = createdObjects.ToArray();
            }
        }
    }
}
