Mesh Optimizer - Demo
=====================

1. Open MeshOptimizerDemo.unity in this folder.
2. Open Window > Asset Management > Mesh Optimizer.
3. Set Prefab Folder to Assets/MeshOptimizer/Demo/Prefabs
   (or right-click the Prefabs folder > Optimize Prefab Meshes...).
4. Click Start Optimizing, tune each mesh against the preview, then Apply & Next.

What each prefab shows:
- Sphere         : a smooth 18k-triangle mesh.
- Rock_A, Rock_B : two prefabs sharing one mesh (Rock_B also uses it as a MeshCollider).
                   It is optimized once and both prefabs are re-pointed.
- TorusKnot_LOD  : an LODGroup with three levels; LOD1 and LOD2 are deliberately too dense.
- Barrel         : one mesh with three sub-meshes and materials.

Optimized meshes are written to Demo/Prefabs/OptimizedMeshes. Your original models are not changed.
All models are generated procedurally and are free to use.
The demo uses its own "Mesh Optimizer/Demo Lit" shader, so it renders the same in Built-in and URP.
