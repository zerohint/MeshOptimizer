using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    // The original/optimized 3D preview panel and its camera controls.
    public partial class PrefabMeshOptimizerWindow
    {
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
    }
}
