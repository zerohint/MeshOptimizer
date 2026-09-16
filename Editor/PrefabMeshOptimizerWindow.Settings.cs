using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    // The per-mesh simplifier settings panel, the reduction hint, and the bottom action bar.
    public partial class PrefabMeshOptimizerWindow
    {
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
    }
}
