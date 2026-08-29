using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// </summary>
    public class PrefabMeshOptimizerWindow : EditorWindow
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

        void DrawSetup()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    sourceFolderAsset = (DefaultAsset)EditorGUILayout.ObjectField("Prefab Folder", sourceFolderAsset, typeof(DefaultAsset), false);
                    if (EditorGUI.EndChangeCheck() && sourceFolderAsset != null)
                    {
                        var p = AssetDatabase.GetAssetPath(sourceFolderAsset);
                        if (AssetDatabase.IsValidFolder(p))
                        {
                            state.sourceFolder = p;
                            state.outputFolder = DefaultOutputFor(p);
                        }
                        else
                        {
                            sourceFolderAsset = null;
                            Debug.LogWarning("[MeshOptimizer] Drop a folder, not a file.");
                        }
                    }

                    if (GUILayout.Button("Browse", GUILayout.Width(70f)))
                    {
                        var abs = EditorUtility.OpenFolderPanel("Prefab folder", Application.dataPath, "");
                        if (!string.IsNullOrEmpty(abs) && abs.StartsWith(Application.dataPath))
                        {
                            state.sourceFolder = "Assets" + abs.Substring(Application.dataPath.Length).Replace('\\', '/');
                            sourceFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(state.sourceFolder);
                            state.outputFolder = DefaultOutputFor(state.sourceFolder);
                        }
                    }
                }

                state.outputFolder = EditorGUILayout.TextField(
                    new GUIContent("Output Folder", "Where optimized meshes are written. A small cache file is kept there so the tool recognises its own output later."),
                    state.outputFolder);

                using (new EditorGUILayout.HorizontalScope())
                {
                    state.includeSubfolders = EditorGUILayout.ToggleLeft("Include subfolders", state.includeSubfolders, GUILayout.Width(135f));
                    state.skipPrefabsWithoutMeshes = EditorGUILayout.ToggleLeft(
                        new GUIContent("Skip empty prefabs", "Prefabs with no qualifying mesh are left out of the queue."),
                        state.skipPrefabsWithoutMeshes, GUILayout.Width(145f));

                    var prev = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 165f;
                    state.minTriangles = Mathf.Max(0, EditorGUILayout.IntField(
                        new GUIContent("Skip meshes under [tris]", "Meshes with fewer triangles than this are not listed and never touched. 0 lists everything."),
                        state.minTriangles, GUILayout.Width(230f)));
                    EditorGUIUtility.labelWidth = prev;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    state.alsoReplaceColliderMeshes = EditorGUILayout.ToggleLeft(
                        new GUIContent("Also swap MeshColliders",
                            "Only swaps a MeshCollider that shares one of the meshes optimized here. Colliders using their own " +
                            "dedicated collision mesh are never touched - that mesh belongs to no renderer, so it is never optimized. " +
                            "Rows in the mesh list are marked with * when a collider shares them."),
                        state.alsoReplaceColliderMeshes, GUILayout.Width(190f));

                    state.updateSharedMeshInPlace = EditorGUILayout.ToggleLeft(
                        new GUIContent("Reuse shared meshes",
                            "A mesh used by several prefabs is generated once and re-pointed afterwards; re-tuning overwrites that asset in place."),
                        state.updateSharedMeshInPlace);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(state.sourceFolder) || !AssetDatabase.IsValidFolder(state.sourceFolder)))
                    {
                        if (GUILayout.Button(state.queue.Count == 0 ? "Start Optimizing" : "Re-scan Folder", GUILayout.Height(24f)))
                            Scan();
                    }

                    if (state.queue.Count > 0 && GUILayout.Button("Stop", GUILayout.Width(70f), GUILayout.Height(24f)))
                    {
                        ClearEntries();
                        state.queue.Clear();
                        state.statuses.Clear();
                        state.index = -1;
                        currentPrefab = null;
                        SaveState();
                    }
                }
            }
        }

        void DrawQueueBar()
        {
            var applied = state.statuses.Count(x => x == (int)PrefabStatus.Applied);
            var skipped = state.statuses.Count(x => x == (int)PrefabStatus.Skipped);
            var failed = state.statuses.Count(x => x == (int)PrefabStatus.Failed);
            var pending = state.queue.Count - applied - skipped - failed;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"{state.index + 1} / {state.queue.Count}", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
                GUILayout.Label(new GUIContent($"done {applied}   skipped {skipped}   left {pending}" + (failed > 0 ? $"   failed {failed}" : ""),
                    "Progress is stored per prefab in ProjectSettings/MeshOptimizer.json, so it survives closing the window."),
                    EditorStyles.miniLabel, GUILayout.Width(230f));
                GUILayout.Label(currentPrefab != null ? currentPrefab.name : "-", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(pending == 0))
                {
                    if (GUILayout.Button(new GUIContent("Resume", "Jump to the first prefab that is still pending."),
                            EditorStyles.toolbarButton, GUILayout.Width(65f)))
                        GotoFirstPending();
                }


                using (new EditorGUI.DisabledScope(state.index <= 0))
                {
                    if (GUILayout.Button("< Prev", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                        Goto(state.index - 1);
                }

                using (new EditorGUI.DisabledScope(state.index >= state.queue.Count - 1))
                {
                    if (GUILayout.Button("Next >", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                        Goto(state.index + 1);
                }

                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                    Goto(state.index);

                if (GUILayout.Button("Select in Project", EditorStyles.toolbarButton, GUILayout.Width(110f)) && currentPrefab != null)
                {
                    Selection.activeObject = currentPrefab;
                    EditorGUIUtility.PingObject(currentPrefab);
                }

                state.showQueue = GUILayout.Toggle(state.showQueue, "Queue", EditorStyles.toolbarButton, GUILayout.Width(55f));
            }
        }

        void DrawQueueList()
        {
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollQueue, GUILayout.Height(state.queueHeight)))
            {
                scrollQueue = sv.scrollPosition;

                for (int i = 0; i < state.queue.Count; i++)
                {
                    var row = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));
                    var status = (PrefabStatus)state.statuses[i];
                    var ev = Event.current;
                    var hover = row.Contains(ev.mousePosition);

                    if (ev.type == EventType.Repaint)
                    {
                        if (i == state.index)
                            EditorGUI.DrawRect(row, new Color(0.30f, 0.45f, 0.75f, 0.28f));
                        else if (hover)
                            EditorGUI.DrawRect(row, new Color(1f, 1f, 1f, 0.04f));

                        // Colour bar on the left makes the run state readable at a glance.
                        var bar = new Rect(row.x, row.y + 2f, 3f, row.height - 4f);
                        EditorGUI.DrawRect(bar, StatusColor(status));
                    }

                    var iconRect = new Rect(row.x + 7f, row.y + 2f, 16f, 16f);
                    var icon = QueueStatusIcon(status);
                    if (icon != null)
                        GUI.Label(iconRect, icon);

                    var labelRect = new Rect(row.x + 26f, row.y, row.width - 30f, row.height);
                    var style = i == state.index ? EditorStyles.boldLabel : EditorStyles.label;
                    var prev = GUI.color;
                    if (status == PrefabStatus.Skipped)
                        GUI.color = new Color(1f, 1f, 1f, 0.6f);
                    GUI.Label(labelRect, Path.GetFileNameWithoutExtension(state.queue[i]), style);
                    GUI.color = prev;

                    if (ev.type == EventType.MouseDown && ev.button == 0 && hover)
                    {
                        Goto(i);
                        ev.Use();
                    }
                }
            }
        }

        static Color StatusColor(PrefabStatus s) => s switch
        {
            PrefabStatus.Applied => new Color(0.35f, 0.75f, 0.4f, 0.9f),
            PrefabStatus.Skipped => new Color(0.6f, 0.6f, 0.6f, 0.7f),
            PrefabStatus.Failed => new Color(0.85f, 0.35f, 0.3f, 0.9f),
            _ => new Color(1f, 1f, 1f, 0.12f)
        };

        static GUIContent QueueStatusIcon(PrefabStatus s)
        {
            switch (s)
            {
                case PrefabStatus.Applied:
                    return new GUIContent(EditorGUIUtility.IconContent("TestPassed")?.image, "Optimized and applied");
                case PrefabStatus.Skipped:
                    return new GUIContent(EditorGUIUtility.IconContent("TestIgnored")?.image, "Skipped");
                case PrefabStatus.Failed:
                    return new GUIContent(EditorGUIUtility.IconContent("TestFailed")?.image, "Failed - see console");
                default:
                    return null;
            }
        }

        void GotoFirstPending()
        {
            for (int i = 0; i < state.statuses.Count; i++)
            {
                if (state.statuses[i] == (int)PrefabStatus.Pending)
                {
                    Goto(i);
                    return;
                }
            }
        }

        void DrawLodSection()
        {
            state.showLods = EditorGUILayout.Foldout(state.showLods, $"LOD Groups ({lods.Count})", true, EditorStyles.foldoutHeader);
            if (!state.showLods)
                return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var lod in lods)
                {
                    EditorGUILayout.LabelField(lod.name, EditorStyles.boldLabel);
                    EditorGUI.indentLevel++;

                    EditorGUI.BeginChangeCheck();
                    lod.fadeMode = (LODFadeMode)EditorGUILayout.EnumPopup("Fade Mode", lod.fadeMode);
                    if (lod.fadeMode != LODFadeMode.None)
                        lod.animateCrossFading = EditorGUILayout.Toggle("Animate Cross-fading", lod.animateCrossFading);

                    for (int i = 0; i < lod.heights.Length; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var label = $"LOD{i}  ({lod.rendererCounts[i]} rend, {lod.triangleCounts[i]:N0} tris)";
                            lod.heights[i] = EditorGUILayout.Slider(label, lod.heights[i], 0f, 1f);

                            var pct = lod.heights[i] * 100f;
                            if (GUILayout.Button(new GUIContent("LOD View",
                                    $"Frame the preview at {pct:0.##}% screen height - the size at which LOD{i} hands over."),
                                EditorStyles.miniButton, GUILayout.Width(70f)))
                            {
                                lodViewFraction = Mathf.Max(0.0005f, lod.heights[i]);
                                lodViewLabel = $"LOD{i} switch ({pct:0.##}% screen height)";
                            }
                        }
                    }

                    EditorGUILayout.LabelField("Culled below", $"{lod.heights[lod.heights.Length - 1] * 100f:0.##}% screen height", EditorStyles.miniLabel);
                    if (EditorGUI.EndChangeCheck())
                        lod.changed = true;

                    if (lod.changed)
                        EditorGUILayout.LabelField(" ", "modified - saved on Apply", EditorStyles.miniLabel);

                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space(4f);
                }
            }
        }

        // ---------------------------------------------------------------- mesh list

        static readonly string[] ColumnTitles = { "mesh", "lod", "used", "tris", "verts", "sub", "optimized" };
        static readonly float[] DefaultColumnWidths = { 220f, 42f, 40f, 70f, 70f, 34f, 160f };
        const float IconColumnWidth = 20f;

        List<float> ColumnWidths
        {
            get
            {
                if (state.columnWidths == null)
                    state.columnWidths = new List<float>();

                while (state.columnWidths.Count < ColumnTitles.Length)
                    state.columnWidths.Add(DefaultColumnWidths[state.columnWidths.Count]);

                return state.columnWidths;
            }
        }

        float TotalColumnsWidth()
        {
            var total = IconColumnWidth;
            foreach (var w in ColumnWidths)
                total += w;
            return total + 4f;
        }

        void DrawMeshList()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Meshes ({entries.Count})", EditorStyles.boldLabel, GUILayout.Width(120f));
                if (hiddenByThreshold > 0)
                    GUILayout.Label($"{hiddenByThreshold} hidden under {state.minTriangles:N0} tris", EditorStyles.miniLabel);

                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Auto Size", "Resize every column to fit its widest value."),
                        EditorStyles.miniButton, GUILayout.Width(75f)))
                    AutoSizeColumns();

                if (GUILayout.Button(new GUIContent("Reset", "Restore the default column widths."),
                        EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    state.columnWidths = new List<float>(DefaultColumnWidths);
                    SaveState();
                }
            }

            DrawColumnHeader();

            using (var sv = new EditorGUILayout.ScrollViewScope(scrollList, GUILayout.Height(state.listHeight)))
            {
                scrollList = sv.scrollPosition;

                var width = TotalColumnsWidth();
                for (int i = 0; i < entries.Count; i++)
                    DrawMeshRow(i, entries[i], width);
            }
        }

        void DrawColumnHeader()
        {
            var headerRect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
            GUI.BeginGroup(headerRect);

            var widths = ColumnWidths;
            var x = IconColumnWidth - scrollList.x;

            for (int i = 0; i < ColumnTitles.Length; i++)
            {
                var cell = new Rect(x, 0f, widths[i], headerRect.height);
                GUI.Label(cell, ColumnTitles[i], EditorStyles.miniBoldLabel);
                x += widths[i];

                // Drag handle on the column's right edge.
                var handle = new Rect(x - 3f, 0f, 6f, headerRect.height);
                EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeHorizontal);

                var id = 100 + i;
                var ev = Event.current;
                if (ev.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(new Rect(x - 1f, 2f, 1f, headerRect.height - 4f),
                        new Color(1f, 1f, 1f, draggingSplitter == id ? 0.35f : 0.12f));
                }
                else if (ev.type == EventType.MouseDown && ev.button == 0 && handle.Contains(ev.mousePosition))
                {
                    draggingSplitter = id;
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDrag && draggingSplitter == id)
                {
                    widths[i] = Mathf.Clamp(widths[i] + ev.delta.x, 28f, 600f);
                    ev.Use();
                    Repaint();
                }
                else if (ev.type == EventType.MouseUp && draggingSplitter == id)
                {
                    draggingSplitter = 0;
                    ev.Use();
                    SaveState();
                }
            }

            GUI.EndGroup();
        }

        void AutoSizeColumns()
        {
            var widths = ColumnWidths;
            var style = EditorStyles.label;
            var content = new GUIContent();

            float Measure(string text)
            {
                content.text = text ?? "";
                return style.CalcSize(content).x + 10f;
            }

            for (int i = 0; i < ColumnTitles.Length; i++)
                widths[i] = Measure(ColumnTitles[i]);

            foreach (var e in entries)
            {
                widths[0] = Mathf.Max(widths[0], Measure(e.source != null ? e.source.name : "<missing>"));
                widths[1] = Mathf.Max(widths[1], Measure(e.lodLabel));
                widths[2] = Mathf.Max(widths[2], Measure(e.useCount + (e.colliderUseCount > 0 ? "*" : "")));
                widths[3] = Mathf.Max(widths[3], Measure(e.sourceStats.triangles.ToString("N0")));
                widths[4] = Mathf.Max(widths[4], Measure(e.sourceStats.vertices.ToString("N0")));
                widths[5] = Mathf.Max(widths[5], Measure(e.sourceStats.subMeshes.ToString()));
                widths[6] = Mathf.Max(widths[6], Measure(StatusText(e)));
            }

            for (int i = 0; i < widths.Count; i++)
                widths[i] = Mathf.Clamp(widths[i], 30f, 600f);

            SaveState();
            Repaint();
        }

        struct RowColumns
        {
            public Rect icon, name, lod, used, tris, verts, sub, status;
        }

        RowColumns Columns(Rect row)
        {
            var widths = ColumnWidths;
            var c = new RowColumns();
            var x = row.x;

            c.icon = new Rect(x + 1f, row.y, IconColumnWidth - 2f, row.height);
            x += IconColumnWidth;

            var cells = new Rect[ColumnTitles.Length];
            for (int i = 0; i < ColumnTitles.Length; i++)
            {
                cells[i] = new Rect(x, row.y, widths[i], row.height);
                x += widths[i];
            }

            c.name = cells[0];
            c.lod = cells[1];
            c.used = cells[2];
            c.tris = cells[3];
            c.verts = cells[4];
            c.sub = cells[5];
            c.status = cells[6];
            return c;
        }

        void DrawMeshRow(int i, MeshEntry e, float width)
        {
            var row = GUILayoutUtility.GetRect(width, RowHeight, GUILayout.Width(width));
            var c = Columns(row);
            var ev = Event.current;
            var rowHover = row.Contains(ev.mousePosition);
            var statusHover = c.status.Contains(ev.mousePosition);

            if (ev.type == EventType.Repaint)
            {
                if (i == selected)
                    EditorGUI.DrawRect(row, new Color(0.30f, 0.45f, 0.75f, 0.28f));
                else if (rowHover)
                    EditorGUI.DrawRect(row, new Color(1f, 1f, 1f, 0.04f));
            }

            // Provenance icon: shows at a glance which meshes were already processed.
            var icon = StatusIcon(e);
            if (icon != null)
                GUI.Label(c.icon, icon);

            var prevColor = GUI.color;
            if (!e.include)
                GUI.color = new Color(1f, 1f, 1f, 0.5f);

            GUI.Label(c.name, new GUIContent(e.source != null ? e.source.name : "<missing>", e.label));
            GUI.Label(c.lod, e.lodLabel ?? "");
            GUI.Label(c.used, new GUIContent(e.useCount + (e.colliderUseCount > 0 ? "*" : ""),
                e.colliderUseCount > 0
                    ? $"{e.useCount} renderer(s) and {e.colliderUseCount} MeshCollider(s) in this prefab share this mesh"
                    : $"{e.useCount} renderer(s) in this prefab use this mesh"));
            GUI.Label(c.tris, e.sourceStats.triangles.ToString("N0"));
            GUI.Label(c.verts, e.sourceStats.vertices.ToString("N0"));
            GUI.Label(c.sub, e.sourceStats.subMeshes.ToString());

            GUI.color = prevColor;

            // The status cell doubles as the include/exclude control.
            if (statusHover && !e.IsGenerating)
            {
                GUI.Label(c.status, e.include ? "Cancel" : "Include", LinkStyle);
                if (ev.type == EventType.MouseDown && ev.button == 0)
                {
                    e.include = !e.include;
                    ev.Use();
                    Repaint();
                }
            }
            else
            {
                GUI.Label(c.status, StatusText(e), EditorStyles.miniLabel);
            }

            if (ev.type == EventType.MouseDown && ev.button == 0 && rowHover && !statusHover)
            {
                Select(i);
                ev.Use();
                Repaint();
            }
        }

        GUIContent StatusIcon(MeshEntry e)
        {
            if (e.isOrphanOutput)
                return new GUIContent(EditorGUIUtility.IconContent("console.warnicon.sml").image,
                    "Already an optimized asset, but its original is unknown (no database record). " +
                    "Optimizing it again would compound the error.");

            if (!e.AlreadyOptimized)
                return null;

            var assetPath = e.Reoptimizing ? e.overwritePath : e.cacheEntry.outputPath;
            var applied = e.Reoptimizing ? e.referencedStats : MeshStats.From(AssetDatabase.LoadAssetAtPath<Mesh>(assetPath));
            var assetName = Path.GetFileNameWithoutExtension(assetPath);

            var tip = e.Reoptimizing
                ? $"Already optimized - this prefab uses {assetName}\n" +
                  $"applied:  {applied.triangles:N0} tris, {applied.vertices:N0} verts\n" +
                  $"original: {e.sourceStats.triangles:N0} tris, {e.sourceStats.vertices:N0} verts\n" +
                  "Re-tuning overwrites that same asset."
                : $"Optimized earlier: {assetName}\n" +
                  $"optimized: {applied.triangles:N0} tris, {applied.vertices:N0} verts\n" +
                  $"original:  {e.sourceStats.triangles:N0} tris, {e.sourceStats.vertices:N0} verts\n" +
                  "Applying re-points this prefab at it without regenerating.";

            var img = EditorGUIUtility.IconContent("TestPassed")?.image
                      ?? EditorGUIUtility.IconContent("Valid")?.image;
            return new GUIContent(img, tip);
        }

        static GUIStyle linkStyle;
        static GUIStyle LinkStyle
        {
            get
            {
                if (linkStyle == null)
                {
                    linkStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = new Color(0.45f, 0.7f, 1f) }
                    };
                }
                return linkStyle;
            }
        }

        static string StatusText(MeshEntry e)
        {
            if (!e.include)
                return "excluded";
            if (e.IsGenerating)
                return Spinner() + " generating...";
            if (!string.IsNullOrEmpty(e.error))
                return "error";
            if (e.reuseCache && e.cacheEntry != null)
                return "reuse " + Path.GetFileNameWithoutExtension(e.cacheEntry.outputPath);
            if (e.result != null)
            {
                var pct = e.sourceStats.triangles > 0 ? 100f * e.resultStats.triangles / e.sourceStats.triangles : 0f;
                return $"{e.resultStats.triangles:N0} tris ({pct:0.#}%)";
            }
            if (e.Reoptimizing)
                return $"applied: {e.referencedStats.triangles:N0} tris";
            if (e.needsRebuild)
                return "not generated";
            return "-";
        }

        // ---------------------------------------------------------------- splitters

        /// <summary>Horizontal drag handle between stacked panes; sign flips which way it grows.</summary>
        float DrawHorizontalSplitter(float value, int id, float min, float max, float sign)
        {
            var r = GUILayoutUtility.GetRect(0f, SplitterHeight, GUILayout.ExpandWidth(true));
            EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeVertical);

            var ev = Event.current;
            if (ev.type == EventType.Repaint)
            {
                var line = new Rect(r.x + 4f, r.y + r.height * 0.5f - 1f, r.width - 8f, 2f);
                EditorGUI.DrawRect(line, new Color(1f, 1f, 1f, draggingSplitter == id ? 0.25f : 0.08f));
            }
            else if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition))
            {
                draggingSplitter = id;
                ev.Use();
            }
            else if (ev.type == EventType.MouseDrag && draggingSplitter == id)
            {
                value = Mathf.Clamp(value + ev.delta.y * sign, min, max);
                ev.Use();
                Repaint();
            }
            else if (ev.type == EventType.MouseUp && draggingSplitter == id)
            {
                draggingSplitter = 0;
                ev.Use();
                SaveState();
            }

            return value;
        }

        /// <summary>Vertical divider between the preview and settings columns.</summary>
        void DrawVerticalSplitterHandle(Rect handle, Rect area)
        {
            EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeHorizontal);

            const int id = 2;
            var ev = Event.current;

            if (ev.type == EventType.Repaint)
            {
                var line = new Rect(handle.x + handle.width * 0.5f - 1f, handle.y + 4f, 2f, handle.height - 8f);
                EditorGUI.DrawRect(line, new Color(1f, 1f, 1f, draggingSplitter == id ? 0.25f : 0.08f));
            }
            else if (ev.type == EventType.MouseDown && ev.button == 0 && handle.Contains(ev.mousePosition))
            {
                draggingSplitter = id;
                ev.Use();
            }
            else if (ev.type == EventType.MouseDrag && draggingSplitter == id)
            {
                state.previewWidth = Mathf.Clamp((ev.mousePosition.x - area.x) / Mathf.Max(1f, area.width), 0.15f, 0.85f);
                ev.Use();
                Repaint();
            }
            else if (ev.type == EventType.MouseUp && draggingSplitter == id)
            {
                draggingSplitter = 0;
                ev.Use();
                SaveState();
            }
        }

        // ---------------------------------------------------------------- preview

        void DrawPreview(MeshEntry e)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                state.previewMode = (int)(PreviewMode)EditorGUILayout.EnumPopup((PreviewMode)state.previewMode, EditorStyles.toolbarPopup, GUILayout.Width(90f));

                using (new EditorGUI.DisabledScope((PreviewMode)state.previewMode != PreviewMode.SideBySide))
                {
                    var split = state.compareVertical ? "Stacked" : "Side by side";
                    if (GUILayout.Button(new GUIContent(split, "Switch between stacking the two views and placing them next to each other."),
                            EditorStyles.toolbarButton, GUILayout.Width(85f)))
                        state.compareVertical = !state.compareVertical;
                }

                state.wireframe = GUILayout.Toggle(state.wireframe, new GUIContent("Wire", "Draw both meshes as wireframe to compare topology."), EditorStyles.toolbarButton, GUILayout.Width(45f));
                state.autoRotate = GUILayout.Toggle(state.autoRotate, new GUIContent("Rotate", "Spin both previews so you can judge the silhouette from every angle."), EditorStyles.toolbarButton, GUILayout.Width(55f));
                state.showStatsOverlay = GUILayout.Toggle(state.showStatsOverlay, new GUIContent("Stats", "Overlay triangle/vertex counts on the previews."), EditorStyles.toolbarButton, GUILayout.Width(45f));

                if (GUILayout.Button(new GUIContent("Reset", "Back to the default framing."), EditorStyles.toolbarButton, GUILayout.Width(50f)))
                {
                    camAngles = new Vector2(25f, 135f);
                    camZoom = 1f;
                    lodViewFraction = 0f;
                }

                GUILayout.FlexibleSpace();
                if (lodViewFraction > 0f)
                {
                    GUILayout.Label(lodViewLabel, EditorStyles.miniLabel);
                    if (GUILayout.Button("exit", EditorStyles.toolbarButton, GUILayout.Width(35f)))
                        lodViewFraction = 0f;
                }
            }

            var rect = GUILayoutUtility.GetRect(10f, 40f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            HandlePreviewInput(rect);
            EnsurePreview();

            if (e == null)
            {
                GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
                return;
            }

            switch ((PreviewMode)state.previewMode)
            {
                case PreviewMode.SideBySide:
                    Rect first, second;
                    if (state.compareVertical)
                    {
                        var h = rect.height * 0.5f - 2f;
                        first = new Rect(rect.x, rect.y, rect.width, h);
                        second = new Rect(rect.x, rect.y + h + 4f, rect.width, h);
                    }
                    else
                    {
                        var w = rect.width * 0.5f - 2f;
                        first = new Rect(rect.x, rect.y, w, rect.height);
                        second = new Rect(rect.x + w + 4f, rect.y, w, rect.height);
                    }

                    RenderMesh(first, e.source, e.materials, e.source, "ORIGINAL", e.sourceStats);
                    RenderMesh(second, ResolveResultMesh(e), e.materials, e.source, "OPTIMIZED", OptimizedStats(e));
                    break;

                case PreviewMode.Original:
                    RenderMesh(rect, e.source, e.materials, e.source, "ORIGINAL", e.sourceStats);
                    break;

                default:
                    RenderMesh(rect, ResolveResultMesh(e), e.materials, e.source, "OPTIMIZED", OptimizedStats(e));
                    break;
            }
        }

        Mesh ResolveResultMesh(MeshEntry e)
        {
            if (e.reuseCache && e.cacheEntry != null)
                return AssetDatabase.LoadAssetAtPath<Mesh>(e.cacheEntry.outputPath);

            if (e.result == null && e.Reoptimizing)
                return AssetDatabase.LoadAssetAtPath<Mesh>(e.overwritePath);

            return e.result;
        }

        MeshStats OptimizedStats(MeshEntry e)
        {
            if (e.result != null)
                return e.resultStats;

            var shown = ResolveResultMesh(e);
            return shown != null ? MeshStats.From(shown) : default;
        }

        void HandlePreviewInput(Rect rect)
        {
            var ev = Event.current;
            if (!rect.Contains(ev.mousePosition))
                return;

            if (ev.type == EventType.MouseDrag && (ev.button == 0 || ev.button == 2))
            {
                camAngles.y += ev.delta.x;
                camAngles.x = Mathf.Clamp(camAngles.x + ev.delta.y, -89f, 89f);
                ev.Use();
                Repaint();
            }
            else if (ev.type == EventType.ScrollWheel)
            {
                lodViewFraction = 0f; // zooming leaves the LOD framing
                camZoom = Mathf.Clamp(camZoom * (1f - ev.delta.y * 0.05f), 0.1f, 10f);
                ev.Use();
                Repaint();
            }
        }

        void RenderMesh(Rect rect, Mesh mesh, Material[] materials, Mesh framingMesh, string title, MeshStats stats)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            if (mesh != null)
            {
                var bounds = framingMesh != null ? framingMesh.bounds : mesh.bounds;
                var radius = Mathf.Max(0.001f, bounds.extents.magnitude);
                const float fov = 30f;

                // LOD view: put the camera where the object covers `lodViewFraction` of the screen
                // height - exactly the size at which that LOD level hands over.
                var dist = lodViewFraction > 0f
                    ? radius * 2f / (2f * lodViewFraction * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad))
                    : radius * 3.2f / camZoom;

                preview.BeginPreview(rect, GUIStyle.none);

                var rot = Quaternion.Euler(camAngles.x, camAngles.y, 0f);
                var cam = preview.camera;
                cam.transform.rotation = rot;
                cam.transform.position = bounds.center - rot * Vector3.forward * dist;
                cam.nearClipPlane = Mathf.Max(0.001f, dist * 0.005f);
                cam.farClipPlane = dist * 10f + radius * 4f;
                cam.fieldOfView = fov;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.18f, 0.18f, 0.19f, 1f);

                preview.lights[0].intensity = 1.15f;
                preview.lights[0].transform.rotation = Quaternion.Euler(35f, 35f, 0f);
                preview.lights[1].intensity = 0.55f;
                preview.lights[1].transform.rotation = Quaternion.Euler(-25f, 200f, 0f);
                preview.ambientColor = new Color(0.28f, 0.28f, 0.32f, 1f);

                GL.wireframe = state.wireframe;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var mat = materials != null && materials.Length > 0
                        ? materials[Mathf.Min(s, materials.Length - 1)]
                        : null;
                    preview.DrawMesh(mesh, Matrix4x4.identity, mat ?? FallbackMaterial, s);
                }

                cam.Render();
                GL.wireframe = false;

                GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
            }
            else
            {
                GUI.Label(rect, "Not generated yet - press Generate", EditorStyles.centeredGreyMiniLabel);
            }

            if (!state.showStatsOverlay)
                return;

            var label = mesh == null
                ? title
                : $"{title}\n{stats.triangles:N0} tris   {stats.vertices:N0} verts   {stats.subMeshes} sub\n{stats.Attributes}";

            EditorGUI.DropShadowLabel(new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, 46f), label);
        }

        static Material FallbackMaterial
        {
            get
            {
                if (fallbackMaterial == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    fallbackMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                return fallbackMaterial;
            }
        }

        // ---------------------------------------------------------------- settings

        void DrawSettings(MeshEntry e, float columnHeight)
        {
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollSettings, GUILayout.Height(columnHeight)))
            {
                scrollSettings = sv.scrollPosition;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Settings - {(e.source != null ? e.source.name : "-")}", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();

                    if (e.IsGenerating)
                        GUILayout.Label(Spinner() + " generating", EditorStyles.miniLabel, GUILayout.Width(90f));

                    using (new EditorGUI.DisabledScope(e.IsGenerating))
                    {
                        state.autoRebuild = GUILayout.Toggle(state.autoRebuild,
                            new GUIContent("Auto", "Regenerate the preview automatically a moment after you stop changing settings. " +
                                                   "Turn it off to regenerate only when you press Generate - useful on very heavy meshes."),
                            EditorStyles.miniButton, GUILayout.Width(50f));

                        if (GUILayout.Button(new GUIContent("Generate", "Re-simplify this mesh now with the settings below. " +
                                                                        "The result stays a preview until you press Apply."),
                                EditorStyles.miniButton, GUILayout.Width(70f)))
                            StartJob(e);

                        if (GUILayout.Button(new GUIContent("Copy to all", "Copy these settings onto every other mesh in this prefab " +
                                                                           "and mark them for regeneration."),
                                EditorStyles.miniButton, GUILayout.Width(80f)))
                            CopySettingsToAll(e);
                    }
                }

                if (e.isOrphanOutput)
                    EditorGUILayout.HelpBox("This mesh is already an optimized asset but is not in the cache, so the original is unknown. Optimizing it again compounds the error.", MessageType.Warning);

                if (!string.IsNullOrEmpty(e.error))
                    EditorGUILayout.HelpBox(e.error, MessageType.Error);

                DrawReductionHint(e);

                var locked = e.reuseCache || e.IsGenerating;
                var body = EditorGUILayout.BeginVertical();
                var prevLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 230f;

                using (new EditorGUI.DisabledScope(locked))
                {
                    var s = e.settings;
                    EditorGUI.BeginChangeCheck();

                    s.quality = EditorGUILayout.Slider(new GUIContent("Quality", "Fraction of triangles to keep."), s.quality, 0.01f, 1f);
                    EditorGUILayout.LabelField(" ", $"target ~{Mathf.RoundToInt(e.sourceStats.triangles * s.quality):N0} tris", EditorStyles.miniLabel);

                    EditorGUILayout.LabelField("Preservation", EditorStyles.miniBoldLabel);
                    s.preserveBorderEdges = EditorGUILayout.Toggle(new GUIContent("Preserve Border Edges", "Keeps open mesh borders intact. Fixes shrinking planes/fences."), s.preserveBorderEdges);
                    s.preserveUVSeamEdges = EditorGUILayout.Toggle(new GUIContent("Preserve UV Seam Edges", "Keeps UV seams. On hard-surface meshes with per-face UVs this locks nearly every edge and quality stops having any effect."), s.preserveUVSeamEdges);
                    s.preserveUVFoldoverEdges = EditorGUILayout.Toggle(new GUIContent("Preserve UV Foldover Edges", "Keeps folded UV edges."), s.preserveUVFoldoverEdges);
                    s.preserveSurfaceCurvature = EditorGUILayout.Toggle(new GUIContent("Preserve Surface Curvature", "Biases the error metric toward keeping curved detail."), s.preserveSurfaceCurvature);

                    EditorGUILayout.LabelField("Vertex Linking", EditorStyles.miniBoldLabel);
                    s.enableSmartLink = EditorGUILayout.Toggle(new GUIContent("Enable Smart Link", "Links vertices that are coincident but separate; closes cracks."), s.enableSmartLink);
                    using (new EditorGUI.DisabledScope(!s.enableSmartLink))
                    {
                        s.vertexLinkDistance = EditorGUILayout.DelayedFloatField(
                            new GUIContent("Vertex Link Distance", "0 = epsilon. Small values (1e-4) weld split vertices; large values (0.01+) over-constrain the mesh and can stop reduction entirely."),
                            s.vertexLinkDistance);
                        s.vertexLinkDistance = Mathf.Max(0f, s.vertexLinkDistance);
                    }

                    EditorGUILayout.LabelField("Algorithm", EditorStyles.miniBoldLabel);
                    s.agressiveness = EditorGUILayout.Slider(new GUIContent("Aggressiveness", "Higher collapses more per pass at lower quality. 7 is the default."), s.agressiveness, 1f, 20f);
                    s.maxIterationCount = EditorGUILayout.IntSlider(new GUIContent("Max Iterations", "Upper bound on collapse passes."), s.maxIterationCount, 1, 200);

                    s.manualUVComponentCount = EditorGUILayout.Toggle(new GUIContent("Manual UV Component Count", "Force UVs to 1-4 components instead of auto-detect."), s.manualUVComponentCount);
                    using (new EditorGUI.DisabledScope(!s.manualUVComponentCount))
                        s.uvComponentCount = EditorGUILayout.IntSlider("UV Components", s.uvComponentCount, 1, 4);

                    EditorGUILayout.LabelField("Post-process", EditorStyles.miniBoldLabel);
                    s.recalculateNormals = EditorGUILayout.Toggle(new GUIContent("Recalculate Normals", "Off by default: it averages normals across hard edges and flattens the shading of hard-surface props. Turn on only when shading looks broken."), s.recalculateNormals);
                    s.recalculateTangents = EditorGUILayout.Toggle(new GUIContent("Recalculate Tangents", "On by default: rebuilds tangents from the new UVs and normals so normal maps stay correct after vertices move."), s.recalculateTangents);
                    s.optimizeForRendering = EditorGUILayout.Toggle(new GUIContent("Optimize For Rendering", "MeshUtility.Optimize - reorders vertices and indices for the GPU vertex cache."), s.optimizeForRendering);

                    if (EditorGUI.EndChangeCheck())
                        OnSettingsEdited(e);
                }

                EditorGUIUtility.labelWidth = prevLabelWidth;
                EditorGUILayout.EndVertical();

                if (e.IsGenerating && Event.current.type == EventType.Repaint && body.height > 1f)
                    EditorGUI.DrawRect(body, new Color(0.1f, 0.1f, 0.1f, 0.45f));

                if (e.needsRebuild && !state.autoRebuild)
                    EditorGUILayout.LabelField(" ", "settings changed - press Generate", EditorStyles.miniLabel);

                if (e.needsRebuild && e.sourceStats.triangles > AutoRebuildTriangleLimit && state.autoRebuild)
                    EditorGUILayout.HelpBox($"Mesh is above {AutoRebuildTriangleLimit:N0} tris - auto preview is off for it. Press Generate.", MessageType.Info);
            }
        }

        /// <summary>Settings edits are sticky: the next mesh you open starts from them.</summary>
        void OnSettingsEdited(MeshEntry e)
        {
            e.touched = true;
            sticky = e.settings.Clone();
            RequestRebuild(e);
        }

        /// <summary>
        /// Explains the common "quality does nothing" case: locked edges cap the reduction long
        /// before the target is reached.
        /// </summary>
        void DrawReductionHint(MeshEntry e)
        {
            if (e.result == null || e.IsGenerating || e.sourceStats.triangles <= 0)
                return;

            var target = Mathf.Max(1, Mathf.RoundToInt(e.sourceStats.triangles * e.settings.quality));
            if (e.resultStats.triangles <= target * 1.35f)
                return;

            var blockers = new List<string>();
            if (e.settings.preserveUVSeamEdges) blockers.Add("Preserve UV Seam Edges");
            if (e.settings.preserveBorderEdges) blockers.Add("Preserve Border Edges");
            if (e.settings.vertexLinkDistance > 0.001f) blockers.Add("a large Vertex Link Distance");

            var reached = 100f * e.resultStats.triangles / e.sourceStats.triangles;
            var msg = $"Only reached {reached:0.#}% of the original ({e.resultStats.triangles:N0} tris) while asking for ~{target:N0}. " +
                      "Lowering quality further will not help: the remaining edges are locked.";
            msg += blockers.Count > 0
                ? "\nLikely cause: " + string.Join(", ", blockers) + "."
                : "\nThe mesh may already be near its minimum for this topology.";

            EditorGUILayout.HelpBox(msg, MessageType.Warning);

            if (e.settings.preserveUVSeamEdges && GUILayout.Button("Unlock UV seams and regenerate", EditorStyles.miniButton))
            {
                e.settings.preserveUVSeamEdges = false;
                OnSettingsEdited(e);
            }
        }

        static string Spinner()
        {
            var frames = new[] { "|", "/", "-", "\\" };
            return frames[(int)(EditorApplication.timeSinceStartup * 8) % frames.Length];
        }

        void DrawBottomBar()
        {
            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var pending = entries.Count(x => x.include && (x.result != null || (x.reuseCache && x.cacheEntry != null)));
                var missing = entries.Count(x => x.include && x.result == null && !(x.reuseCache && x.cacheEntry != null));
                var generating = entries.Any(x => x.IsGenerating);

                using (new EditorGUI.DisabledScope(generating))
                {
                    if (GUILayout.Button(new GUIContent("Generate All", "Simplify every included mesh in this prefab with its current settings."),
                            GUILayout.Height(26f), GUILayout.Width(110f)))
                        GenerateAll();
                }

                using (new EditorGUI.DisabledScope(pending == 0 || generating))
                {
                    var label = missing > 0
                        ? $"Apply {pending} mesh(es) & Next  ({missing} not generated)"
                        : $"Apply {pending} mesh(es) & Next";
                    if (GUILayout.Button(label, GUILayout.Height(26f)))
                        ApplyCurrent(true);
                }

                if (GUILayout.Button(new GUIContent("Skip", "Leave this prefab untouched and move on. The skip is remembered in the project database."),
                        GUILayout.Height(26f), GUILayout.Width(80f)))
                {
                    state.statuses[state.index] = (int)PrefabStatus.Skipped;
                    db = db ?? MeshOptimizerDatabase.Instance;
                    db.RecordPrefab(state.queue[state.index], "skipped", 0, 0, 0);
                    db.Save();
                    Goto(state.index + 1);
                }
            }
        }

        // ---------------------------------------------------------------- actions

        void Scan()
        {
            ClearEntries();
            state.queue.Clear();
            state.statuses.Clear();

            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { state.sourceFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Where(p => state.includeSubfolders || Path.GetDirectoryName(p).Replace('\\', '/') == state.sourceFolder)
                .Where(p => !p.StartsWith(state.outputFolder.TrimEnd('/') + "/"))
                .OrderBy(p => p)
                .ToList();

            if (state.skipPrefabsWithoutMeshes)
            {
                paths = paths.Where(p =>
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    return go != null && CollectMeshRenderers(go)
                        .Any(r => MeshStats.From(MeshOf(r)).triangles >= state.minTriangles);
                }).ToList();
            }

            db = MeshOptimizerDatabase.Reload();

            state.queue.AddRange(paths);
            state.statuses.AddRange(paths.Select(StatusFromDatabase));

            MeshOptimizeCore.EnsureFolder(state.outputFolder);
            MeshOptimizeCore.ClearProvenanceCache();
            db = MeshOptimizerDatabase.Instance;

            state.index = -1;
            if (state.queue.Count == 0)
                Debug.LogWarning($"[MeshOptimizer] No prefabs with meshes found in {state.sourceFolder}");
            else
                Goto(0);

            SaveState();
        }

        int StatusFromDatabase(string prefabPath)
        {
            var record = (db ?? MeshOptimizerDatabase.Instance).FindPrefab(prefabPath);
            if (record == null)
                return (int)PrefabStatus.Pending;

            return record.status == "applied" ? (int)PrefabStatus.Applied
                 : record.status == "skipped" ? (int)PrefabStatus.Skipped
                 : (int)PrefabStatus.Pending;
        }

        void Goto(int i)
        {
            if (state.queue.Count == 0)
                return;

            ClearEntries();
            state.index = Mathf.Clamp(i, 0, state.queue.Count - 1);
            LoadCurrent();
            SaveState();
            Repaint();
        }

        void LoadCurrent()
        {
            currentPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(state.queue[state.index]);
            entries.Clear();
            lods.Clear();
            selected = -1;
            hiddenByThreshold = 0;

            if (currentPrefab == null)
                return;

            db = db ?? MeshOptimizerDatabase.Instance;

            foreach (var group in currentPrefab.GetComponentsInChildren<LODGroup>(true))
            {
                var levels = group.GetLODs();
                lods.Add(new LodInfo
                {
                    path = TransformPath(group.transform, currentPrefab.transform),
                    name = group.gameObject.name + $" (LODGroup, {levels.Length} levels)",
                    heights = levels.Select(l => l.screenRelativeTransitionHeight).ToArray(),
                    fadeMode = group.fadeMode,
                    animateCrossFading = group.animateCrossFading,
                    rendererCounts = levels.Select(l => l.renderers?.Count(r => r != null) ?? 0).ToArray(),
                    triangleCounts = levels.Select(l => l.renderers == null
                        ? 0
                        : l.renderers.Where(r => r != null).Sum(r => MeshStats.From(MeshOf(r)).triangles)).ToArray()
                });
            }

            var colliderUse = new Dictionary<Mesh, int>();
            foreach (var mc in currentPrefab.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc.sharedMesh == null)
                    continue;

                colliderUse.TryGetValue(mc.sharedMesh, out var n);
                colliderUse[mc.sharedMesh] = n + 1;
            }

            var byMesh = new Dictionary<Mesh, MeshEntry>();
            var lodLevelOf = BuildLodLevelMap(currentPrefab);

            foreach (var r in CollectMeshRenderers(currentPrefab))
            {
                var mesh = MeshOf(r);
                if (mesh == null)
                    continue;

                if (MeshStats.From(mesh).triangles < state.minTriangles)
                {
                    if (!byMesh.ContainsKey(mesh))
                    {
                        byMesh.Add(mesh, null);
                        hiddenByThreshold++;
                    }
                    continue;
                }

                if (!byMesh.TryGetValue(mesh, out var entry) || entry == null)
                {
                    entry = BuildEntry(mesh, r);
                    entry.colliderUseCount = colliderUse.TryGetValue(mesh, out var cu) ? cu : 0;
                    byMesh[mesh] = entry;
                    entries.Add(entry);
                }

                entry.useCount++;
                if (lodLevelOf.TryGetValue(r, out var lvl) && entry.lodLevel < 0)
                {
                    entry.lodLevel = lvl;
                    entry.lodLabel = "LOD" + lvl;
                }
            }

            entries = entries.OrderByDescending(e => e.sourceStats.triangles).ToList();
            if (entries.Count > 0)
                Select(0);
        }

        MeshEntry BuildEntry(Mesh referenced, Renderer renderer)
        {
            var entry = new MeshEntry
            {
                referenced = referenced,
                source = referenced,
                referencedStats = MeshStats.From(referenced),
                label = TransformPath(renderer.transform, currentPrefab.transform),
                materials = renderer.sharedMaterials,
                skinned = renderer is SkinnedMeshRenderer
            };

            var info = MeshOptimizeCore.InspectReferencedMesh(referenced, state.outputFolder, db);

            if (info.orphanOutput)
            {
                entry.isOrphanOutput = true;
                entry.key = MeshOptimizeCore.MeshKey(referenced);
                entry.settings = sticky.Clone();
                entry.include = false;
            }
            else if (!string.IsNullOrEmpty(info.overwritePath))
            {
                // The prefab already points at one of our optimized assets: regenerate from the
                // recovered original and overwrite that same asset.
                entry.source = info.source;
                entry.key = info.produced.key;
                entry.overwritePath = info.overwritePath;
                entry.cacheEntry = info.produced;

                var recorded = MeshOptimizerDatabase.SettingsOfEntry(info.produced);
                entry.settings = (recorded ?? sticky).Clone();
                entry.touched = recorded != null;    // keep the settings this asset was made with
                entry.reuseCache = false;
                entry.include = true;
            }
            else
            {
                entry.key = MeshOptimizeCore.MeshKey(referenced);
                entry.cacheEntry = db.Find(entry.key);

                var recorded = db.SettingsOf(entry.cacheEntry);
                entry.settings = (recorded ?? sticky).Clone();
                entry.touched = recorded != null;
                entry.reuseCache = entry.cacheEntry != null;
            }

            entry.sourceStats = MeshStats.From(entry.source);
            return entry;
        }

        void Select(int i)
        {
            selected = i;
            var e = Current;
            if (e == null)
                return;

            // Carry the settings the user last tuned over to meshes they have not touched yet.
            if (!e.touched && !e.settings.SameAs(sticky))
            {
                e.settings = sticky.Clone();
                e.needsRebuild = true;
            }

            if (e.needsRebuild && !e.reuseCache && state.autoRebuild && e.sourceStats.triangles <= AutoRebuildTriangleLimit)
                dirtyStamp = EditorApplication.timeSinceStartup;
        }

        void RequestRebuild(MeshEntry e)
        {
            if (e == null)
                return;

            e.needsRebuild = true;
            dirtyStamp = EditorApplication.timeSinceStartup;
        }

        void StartJob(MeshEntry e)
        {
            if (e?.source == null)
                return;

            e.error = null;
            e.needsRebuild = false;
            e.job = MeshOptimizeCore.SimplifyJob.Start(e.source, e.settings.Clone());
        }

        /// <summary>Blocking variant used by Generate All, where a progress bar is shown anyway.</summary>
        void RebuildNow(MeshEntry e)
        {
            if (e?.source == null)
                return;

            StartJob(e);
            var job = e.job;
            e.job = null;

            DestroyIfTemporary(e.result);
            e.result = job.Complete();
            e.error = job.Error;
            e.resultStats = MeshStats.From(e.result);
            if (e.result != null)
                e.result.hideFlags = HideFlags.DontSave;
        }

        void GenerateAll()
        {
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (!e.include || e.reuseCache || !e.needsRebuild)
                        continue;

                    EditorUtility.DisplayProgressBar("Mesh Optimizer", $"Simplifying {e.source.name}", (float)i / entries.Count);
                    RebuildNow(e);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void CopySettingsToAll(MeshEntry src)
        {
            foreach (var e in entries)
            {
                if (e == src || e.reuseCache)
                    continue;

                e.settings = src.settings.Clone();
                e.touched = true;
                e.needsRebuild = true;
            }

            RequestRebuild(Current);
        }

        void ApplyCurrent(bool advance)
        {
            var prefabPath = state.queue[state.index];
            db = db ?? MeshOptimizerDatabase.Instance;

            var requests = new List<MeshOptimizeCore.ApplyRequest>();
            foreach (var e in entries)
            {
                if (!e.include || e.source == null)
                    continue;

                if (e.reuseCache && e.cacheEntry != null)
                {
                    requests.Add(new MeshOptimizeCore.ApplyRequest
                    {
                        source = e.source,
                        referenced = e.referenced,
                        reuseAssetPath = e.cacheEntry.outputPath,
                        cacheKey = e.key,
                        settings = e.settings
                    });
                    continue;
                }

                if (e.result == null)
                    continue;

                requests.Add(new MeshOptimizeCore.ApplyRequest
                {
                    source = e.source,
                    referenced = e.referenced,
                    generated = e.result,
                    overwritePath = e.overwritePath,
                    cacheKey = e.key,
                    settings = e.settings
                });
            }

            var mapping = MeshOptimizeCore.PersistMeshes(requests, state.outputFolder, db, state.updateSharedMeshInPlace);

            foreach (var e in entries)
                e.cacheEntry = db.Find(e.key) ?? e.cacheEntry;

            var lodEdits = lods.Where(l => l.changed)
                               .Select(l => new MeshOptimizeCore.LodEdit
                               {
                                   path = l.path,
                                   heights = l.heights,
                                   fadeMode = l.fadeMode,
                                   animateCrossFading = l.animateCrossFading
                               })
                               .ToList();

            var ok = MeshOptimizeCore.ApplyToPrefab(prefabPath, mapping, state.alsoReplaceColliderMeshes, lodEdits,
                out var replaced, out var error);

            state.statuses[state.index] = (int)(ok ? PrefabStatus.Applied : PrefabStatus.Failed);
            if (!ok)
                Debug.LogError($"[MeshOptimizer] Failed on {prefabPath}: {error}");

            var applied = entries.Where(e => e.referenced != null && mapping.ContainsKey(e.referenced)).ToList();
            var before = applied.Sum(e => e.sourceStats.triangles);
            var after = applied.Sum(e => MeshStats.From(mapping[e.referenced]).triangles);
            Debug.Log($"[MeshOptimizer] {Path.GetFileNameWithoutExtension(prefabPath)}: {mapping.Count} mesh(es), " +
                      $"{replaced} reference(s) re-pointed, {before:N0} -> {after:N0} tris -> {state.outputFolder}");

            if (ok)
            {
                db.RecordPrefab(prefabPath, "applied", mapping.Count, before, after);
                db.Save();
            }

            if (advance)
            {
                if (state.index < state.queue.Count - 1)
                {
                    Goto(state.index + 1);
                }
                else
                {
                    Goto(state.index);
                    Debug.Log("[MeshOptimizer] Reached the end of the queue.");
                }
            }

            SaveState();
        }

        // ---------------------------------------------------------------- helpers

        static string DefaultOutputFor(string prefabFolder)
        {
            return prefabFolder.TrimEnd('/') + "/OptimizedMeshes";
        }

        static List<Renderer> CollectMeshRenderers(GameObject root)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if ((r is MeshRenderer || r is SkinnedMeshRenderer) && MeshOf(r) != null)
                    list.Add(r);
            }
            return list;
        }

        static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr)
                return smr.sharedMesh;

            var mf = r != null ? r.GetComponent<MeshFilter>() : null;
            return mf != null ? mf.sharedMesh : null;
        }

        static Dictionary<Renderer, int> BuildLodLevelMap(GameObject root)
        {
            var map = new Dictionary<Renderer, int>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
            {
                var levels = group.GetLODs();
                for (int i = 0; i < levels.Length; i++)
                {
                    if (levels[i].renderers == null)
                        continue;

                    foreach (var r in levels[i].renderers)
                    {
                        if (r != null && !map.ContainsKey(r))
                            map.Add(r, i);
                    }
                }
            }
            return map;
        }

        static string TransformPath(Transform t, Transform root)
        {
            if (t == root)
                return "";

            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        void EnsurePreview()
        {
            if (preview == null)
                preview = new PreviewRenderUtility();
        }

        void DisposePreview()
        {
            if (preview != null)
            {
                preview.Cleanup();
                preview = null;
            }
        }

        static void DestroyIfTemporary(Mesh m)
        {
            if (m != null && !AssetDatabase.Contains(m))
                DestroyImmediate(m);
        }

        void ClearEntries()
        {
            foreach (var e in entries)
            {
                e.job = null; // abandon in-flight work; its result is simply never collected
                DestroyIfTemporary(e.result);
            }

            entries.Clear();
            lods.Clear();
            selected = -1;
            dirtyStamp = -1;
        }
    }
}
