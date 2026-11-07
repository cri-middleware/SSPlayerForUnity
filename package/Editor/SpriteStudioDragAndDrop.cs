using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.EventSystems;
using UnityEditor.SceneManagement;
using SpriteStudio.Runtime;
using System.Collections.Generic;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Handles drag and drop of a .ssab asset into the Scene view or the Hierarchy view.
    /// It lets you choose whether to create a SpriteStudioPlayer or a SpriteStudioGraphic for a Canvas.
    /// It takes part in the Editor's drop-handler chain (DragAndDrop.AddDropHandlerV2), so a drag that carries no
    /// SpriteStudio asset goes on to the next handler, the Hierarchy says which row a drop lands on, and what is
    /// created goes into the stage being edited -- a prefab's contents in Prefab Mode -- as the GameObject menu
    /// places objects.
    /// </summary>
    [InitializeOnLoad]
    public static class SpriteStudioDragAndDrop
    {
        static SpriteStudioDragAndDrop()
        {
            DragAndDrop.AddDropHandlerV2(new DragAndDrop.SceneDropHandler(OnSceneDrop));
            DragAndDrop.AddDropHandlerV2(new DragAndDrop.HierarchyDropHandlerV2(OnHierarchyDrop));
        }

        private static DragAndDropVisualMode OnSceneDrop(Object dropUpon, Vector3 worldPosition, Vector2 viewportPosition,
            Transform parentForDraggedObjects, bool perform)
        {
            var assets = DraggedAnimeAssets();
            if (assets == null) return DragAndDropVisualMode.None;
            if (perform)
            {
                ShowInstantiateMenu(assets, parentForDraggedObjects != null ? parentForDraggedObjects.gameObject : null,
                    SceneDropPosition(worldPosition));
            }
            return DragAndDropVisualMode.Copy;
        }

        private static DragAndDropVisualMode OnHierarchyDrop(EntityId dropTargetEntityId, HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects, bool perform)
        {
            var assets = DraggedAnimeAssets();
            if (assets == null) return DragAndDropVisualMode.None;
            if (perform)
            {
                // In Prefab Mode the Hierarchy names the parent itself (and no row).
                GameObject parent = parentForDraggedObjects != null
                    ? parentForDraggedObjects.gameObject
                    : HierarchyDropParent(dropTargetEntityId, dropMode);
                ShowInstantiateMenu(assets, parent, null);
            }
            return DragAndDropVisualMode.Copy;
        }

        // The SpriteStudio assets being dragged, or null when there are none and the drop is another handler's.
        private static List<SpriteStudioAnimeAsset> DraggedAnimeAssets()
        {
            List<SpriteStudioAnimeAsset> assets = null;
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj is SpriteStudioAnimeAsset asset) (assets ??= new List<SpriteStudioAnimeAsset>()).Add(asset);
            }
            return assets;
        }

        // Dropped onto a row: under it. Dropped between rows: beside the row. Anywhere else -- a scene header,
        // the space below the rows -- at the root of the stage being edited.
        private static GameObject HierarchyDropParent(EntityId dropTargetEntityId, HierarchyDropFlags dropMode)
        {
            var target = EditorUtility.EntityIdToObject(dropTargetEntityId) as GameObject;
            if (target == null) return null;
            if ((dropMode & HierarchyDropFlags.DropUpon) != 0) return target;
            return target.transform.parent != null ? target.transform.parent.gameObject : null;
        }

        // The point under the mouse on the z = 0 plane the 2D animation lives on: straight down in the 2D view,
        // along the view ray in 3D, and where the Scene view would drop it when the ray misses the plane.
        private static Vector3 SceneDropPosition(Vector3 worldPosition)
        {
            if (Event.current == null) return worldPosition;
            Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            var sceneView = SceneView.currentDrawingSceneView != null ? SceneView.currentDrawingSceneView : SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.in2DMode)
            {
                Vector3 p = ray.origin;
                p.z = 0;
                return p;
            }
            return new Plane(Vector3.forward, Vector3.zero).Raycast(ray, out float enter) ? ray.GetPoint(enter) : worldPosition;
        }

        private static void ShowInstantiateMenu(List<SpriteStudioAnimeAsset> assets, GameObject parent, Vector3? spawnPosition)
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("SpriteStudioPlayer"), false, () => InstantiatePlayers(assets, parent, spawnPosition, false));
            menu.AddItem(new GUIContent("SpriteStudioGraphic (Canvas UI)"), false, () => InstantiatePlayers(assets, parent, spawnPosition, true));
            menu.ShowAsContext();
        }

        /// <summary>
        /// The animation a newly placed object starts on: the asset's first. Placement has to select one,
        /// because Play mode draws nothing for an empty animation name. Null when the asset lists none.
        /// </summary>
        private static string GetDefaultAnimationName(SpriteStudioAnimeAsset asset)
        {
            return (asset.AnimationNames != null && asset.AnimationNames.Length > 0)
                ? asset.AnimationNames[0]
                : null;
        }

        /// <summary>
        /// Creates a player (or, with <paramref name="isUI"/>, a graphic) for each asset, under
        /// <paramref name="parent"/> or at the root of the stage being edited, and selects them. Also what
        /// Assets &gt; SpriteStudio &gt; Instantiate Player and the asset Inspector's button use.
        /// </summary>
        internal static void InstantiatePlayers(List<SpriteStudioAnimeAsset> assets, GameObject parent, Vector3? spawnPosition, bool isUI)
        {
            var createdObjects = new List<GameObject>();

            foreach (var asset in assets)
            {
                GameObject go;
                if (isUI)
                {
                    go = new GameObject(asset.name, typeof(RectTransform));
                    Place(go, UIParent(parent));

                    var graphic = go.AddComponent<SpriteStudioGraphic>();
                    graphic.AnimeAsset = asset;
                    string uiAnimation = GetDefaultAnimationName(asset);
                    if (uiAnimation != null) graphic.CurrentAnimation = uiAnimation;
                    // The rect starts at the canvas's size, so the animation is drawn at the size it was made.
                    graphic.SetNativeSize();
                }
                else
                {
                    go = new GameObject(asset.name);
                    Place(go, parent);

                    var player = go.AddComponent<SpriteStudioPlayer>();
                    player.AnimeAsset = asset;
                    string worldAnimation = GetDefaultAnimationName(asset);
                    if (worldAnimation != null) player.CurrentAnimation = worldAnimation;

                    if (spawnPosition.HasValue) go.transform.position = spawnPosition.Value;
                }

                Undo.RegisterCreatedObjectUndo(go, "Create SpriteStudio Object");
                createdObjects.Add(go);
            }

            if (createdObjects.Count > 0)
            {
                Selection.objects = createdObjects.ToArray();
            }
        }

        // Under the parent, taking its layer as the GameObject menu does, or at the root of the stage being edited.
        private static void Place(GameObject go, GameObject parent)
        {
            if (parent != null) GameObjectUtility.SetParentAndAlign(go, parent);
            else StageUtility.PlaceGameObjectInCurrentStage(go);
            GameObjectUtility.EnsureUniqueNameForSibling(go);
        }

        // The parent a new graphic goes under, chosen as uGUI's GameObject > UI items choose it: the drop target
        // when it is inside a Canvas; with no target, a canvas in the stage being edited -- one inside the prefab
        // in Prefab Mode -- and otherwise a new Canvas, under the target when there is one.
        private static GameObject UIParent(GameObject parent)
        {
            if (parent == null)
            {
                parent = ExistingCanvas();
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                if (prefabStage != null && (parent == null || !prefabStage.IsPartOfPrefabContents(parent)))
                    parent = prefabStage.prefabContentsRoot;
            }
            if (parent == null || parent.GetComponentInParent<Canvas>(true) == null)
            {
                var canvas = CreateCanvas();
                if (parent != null) Undo.SetTransformParent(canvas.transform, parent.transform, "Create Canvas");
                parent = canvas;
            }
            return parent;
        }

        // The selection's canvas, or any active canvas in the stage being edited.
        private static GameObject ExistingCanvas()
        {
            var selected = Selection.activeGameObject;
            var canvas = selected != null ? selected.GetComponentInParent<Canvas>() : null;
            if (IsUsableCanvas(canvas)) return canvas.gameObject;
            foreach (var c in StageUtility.GetCurrentStageHandle().FindComponentsOfType<Canvas>())
            {
                if (IsUsableCanvas(c)) return c.gameObject;
            }
            return null;
        }

        private static bool IsUsableCanvas(Canvas canvas)
        {
            if (canvas == null || !canvas.gameObject.activeInHierarchy) return false;
            if (EditorUtility.IsPersistent(canvas) || (canvas.hideFlags & HideFlags.HideInHierarchy) != 0) return false;
            return StageUtility.GetStageHandle(canvas.gameObject) == StageUtility.GetCurrentStageHandle();
        }

        // A Screen Space - Overlay canvas on the UI layer, as GameObject > UI > Canvas makes one. Outside Prefab
        // Mode it brings an EventSystem when the scene has none, with the input module the project's input
        // handling calls for.
        private static GameObject CreateCanvas()
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.layer = LayerMask.NameToLayer("UI");
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            StageUtility.PlaceGameObjectInCurrentStage(canvasGo);
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                Undo.SetTransformParent(canvasGo.transform, prefabStage.prefabContentsRoot.transform, "Create Canvas");
            }
            else if (StageUtility.GetCurrentStageHandle().FindComponentOfType<EventSystem>() == null)
            {
                var eventSystemGo = new GameObject("EventSystem");
                StageUtility.PlaceGameObjectInCurrentStage(eventSystemGo);
                eventSystemGo.AddComponent<EventSystem>();
                InputModuleComponentFactory.AddInputModule(eventSystemGo);
                Undo.RegisterCreatedObjectUndo(eventSystemGo, "Create EventSystem");
            }
            return canvasGo;
        }
    }
}
