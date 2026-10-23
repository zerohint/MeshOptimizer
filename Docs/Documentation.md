# Mesh Optimizer: Batch Prefab LOD & Simplify — Documentation

Mesh Optimizer is an Editor tool that reduces the triangle count of the meshes used by your
prefabs. You point it at a folder, step through the prefabs one at a time, tune each mesh against
a live original/optimized preview, and apply. The tool writes the optimized meshes as new assets
and re-points the prefabs at them. Your original meshes are never modified.

![Mesh Optimizer window](screenshoot.jpg)

## Contents

1. [Requirements](#requirements)
2. [Installation](#installation)
3. [Quick start](#quick-start)
4. [The window](#the-window)
5. [Simplification settings](#simplification-settings)
6. [LOD groups](#lod-groups)
7. [What Apply changes](#what-apply-changes)
8. [Saved data](#saved-data)
9. [Troubleshooting](#troubleshooting)
10. [Scripting](#scripting)
11. [Support](#support)
12. [Third-party software](#third-party-software)

## Requirements

- Unity 6000.0 or newer.
- Works with the Built-in Render Pipeline, URP and HDRP. It is Editor-only, adds no scripts or
  components to your build, and has no package dependencies.
- Supports `MeshRenderer` + `MeshFilter` and `SkinnedMeshRenderer`.

## Installation

**From the Asset Store:** open **Window ▸ Package Manager ▸ My Assets**, find
*Mesh Optimizer: Batch Prefab LOD & Simplify* and click **Import**. Everything goes under
`Assets/MeshOptimizer`.

**From git (UPM):** **Window ▸ Package Manager ▸ + ▸ Install package from git URL…** and enter
`https://github.com/zerohint/MeshOptimizer.git`.

You don't need to install anything else. The simplification algorithm is included.

## Quick start

1. Open **Window ▸ Asset Management ▸ Mesh Optimizer**, or right-click a folder in the Project
   window and choose **Optimize Prefab Meshes…**.
2. Set **Prefab Folder** to the folder that holds your prefabs. **Output Folder** defaults to
   `<Prefab Folder>/OptimizedMeshes`. You can change it.
3. Click **Start Optimizing**. The tool scans the folder and builds a queue of prefabs.
4. The first prefab opens. Its meshes are listed with triangle and vertex counts. Select a mesh,
   move the **Quality** slider and watch the preview. Press **Smart** to let the tool pick a
   quality level for you.
5. When the result looks right, click **Apply N mesh(es) & Next**. The meshes are written, the
   prefab is updated and the next prefab opens. Use **Skip** to leave a prefab as it is.

You can close the window at any time. Your progress is saved and **Resume** takes you back to
the first prefab that is still pending.

## The window

### Folder and queue options

| Option | What it does |
| --- | --- |
| **Prefab Folder** | The folder to scan. Drag a folder in or use **Browse**. |
| **Output Folder** | Where optimized mesh assets are written. |
| **Include subfolders** | Also scan folders inside the prefab folder. |
| **Skip empty prefabs** | Leave prefabs with no qualifying mesh out of the queue. |
| **Skip meshes under [tris]** | Meshes with fewer triangles than this are not listed and never changed. `0` lists everything. |
| **Also swap MeshColliders** | When a `MeshCollider` uses the same mesh as a renderer you optimize, point it at the optimized mesh too. Rows marked with `*` share their mesh with a collider. Colliders that use their own collision mesh are never touched. |
| **Reuse shared meshes** | A mesh used by several prefabs is generated once. Later prefabs reuse it, and re-tuning it overwrites that asset in place. |
| **Start Optimizing / Re-scan Folder** | Build the queue, or rebuild it after you add prefabs. |
| **Stop** | Close the current queue. Saved progress is kept. |

### Queue toolbar

The progress line shows the current position (for example `93 / 276`) and how many prefabs are
done, skipped and left.

| Button | What it does |
| --- | --- |
| **Resume** | Jump to the first prefab that is still pending. |
| **< Prev / Next >** | Move through the queue without applying anything. |
| **Reload** | Reload the current prefab from disk and drop unapplied previews. |
| **Select in Project** | Highlight the current prefab in the Project window. |
| **Queue** | Show the full queue with a status icon per prefab: applied, skipped or failed. Click an entry to jump to it. |

### Mesh list

One row per unique mesh in the current prefab: name, how many renderers use it, triangles,
vertices, submeshes, current triangles, optimized result and LOD level. **Auto Size** fits the
columns to their contents and **Reset** restores the default widths.

- Click **Cancel** on a row to leave that mesh out of Apply, and **Include** to bring it back.
- A check mark means the mesh already has an optimized version from an earlier run.
- **LOD View** frames the preview the way the mesh looks at its LOD switch distance. This is a
  quick way to see whether a reduction will be noticeable in game. Click **exit** in the preview
  toolbar to go back.

### Preview

| Control | What it does |
| --- | --- |
| **Side By Side / Stacked / Original / Optimized** | Layout of the two previews. |
| **Shaded / Wireframe / ShadedWireframe / Unlit** | How the mesh is drawn. Wireframe is the clearest way to see where triangles were removed. |
| **Rotate: Off / Spin / Orbit** | Turn the model automatically. |
| **Stats** | Show triangle and vertex counts on the previews. |
| **Reset** | Restore the default camera. |

Drag inside the preview to orbit and use the scroll wheel to zoom. Both previews move together.

### Settings panel and actions

| Button | What it does |
| --- | --- |
| **Auto** | Regenerate the preview shortly after you stop changing a setting. Turn it off for very heavy meshes and use **Generate** instead. |
| **Generate** | Simplify the selected mesh now. The result is only a preview until you apply it. |
| **Copy to all** | Copy the current settings to every other mesh in this prefab. |
| **Smart** | Search for the lowest quality that still looks the same, and use it. Press it again to go back to your previous value. |
| **Generate All** | Simplify every included mesh in the prefab. |
| **Apply N mesh(es) & Next** | Write the results, update the prefab and open the next one. |
| **Skip** | Leave this prefab unchanged and move on. The skip is remembered. |

Generation runs on a background thread, so the Editor stays responsive while you move sliders.

## Simplification settings

Each mesh has its own settings.

| Setting | Default | Notes |
| --- | --- | --- |
| **Quality** | 0.5 | Fraction of triangles to keep. `0.4` keeps about 40%. The target triangle count is shown under the slider. |
| **Preserve Border Edges** | on | Keeps open borders in place. Keep it on for planes, fences, foliage cards and other open meshes that shrink or lose their outline. Turning it off lets closed hard-surface meshes reduce further. |
| **Preserve UV Seam Edges** | off | Keeps UV seams. On hard-surface models with per-face UVs nearly every edge is a seam, so this can stop the mesh from shrinking at all. |
| **Preserve UV Foldover Edges** | off | Keeps edges where the UV layout folds over. |
| **Preserve Surface Curvature** | on | Prefers keeping curved detail over flat areas. |
| **Enable Smart Link** | on | Joins vertices that sit in the same place but are separate, which closes cracks along hard edges and UV seams. |
| **Vertex Link Distance** | 0 | How close vertices must be to be joined. `0` means "exactly the same position". Small values such as `0.0001` weld split vertices. Large values can stop the mesh from shrinking. |
| **Aggressiveness** | 7 | Higher removes more per pass, with lower quality. |
| **Max Iterations** | 100 | Upper limit on simplification passes. |
| **Manual UV Component Count** / **UV Components** | off / 2 | Force UVs to 1–4 components instead of detecting them. |
| **Recalculate Normals** | off | Rebuilds normals. Leave it off for hard-surface models, because it smooths across hard edges. |
| **Recalculate Tangents** | on | Rebuilds tangents so normal maps stay correct on the new mesh. |
| **Optimize For Rendering** | on | Reorders vertices and indices for the GPU. |

**The mesh will not get smaller?** If Quality has no effect, a preservation option is usually
blocking it. The window detects this, names the option that is blocking it and offers
**Unlock UV seams and regenerate**.

## LOD groups

When a prefab has an `LODGroup`, its levels appear above the mesh list. You can change
**Fade Mode**, **Animate Cross-fading** and the transition height of each level. The
**Culled below** line shows the screen height at which the object disappears. Edits are marked
*modified – saved on Apply* and are written to the prefab together with the meshes.

Each mesh shows its LOD level in the mesh list. A common workflow is to keep LOD0 high quality
and reduce the lower levels more.

## What Apply changes

- Each optimized mesh is saved as a new `.asset` file in the Output Folder, named
  `<original name>_opt.asset`.
- Every `MeshFilter` and `SkinnedMeshRenderer` in the prefab that used the original mesh is
  pointed at the new asset. If **Also swap MeshColliders** is on, matching colliders are updated
  too.
- LOD group edits are saved to the prefab.
- **The original mesh and model files are never modified or deleted.**

**Re-tuning:** if you open a prefab that already uses an optimized mesh, the tool finds the
original from its saved record. It simplifies the original again and overwrites the same `_opt`
asset. It doesn't stack a second reduction on top of the first.

**Undoing:** point the renderer back at the original mesh, or revert the prefab with version
control. Prefab saves made by Apply are not recorded in Unity's Undo history.

## Saved data

| Where | What |
| --- | --- |
| `ProjectSettings/MeshOptimizer.json` | Which original produced which optimized asset, the settings used, and the applied/skipped state of each prefab, keyed by GUID so it survives renames. Plain JSON, safe to commit. |
| `EditorPrefs` | Your own preferences: folders, pane sizes, column widths and position in the queue. |

To start again from scratch, delete `ProjectSettings/MeshOptimizer.json`.

## Troubleshooting

| Problem | What to do |
| --- | --- |
| Quality has no effect | Turn off **Preserve UV Seam Edges** and **Preserve Border Edges**, or use the unlock button the window shows. |
| Cracks or holes appear | Turn on **Enable Smart Link** and set a small **Vertex Link Distance** such as `0.0001`. |
| Planes or cards shrink | Turn on **Preserve Border Edges**. |
| Shading looks faceted or too smooth | Turn **Recalculate Normals** off. |
| Normal map looks wrong | Turn **Recalculate Tangents** on. |
| "Already an optimized asset but not in the cache" | The mesh was made by this tool, but its record was deleted. Simplifying it again reduces an already reduced mesh. Point the prefab back at the original first. |
| Error mentioning Read/Write | Enable **Read/Write** in the model's import settings and try again. |
| A prefab shows as failed in the queue | The reason is in the Console, prefixed with `[MeshOptimizer]`. |

## Scripting

The simplification and asset-writing code has no UI and can be called from your own Editor
scripts. See `MeshOptimizer.MeshOptimizeCore` and `MeshOptimizer.OptimizeSettings` in
`Editor/`. All code lives in the `MeshOptimizer` namespace.

## Support

- Issues and questions: <https://github.com/zerohint/MeshOptimizer/issues>
- Source code: <https://github.com/zerohint/MeshOptimizer>

## Third-party software

The simplification algorithm is
[UnityMeshSimplifier](https://github.com/Whinarn/UnityMeshSimplifier) 3.1.0 by Mattias Edlund
(MIT License). It is based on
[Fast-Quadric-Mesh-Simplification](https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification)
by Sven Forstmann (MIT License). The code is included in `Editor/ThirdParty/UnityMeshSimplifier`
and was moved into the `MeshOptimizer.Simplifier` namespace so it doesn't conflict with a
separately installed copy. See `Third-Party Notices.txt` for the full license texts.
