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

                state.shadingMode = (int)(ShadingMode)EditorGUILayout.EnumPopup((ShadingMode)state.shadingMode, EditorStyles.toolbarPopup, GUILayout.Width(110f));

                if (GUILayout.Button(RotateButtonContent((RotateMode)state.rotateMode), EditorStyles.toolbarButton, GUILayout.Width(90f)))
                    state.rotateMode = (state.rotateMode + 1) % 3;

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

                var dropped = TrisDroppedLabel(e);
                if (dropped != null)
                    GUILayout.Label(dropped, EditorStyles.miniBoldLabel);
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
                case PreviewMode.Stacked:
                    Rect first, second;
                    if ((PreviewMode)state.previewMode == PreviewMode.Stacked)
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

        static GUIContent RotateButtonContent(RotateMode mode)
        {
            switch (mode)
            {
                case RotateMode.Spin:
                    return new GUIContent("Rotate: Spin", "Spins around the vertical axis. Click to orbit every axis instead.");
                case RotateMode.Orbit:
                    return new GUIContent("Rotate: Orbit", "Continuously orbits so every side passes through view. Click to turn rotation off.");
                default:
                    return new GUIContent("Rotate: Off", "Click to spin the preview around the vertical axis.");
            }
        }

        /// <summary>Percentage of triangles removed by the current result, or null if there is nothing to show yet.</summary>
        string TrisDroppedLabel(MeshEntry e)
        {
            if (e == null || e.sourceStats.triangles <= 0)
                return null;

            var after = OptimizedStats(e);
            if (after.triangles <= 0)
                return null;

            var pct = 100f * (1f - (float)after.triangles / e.sourceStats.triangles);
            return $"{pct:0.#}% tris dropped";
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

                var shading = (ShadingMode)state.shadingMode;
                bool drawShaded = shading != ShadingMode.Wireframe;
                bool drawWire = shading == ShadingMode.Wireframe || shading == ShadingMode.ShadedWireframe;

                if (drawShaded)
                {
                    GL.wireframe = false;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        Material mat;
                        if (shading == ShadingMode.Unlit)
                        {
                            mat = UnlitMaterial;
                        }
                        else
                        {
                            mat = materials != null && materials.Length > 0
                                ? materials[Mathf.Min(s, materials.Length - 1)]
                                : null;
                            mat = mat ?? FallbackMaterial;
                        }
                        preview.DrawMesh(mesh, Matrix4x4.identity, mat, s);
                    }
                }

                if (drawWire)
                {
                    GL.wireframe = true;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        preview.DrawMesh(mesh, Matrix4x4.identity, WireOverlayMaterial, s);
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

        static Material UnlitMaterial
        {
            get
            {
                if (unlitMaterial == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                    unlitMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    if (unlitMaterial.HasProperty("_BaseColor"))
                        unlitMaterial.SetColor("_BaseColor", new Color(0.6f, 0.6f, 0.6f));
                    else if (unlitMaterial.HasProperty("_Color"))
                        unlitMaterial.SetColor("_Color", new Color(0.6f, 0.6f, 0.6f));
                }
                return unlitMaterial;
            }
        }

        static Material WireOverlayMaterial
        {
            get
            {
                if (wireOverlayMaterial == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                    wireOverlayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    if (wireOverlayMaterial.HasProperty("_BaseColor"))
                        wireOverlayMaterial.SetColor("_BaseColor", Color.black);
                    else if (wireOverlayMaterial.HasProperty("_Color"))
                        wireOverlayMaterial.SetColor("_Color", Color.black);
                }
                return wireOverlayMaterial;
            }
        }
    }
}
