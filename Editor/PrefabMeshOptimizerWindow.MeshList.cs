using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    // The mesh table: resizable columns, per-row status/include toggle, provenance icons.
    public partial class PrefabMeshOptimizerWindow
    {
        static readonly string[] ColumnTitles = { "mesh", "lod", "used", "tris", "verts", "sub", "cur tris", "optimized" };
        static readonly float[] DefaultColumnWidths = { 220f, 42f, 40f, 70f, 70f, 34f, 70f, 160f };
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
                widths[6] = Mathf.Max(widths[6], Measure(CurrentTrisText(e)));
                widths[7] = Mathf.Max(widths[7], Measure(StatusText(e)));
            }

            for (int i = 0; i < widths.Count; i++)
                widths[i] = Mathf.Clamp(widths[i], 30f, 600f);

            SaveState();
            Repaint();
        }

        struct RowColumns
        {
            public Rect icon, name, lod, used, tris, verts, sub, currentTris, status;
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
            c.currentTris = cells[6];
            c.status = cells[7];
            return c;
        }

        void DrawMeshRow(int i, MeshEntry e, float width)
        {
            var row = GUILayoutUtility.GetRect(width, RowHeight, GUILayout.Width(width));
            var c = Columns(row);
            var ev = Event.current;
            var rowHover = row.Contains(ev.mousePosition);
            var statusHover = c.status.Contains(ev.mousePosition);
            var lodHover = e.lod != null && c.lod.Contains(ev.mousePosition);

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

            // The lod cell doubles as a shortcut to frame the preview at this LOD's switch height,
            // so it works with the LOD Groups foldout collapsed.
            if (lodHover)
            {
                GUI.Label(c.lod, e.lodLabel ?? "", LinkStyle);
                if (ev.type == EventType.MouseDown && ev.button == 0)
                {
                    var height = e.lod.heights[e.lodLevel];
                    var pct = height * 100f;
                    lodViewFraction = Mathf.Max(0.0005f, height);
                    lodViewLabel = $"{e.lodLabel} switch ({pct:0.##}% screen height)";
                    ev.Use();
                    Repaint();
                }
            }
            else
            {
                GUI.Label(c.lod, e.lodLabel ?? "");
            }

            GUI.Label(c.used, new GUIContent(e.useCount + (e.colliderUseCount > 0 ? "*" : ""),
                e.colliderUseCount > 0
                    ? $"{e.useCount} renderer(s) and {e.colliderUseCount} MeshCollider(s) in this prefab share this mesh"
                    : $"{e.useCount} renderer(s) in this prefab use this mesh"));
            GUI.Label(c.tris, e.sourceStats.triangles.ToString("N0"));
            GUI.Label(c.verts, e.sourceStats.vertices.ToString("N0"));
            GUI.Label(c.sub, e.sourceStats.subMeshes.ToString());
            GUI.Label(c.currentTris, CurrentTrisText(e));

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

            if (ev.type == EventType.MouseDown && ev.button == 0 && rowHover && !statusHover && !lodHover)
            {
                Select(i);
                ev.Use();
                Repaint();
            }
        }

        /// <summary>
        /// What is currently applied for an already-optimized mesh: its asset path and stats.
        /// Returns false (and leaves the outputs default) when there is nothing applied yet.
        /// </summary>
        static bool TryGetAppliedInfo(MeshEntry e, out string assetPath, out MeshStats applied, out string assetName)
        {
            assetPath = null;
            applied = default;
            assetName = null;

            if (e.isOrphanOutput || !e.AlreadyOptimized)
                return false;

            assetPath = e.Reoptimizing ? e.overwritePath : e.cacheEntry.outputPath;
            applied = e.Reoptimizing ? e.referencedStats : MeshStats.From(AssetDatabase.LoadAssetAtPath<Mesh>(assetPath));
            assetName = Path.GetFileNameWithoutExtension(assetPath);
            return true;
        }

        /// <summary>Triangle count of the mesh currently applied to the prefab, or "" when none.</summary>
        static string CurrentTrisText(MeshEntry e) =>
            TryGetAppliedInfo(e, out _, out var applied, out _) ? applied.triangles.ToString("N0") : "";

        GUIContent StatusIcon(MeshEntry e)
        {
            if (e.isOrphanOutput)
                return new GUIContent(EditorGUIUtility.IconContent("console.warnicon.sml").image,
                    "Already an optimized asset, but its original is unknown (no database record). " +
                    "Optimizing it again would compound the error.");

            if (!TryGetAppliedInfo(e, out _, out var applied, out var assetName))
                return null;

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
    }
}
