using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    // Setup panel, queue progress bar/list, and the LOD groups section.
    public partial class PrefabMeshOptimizerWindow
    {
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
                        var label = $"LOD{i}  ({lod.rendererCounts[i]} rend, {lod.triangleCounts[i]:N0} tris)";
                        lod.heights[i] = EditorGUILayout.Slider(label, lod.heights[i], 0f, 1f);
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
    }
}
