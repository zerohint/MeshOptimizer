using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    /// <summary>
    /// Interactive, prefab-by-prefab mesh optimizer.
    ///
    /// Pick a folder of prefabs and step through them: each prefab shows its LOD setup, every
    /// mesh it uses with stats, a live original/optimized preview, and the full simplifier
    /// option set. Apply writes the meshes and re-points the prefab, then moves to the next.
    ///
    /// Split into partial-class files by concern: this file holds persisted/runtime state and
    /// the window lifecycle; see PrefabMeshOptimizerWindow.*.cs for the toolbar/queue UI, mesh
    /// list UI, preview, settings panel, actions and small helpers.
    /// </summary>
    public partial class PrefabMeshOptimizerWindow : EditorWindow
    {
        // ---------------------------------------------------------------- menus

        [MenuItem("Window/Asset Management/Mesh Optimizer", false, 40)]
        public static void Open()
        {
            var w = GetWindow<PrefabMeshOptimizerWindow>();
            w.ApplyTitle();
            w.minSize = new Vector2(760f, 640f);
        }

        /// <summary>Right-click a folder in the Project window to start on it directly.</summary>
        [MenuItem("Assets/Optimize Prefab Meshes...", false, 30)]
        public static void OpenFromProjectWindow()
        {
            OpenFor(AssetDatabase.GetAssetPath(Selection.activeObject), null);
        }

        [MenuItem("Assets/Optimize Prefab Meshes...", true)]
        static bool OpenFromProjectWindowValidate()
        {
            var folder = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder);
        }

        /// <summary>Opens the window pre-configured for a prefab folder and scans it.</summary>
        public static PrefabMeshOptimizerWindow OpenFor(string prefabFolder, string output)
        {
            var w = GetWindow<PrefabMeshOptimizerWindow>();
            w.ApplyTitle();
            w.minSize = new Vector2(760f, 640f);

            if (!AssetDatabase.IsValidFolder(prefabFolder))
            {
                Debug.LogWarning($"[MeshOptimizer] '{prefabFolder}' is not a folder.");
                return w;
            }

            w.state.sourceFolder = prefabFolder;
            w.sourceFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(prefabFolder);
            w.state.outputFolder = string.IsNullOrEmpty(output) ? DefaultOutputFor(prefabFolder) : output;
            w.Scan();
            w.Repaint();
            return w;
        }

        void ApplyTitle()
        {
            var icon = EditorGUIUtility.IconContent("MeshRenderer Icon")?.image
                       ?? EditorGUIUtility.IconContent("Mesh Icon")?.image;
            titleContent = new GUIContent("Mesh Optimizer", icon, "Prefab mesh optimizer");
        }

        // ---------------------------------------------------------------- persisted state

        /// <summary>Everything that should survive closing the window, including the queue position.</summary>
        [Serializable]
        class State
        {
            public string sourceFolder = "";
            public string outputFolder = "";
            public bool includeSubfolders = true;
            public bool skipPrefabsWithoutMeshes = true;
            public int minTriangles;
            public bool alsoReplaceColliderMeshes;
            public bool updateSharedMeshInPlace = true;

            public bool autoRebuild = true;
            public bool wireframe;
            public bool autoRotate;
            public bool showStatsOverlay = true;
            public int previewMode;
            public bool showQueue;
            public bool showLods = true;

            public float queueHeight = 120f;
            public float listHeight = 150f;
            public float previewWidth = 0.5f;       // fraction of the main area
            public bool compareVertical = true;     // stack the two previews instead of side-by-side
            public List<float> columnWidths = new List<float>();

            public string stickySettings = "";

            public List<string> queue = new List<string>();
            public List<int> statuses = new List<int>();
            public int index = -1;
        }

        static string ProjectSuffix => Application.dataPath.GetHashCode().ToString("X");
        static string PrefKey => "MeshOptimizer.State." + ProjectSuffix;
        static string LegacyPrefKey => "DroneProject.MeshOptimizer.State." + ProjectSuffix;

        State state = new State();

        void SaveState()
        {
            state.stickySettings = JsonUtility.ToJson(sticky);
            EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(state));
        }

        void LoadState()
        {
            var json = EditorPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(json))
                json = EditorPrefs.GetString(LegacyPrefKey, ""); // settings from before this was a package
            if (!string.IsNullOrEmpty(json))
            {
                try { state = JsonUtility.FromJson<State>(json) ?? new State(); }
                catch { state = new State(); }
            }

            if (!string.IsNullOrEmpty(state.stickySettings))
            {
                try { sticky = JsonUtility.FromJson<OptimizeSettings>(state.stickySettings) ?? new OptimizeSettings(); }
                catch { sticky = new OptimizeSettings(); }
            }

            if (!string.IsNullOrEmpty(state.sourceFolder) && AssetDatabase.IsValidFolder(state.sourceFolder))
                sourceFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(state.sourceFolder);

            // Drop queue entries whose prefab was deleted since the last session.
            for (int i = state.queue.Count - 1; i >= 0; i--)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(state.queue[i]) != null)
                    continue;

                state.queue.RemoveAt(i);
                if (i < state.statuses.Count)
                    state.statuses.RemoveAt(i);
            }

            while (state.statuses.Count < state.queue.Count)
                state.statuses.Add((int)PrefabStatus.Pending);
        }

        // ---------------------------------------------------------------- runtime state

        enum PrefabStatus { Pending, Applied, Skipped, Failed }
        enum PreviewMode { SideBySide, Original, Optimized }

        class MeshEntry
        {
            public Mesh referenced;               // mesh the prefab currently points at
            public Mesh source;                   // ORIGINAL mesh we simplify from
            public MeshStats sourceStats;
            public MeshStats referencedStats;
            public string key;                    // cache key of the source mesh
            public string label;                  // renderer path
            public string lodLabel;
            public int lodLevel = -1;
            public int useCount;
            public int colliderUseCount;          // MeshColliders in this prefab sharing this mesh
            public Material[] materials;
            public bool skinned;

            // Set when the prefab already points at one of our optimized assets: re-tuning
            // regenerates from `source` and overwrites this asset instead of making a new one.
            public string overwritePath;
            public bool isOrphanOutput;

            public bool include = true;
            public bool touched;                  // has settings of its own (user-edited or recorded)
            public OptimizeSettings settings;

            public Mesh result;
            public MeshStats resultStats;
            public string error;
            public bool needsRebuild = true;
            public MeshOptimizeCore.SimplifyJob job;
            public MeshOptimizerDatabase.MeshRecord cacheEntry;
            public bool reuseCache;

            public bool IsGenerating => job != null;
            public bool Reoptimizing => !string.IsNullOrEmpty(overwritePath);
            public bool AlreadyOptimized => Reoptimizing || cacheEntry != null;
        }

        class LodInfo
        {
            public string path;
            public string name;
            public float[] heights;
            public LODFadeMode fadeMode;
            public bool animateCrossFading;
            public int[] rendererCounts;
            public int[] triangleCounts;
            public bool changed;
        }

        DefaultAsset sourceFolderAsset;

        GameObject currentPrefab;
        List<MeshEntry> entries = new List<MeshEntry>();
        List<LodInfo> lods = new List<LodInfo>();
        int selected = -1;
        int hiddenByThreshold;

        MeshOptimizerDatabase db;
        OptimizeSettings sticky = new OptimizeSettings();

        // preview
        PreviewRenderUtility preview;
        Vector2 camAngles = new Vector2(25f, 135f);
        float camZoom = 1f;
        float lodViewFraction;                    // >0: frame the mesh at this screen height
        string lodViewLabel;
        static Material fallbackMaterial;
        double lastFrameTime;

        // generation debounce
        double dirtyStamp = -1;
        const double RebuildDelay = 0.25;
        const int AutoRebuildTriangleLimit = 250000;

        // layout
        const float BottomBarHeight = 34f;
        const float MinMainHeight = 120f;
        float mainAreaTop = 320f;   // measured one frame behind, identical in both passes
        float measuredTop = 320f;
        int draggingSplitter;                     // 0 none, 1 list, 2 settings
        Vector2 scrollQueue, scrollList, scrollSettings;
        const float RowHeight = 20f;
        const float SplitterHeight = 6f;

        // ---------------------------------------------------------------- lifecycle

        void OnEnable()
        {
            ApplyTitle();
            LoadState();

            if (state.queue.Count > 0 && state.index >= 0 && state.index < state.queue.Count)
            {
                MeshOptimizeCore.ClearProvenanceCache();
                db = MeshOptimizerDatabase.Instance;
                LoadCurrent();
            }
        }

        void OnDisable()
        {
            SaveState();
            DisposePreview();
            ClearEntries();
        }

        void Update()
        {
            var now = EditorApplication.timeSinceStartup;
            var dt = (float)Math.Min(0.1, now - lastFrameTime);
            lastFrameTime = now;

            var busy = false;

            // Collect finished worker results.
            foreach (var e in entries)
            {
                if (e.job == null)
                    continue;

                if (!e.job.IsDone)
                {
                    busy = true;
                    continue;
                }

                var job = e.job;
                e.job = null;

                DestroyIfTemporary(e.result);
                e.result = job.Complete();
                e.error = job.Error;
                e.resultStats = MeshStats.From(e.result);
                if (e.result != null)
                    e.result.hideFlags = HideFlags.DontSave;

                Repaint();
            }

            // Start the pending job - but never while a slider is still held or a field is being
            // typed into. Regenerating mid-drag feels bad and steals focus from number fields.
            if (state.autoRebuild && dirtyStamp >= 0 && now - dirtyStamp >= RebuildDelay)
            {
                if (EditorGUIUtility.editingTextField || GUIUtility.hotControl != 0)
                {
                    dirtyStamp = now;
                }
                else
                {
                    dirtyStamp = -1;
                    var e = Current;
                    if (e != null && e.needsRebuild && !e.reuseCache && e.job == null &&
                        e.sourceStats.triangles <= AutoRebuildTriangleLimit)
                    {
                        StartJob(e);
                        busy = true;
                    }
                }
            }

            if (state.autoRotate && currentPrefab != null)
            {
                camAngles.y += dt * 35f;
                Repaint();
            }

            if (busy)
                Repaint();
        }

        MeshEntry Current => selected >= 0 && selected < entries.Count ? entries[selected] : null;

        // ---------------------------------------------------------------- GUI

        void OnGUI()
        {
            HandleKeyboard();
            DrawSetup();

            if (state.queue.Count == 0)
            {
                EditorGUILayout.HelpBox("Pick a folder that contains prefabs, then press Start Optimizing.", MessageType.Info);
                return;
            }

            DrawQueueBar();

            if (state.showQueue)
            {
                DrawQueueList();
                state.queueHeight = DrawHorizontalSplitter(state.queueHeight, 3, 48f, 400f, +1f);
            }

            if (currentPrefab == null)
            {
                EditorGUILayout.HelpBox("Prefab could not be loaded. Skip to the next one.", MessageType.Warning);
                return;
            }

            if (lods.Count > 0)
                DrawLodSection();

            DrawMeshList();
            state.listHeight = DrawHorizontalSplitter(state.listHeight, 1, 60f, 420f, +1f);

            // Everything below is positioned absolutely. GUILayoutUtility.GetRect returns a dummy
            // rect during the layout pass, so a GUILayout-derived container rect would differ
            // between layout and repaint and every control inside it would be placed wrong.
            if (Event.current.type == EventType.Repaint)
                measuredTop = GUILayoutUtility.GetLastRect().yMax + 2f;

            var bottom = new Rect(0f, position.height - BottomBarHeight, position.width, BottomBarHeight);
            var top = Mathf.Clamp(mainAreaTop, 60f, Mathf.Max(60f, bottom.y - MinMainHeight));
            var main = new Rect(0f, top, position.width, Mathf.Max(MinMainHeight, bottom.y - top));

            DrawMainArea(main);

            GUILayout.BeginArea(bottom);
            DrawBottomBar();
            GUILayout.EndArea();

            // Adopt the new measurement only at the end of the frame, so the layout and repaint
            // passes of a single frame always agree on where the main area starts.
            if (Event.current.type == EventType.Repaint)
                mainAreaTop = measuredTop;
        }

        /// <summary>Preview on the left, settings on the right, with a draggable divider.</summary>
        void DrawMainArea(Rect area)
        {
            const float divider = 6f;
            const float minPane = 200f;

            var leftWidth = Mathf.Clamp(area.width * state.previewWidth, minPane, Mathf.Max(minPane, area.width - minPane - divider));
            var left = new Rect(area.x, area.y, leftWidth, area.height);
            var handle = new Rect(area.x + leftWidth, area.y, divider, area.height);
            var right = new Rect(handle.xMax, area.y, area.width - leftWidth - divider, area.height);

            var e = Current;

            GUILayout.BeginArea(left);
            DrawPreview(e);
            GUILayout.EndArea();

            DrawVerticalSplitterHandle(handle, area);

            GUILayout.BeginArea(right);
            if (e != null)
            {
                DrawSettings(e, right.height);
            }
            else
            {
                EditorGUILayout.HelpBox(entries.Count == 0
                    ? "This prefab has no meshes above the threshold."
                    : "Select a mesh to preview and tune it.", MessageType.Info);
            }
            GUILayout.EndArea();
        }

        void HandleKeyboard()
        {
            var ev = Event.current;
            if (ev.type != EventType.KeyDown || EditorGUIUtility.editingTextField || entries.Count == 0)
                return;

            if (ev.keyCode == KeyCode.DownArrow)
            {
                Select(Mathf.Min(selected + 1, entries.Count - 1));
                RevealSelected();
                ev.Use();
                Repaint();
            }
            else if (ev.keyCode == KeyCode.UpArrow)
            {
                Select(Mathf.Max(selected - 1, 0));
                RevealSelected();
                ev.Use();
                Repaint();
            }
        }

        void RevealSelected()
        {
            if (selected < 0)
                return;

            var top = selected * RowHeight;
            var bottom = top + RowHeight;
            if (top < scrollList.y)
                scrollList.y = top;
            else if (bottom > scrollList.y + state.listHeight)
                scrollList.y = bottom - state.listHeight;
        }
    }
}
