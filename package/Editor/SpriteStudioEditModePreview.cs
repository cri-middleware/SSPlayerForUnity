using System;
using System.Collections.Generic;
using SpriteStudio.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SpriteStudio.Editor
{
    /// <summary>
    /// Plays a player or a graphic in Edit Mode from its inspector. The state lives here, not in the component,
    /// so nothing is written to the scene. The start frame is what a save or Play mode sees: a part tracker's
    /// target and the renderer's materials follow the frame shown, and both are serialized.
    /// It ends when the selection leaves the object, not when the inspector closes: the Inspector rebuilds its
    /// editors whenever the renderer's material list changes, which a frame with another batch layout does.
    /// </summary>
    internal static class SpriteStudioEditModePreview
    {
        private sealed class State
        {
            public bool Playing;
            // The frame shown when Playing last changed; while paused, the frame shown.
            public float Frame;
            public double Since;
            // NaN asks the next tick to draw, playing or not.
            public float Shown = float.NaN;
        }

        private static readonly Dictionary<Object, State> _states = new Dictionary<Object, State>();
        private static bool _hooked;
        private static double _lastTick;
        private const double TickInterval = 1.0 / 60.0;

        internal static bool IsPlaying(Object target) => target != null && _states.TryGetValue(target, out var s) && s.Playing;

        internal static bool TryGetSection(Object target, out int start, out int end, out int fps)
        {
            start = end = 0;
            fps = 60;
            return target != null && target is ISpriteStudioEditPreview p && p.TryGetEditPreviewSection(out start, out end, out fps);
        }

        internal static float CurrentFrame(Object target)
        {
            if (!TryGetSection(target, out int start, out _, out _)) return 0f;
            if (!_states.TryGetValue(target, out var s)) return start;
            return Compute((ISpriteStudioEditPreview)target, s, out float frame) ? frame : start;
        }

        internal static void Play(Object target)
        {
            if (!TryGetSection(target, out int start, out _, out _)) return;
            var s = GetState(target, start);
            if (s.Playing) return;
            s.Playing = true;
            s.Since = EditorApplication.timeSinceStartup;
            Hook();
        }

        internal static void Pause(Object target)
        {
            if (target == null || !_states.TryGetValue(target, out var s) || !s.Playing) return;
            s.Frame = CurrentFrame(target);
            s.Playing = false;
            s.Shown = float.NaN;
            if (Draw((ISpriteStudioEditPreview)target, s)) RepaintViews();
        }

        /// <summary>Pauses on <paramref name="frame"/> and shows it now.</summary>
        internal static void Seek(Object target, float frame)
        {
            if (!TryGetSection(target, out int start, out int end, out _)) return;
            var s = GetState(target, start);
            s.Playing = false;
            s.Frame = Mathf.Clamp(frame, start, end);
            Draw((ISpriteStudioEditPreview)target, s);
            Hook();
            RepaintViews();
        }

        /// <summary>Ends the preview: back to the start frame.</summary>
        internal static void Stop(Object target)
        {
            if (target == null || !_states.Remove(target)) return;
            var p = (ISpriteStudioEditPreview)target;
            if (p.TryGetEditPreviewSection(out int start, out _, out _)) p.ShowEditPreviewFrame(start);
            p.EndEditPreview();
            RepaintViews();
            if (_states.Count == 0) Unhook();
        }

        internal static void StopAll()
        {
            foreach (var target in new List<Object>(_states.Keys)) Stop(target);
            _states.Clear();
            Unhook();
        }

        private static State GetState(Object target, float start)
        {
            if (!_states.TryGetValue(target, out var s))
            {
                s = new State { Frame = start };
                _states.Add(target, s);
            }
            return s;
        }

        private static bool Compute(ISpriteStudioEditPreview p, State s, out float frame)
        {
            frame = 0f;
            if (!p.TryGetEditPreviewSection(out int start, out int end, out int fps)) return false;
            double elapsed = s.Playing ? EditorApplication.timeSinceStartup - s.Since : 0.0;
            frame = SpriteStudioEditPreviewClock.FrameAt(start, end, fps, p.EditPreviewSpeedScale, s.Frame, elapsed,
                p.EditPreviewSubFrame);
            return true;
        }

        private static bool Draw(ISpriteStudioEditPreview p, State s)
        {
            if (p.EditPreviewBlocked || !Compute(p, s, out float frame)) return false;
            if (frame == s.Shown) return false;
            p.ShowEditPreviewFrame(frame);
            s.Shown = frame;
            return true;
        }

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastTick < TickInterval) return;
            _lastTick = now;

            bool drew = false;
            List<Object> gone = null;
            foreach (var entry in _states)
            {
                if (entry.Key == null)
                {
                    (gone ??= new List<Object>()).Add(entry.Key);
                    continue;
                }
                var s = entry.Value;
                if (!s.Playing && !float.IsNaN(s.Shown)) continue;
                if (Draw((ISpriteStudioEditPreview)entry.Key, s)) drew = true;
            }
            if (gone != null)
            {
                foreach (var target in gone) _states.Remove(target);
            }
            if (drew) RepaintViews();
            if (_states.Count == 0) Unhook();
        }

        private static void ShowStartFrames(Func<Component, bool> inScope)
        {
            foreach (var entry in _states)
            {
                if (!(entry.Key is Component c) || c == null || !inScope(c)) continue;
                var p = (ISpriteStudioEditPreview)c;
                if (!p.TryGetEditPreviewSection(out int start, out _, out _)) continue;
                p.ShowEditPreviewFrame(start);
                entry.Value.Shown = float.NaN;
            }
        }

        private static void OnSceneSaving(Scene scene, string path) => ShowStartFrames(c => c.gameObject.scene == scene);

        private static void OnPrefabSaving(GameObject root) => ShowStartFrames(c => c.transform.IsChildOf(root.transform));

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) StopAll();
        }

        private static void OnSelectionChanged()
        {
            foreach (var target in new List<Object>(_states.Keys))
            {
                if (target is Component c && c != null && Selection.Contains(c.gameObject)) continue;
                Stop(target);
                _states.Remove(target);
            }
            if (_states.Count == 0) Unhook();
        }

        private static void RepaintViews()
        {
            SceneView.RepaintAll();
            // Runs LateUpdate, which is where a graphic rebuilds its mesh, and repaints the Game view.
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            EditorApplication.update += Tick;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            PrefabStage.prefabSaving += OnPrefabSaving;
        }

        private static void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            EditorApplication.update -= Tick;
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= StopAll;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            PrefabStage.prefabSaving -= OnPrefabSaving;
        }
    }

    internal static class SpriteStudioEditModePreviewGUI
    {
        private static readonly GUIContent _header = new GUIContent("Play in Edit Mode");
        private static readonly GUIContent _play = new GUIContent("Play", "Nothing is saved to the scene.");
        private static readonly GUIContent _pause = new GUIContent("Pause");
        private static readonly GUIContent _stop = new GUIContent("Stop", "Back to the start frame.");
        private static readonly GUIContent _frame = new GUIContent("Frame");

        internal static void Draw(UnityEditor.Editor editor)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var targets = editor.targets;

            bool ready = true;
            bool blocked = false;
            bool playing = false;
            foreach (var target in targets)
            {
                if (!SpriteStudioEditModePreview.TryGetSection(target, out _, out _, out _)) ready = false;
                if (target is ISpriteStudioEditPreview p && p.EditPreviewBlocked) blocked = true;
                if (SpriteStudioEditModePreview.IsPlaying(target)) playing = true;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_header, EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!ready || blocked))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(playing ? _pause : _play))
                    {
                        foreach (var target in targets)
                        {
                            if (playing) SpriteStudioEditModePreview.Pause(target);
                            else SpriteStudioEditModePreview.Play(target);
                        }
                    }
                    if (GUILayout.Button(_stop))
                    {
                        foreach (var target in targets) SpriteStudioEditModePreview.Stop(target);
                    }
                }
                if (targets.Length == 1 && SpriteStudioEditModePreview.TryGetSection(targets[0], out int start, out int end, out _))
                {
                    EditorGUI.BeginChangeCheck();
                    float frame = EditorGUILayout.Slider(_frame, SpriteStudioEditModePreview.CurrentFrame(targets[0]), start, end);
                    if (EditorGUI.EndChangeCheck()) SpriteStudioEditModePreview.Seek(targets[0], frame);
                }
            }
            if (blocked)
            {
                EditorGUILayout.HelpBox("A Timeline is driving this player. Scrub the Timeline window instead.", MessageType.Info);
            }
            if (playing) editor.Repaint();
        }
    }
}
