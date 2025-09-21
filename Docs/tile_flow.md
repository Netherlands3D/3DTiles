# Tile Flow

This document describes the architecture and flow of tile loading in the 3DTiles Unity package.

## Overview
- Tiles are loaded on demand based on camera position and screen-space error (SSE).
- The loader supports both explicit and implicit tiling (quadtree/octree).
- Subtrees and content are fetched as needed, never loading the entire tree upfront.

## Tile Loading Steps
1. **Initialization**: The root tile is created from the tileset JSON.
2. **Visibility Check**: Each frame, visible tiles are determined based on the camera frustum and SSE.
3. **Subtree/Content Fetch**: If a tile is in view and not yet loaded, its subtree or content is requested.
4. **Recursive Refinement**: If more detail is needed, child tiles are loaded recursively.
5. **Disposal**: Tiles outside the view or with sufficient parent detail are disposed to save memory.

## Key Concepts
- **Screen-Space Error (SSE)**: Controls LOD switching and tile refinement.
- **BoundingVolume**: Used for frustum culling and SSE calculation.
- **Implicit Tiling**: Uses templates and rules to generate tile addresses and bounding volumes on the fly.

## References
- [3D Tiles Spec](https://github.com/CesiumGS/3d-tiles/tree/main/specification)
- [Implicit Tiling](https://github.com/CesiumGS/3d-tiles/tree/main/specification/ImplicitTiling)

---

*This document is a living reference and may be updated as the implementation evolves.*
