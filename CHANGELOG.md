# Changelog

All notable changes to this package are documented here.

## [1.2.0]

### Added
- Demo scene and sample assets in `Demo/`: dense procedurally generated models, prefabs that
  show shared meshes, an LOD group and multi-material meshes, and `MeshOptimizerDemo.unity`.
  The demo renders the same in the Built-in Render Pipeline and URP.

## [1.1.0]

### Added
- Offline manual in `Docs/Documentation.md`, also linked from `package.json`.
- `Third-Party Notices.txt` with the license texts of the bundled code.

### Changed
- Mesh Simplifier 3.1.0 by Mattias Edlund (MIT) is bundled under `Editor/ThirdParty` in the
  `MeshOptimizer.Simplifier` namespace, so the package works right after import and no
  longer needs a manual `Packages/manifest.json` entry. It does not clash with a copy
  that is already installed.

## [1.0.0]

### Added
- Prefab-by-prefab optimization queue with progress that survives closing the window.
- Live original/optimized preview (stacked or side by side) with orbit, zoom, wireframe,
  auto-rotate and an LOD View that frames a mesh at a LOD level's switch size.
- Full simplifier option set per mesh, generated on a worker thread with a
  debounce so sliders stay responsive.
- LOD group editing: fade mode, cross-fading and per-level transition heights.
- Resizable, reorderable-width mesh table with auto-sizing columns.
- Project-wide record in `ProjectSettings/MeshOptimizer.json`: mesh provenance
  (source -> optimized asset plus the settings used) and per-prefab progress.
- Shared meshes are generated once and re-pointed; re-tuning overwrites the same asset
  instead of producing `_opt_opt` chains.
