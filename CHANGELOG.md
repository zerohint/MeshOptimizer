# Changelog

All notable changes to this package are documented here.

## [1.0.0]

### Added
- Prefab-by-prefab optimization queue with progress that survives closing the window.
- Live original/optimized preview (stacked or side by side) with orbit, zoom, wireframe,
  auto-rotate and an LOD View that frames a mesh at a LOD level's switch size.
- Full UnityMeshSimplifier option set per mesh, generated on a worker thread with a
  debounce so sliders stay responsive.
- LOD group editing: fade mode, cross-fading and per-level transition heights.
- Resizable, reorderable-width mesh table with auto-sizing columns.
- Project-wide record in `ProjectSettings/MeshOptimizer.json`: mesh provenance
  (source -> optimized asset plus the settings used) and per-prefab progress.
- Shared meshes are generated once and re-pointed; re-tuning overwrites the same asset
  instead of producing `_opt_opt` chains.
- UnityMeshSimplifier 3.1.0 (MIT) is bundled under `Editor/ThirdParty` in the
  `MeshOptimizer.Simplifier` namespace, so the package works right after import.
- Offline manual in `Docs/Documentation.md`.
