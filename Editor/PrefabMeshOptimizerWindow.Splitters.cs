using UnityEditor;
using UnityEngine;

namespace MeshOptimizer
{
    // Draggable dividers between the stacked panes and between preview/settings columns.
    public partial class PrefabMeshOptimizerWindow
    {
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
    }
}
