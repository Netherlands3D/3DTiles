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

### Core Classes
- **`Content.cs`** - Main content loader, disposal manager, content downloading/processing, and GLTF scene instantiation/positioning
- **`Tile.cs`** - Tile metadata and hierarchy
- **`ImportB3dm.cs`** - B3DM content importer
- **`ImportGlb.cs`** - GLB content importer
- **`ImportGltf.cs`** - GLTF URI content importer
- **`WebTilePrioritiser.cs`** - WebGL-optimized tile priority calculator and download queue manager

---

## Loading Flow

### 1. Tileset Initialization
```
Application Start
↓
Read3DTileset.LoadTileset()
├── Download tileset.json
├── Parse JSON structure
├── Create root Tile objects
└── Initialize tile hierarchy
```

### 2. Tile Tree Creation (`Tile.cs`)
```
Tile Constructor
├── Parse tile metadata (boundingVolume, geometricError, etc.)
├── Create child tiles if present (Quadtree/Octree structure)
├── Initialize Content object if content URI exists
│   └── new Content(contentUri, parentTile)
└── Set up tile hierarchy relationships
```

### 3. Tile Lifecycle Management (`WebTilePrioritiser`)
```
User Navigation/Camera Movement
↓
WebTilePrioritiser.LateUpdate()
├── CalculatePriorities() (screen-space error + center priority)
├── Apply() - Manage download queue
├── Check tile visibility and distance
├── Limit to maxSimultaneousDownloads (WebGL constraint)
└── RequestLoad() → Content.Load() for highest priority tiles
```

### 4. Content Loading (`Content.cs`)
```
Content.Load()
├── State = DOWNLOADING
├── Create CancellationTokenSource
├── Start Coroutine: Content.DownloadContent()
└── Callback: DownloadedData()
```

### 5. Content Processing (`Content.cs`)
```
Content.ProcessDownloadedData()
├── Detect content type (B3DM/GLB/GLTF)
├── Route to appropriate importer:
│   ├── ImportB3dm.LoadB3dm()
│   ├── ImportGlb.Load()
│   └── ImportGltf.Load()
└── Pass CancellationToken through chain
```

### 6. GLTF Import and Scene Instantiation
```
ImportGlb.Load() / ImportB3dm.LoadB3dm() / ImportGltf.Load()
├── Create/Reuse `GltfImport` via `Content.GltfImportObject`
├── `gltf.Load(...)` with CancellationToken
├── `Content.SpawnGltfScenesAsync(...)` with CancellationToken
└── `Content` manages `GltfImport` lifecycle and disposal
```

### 7. Scene Spawning (`Content.cs`)
```
SpawnGltfScenesAsync()
├── For each scene in GLTF:
│   ├── Validate parent Transform exists
│   ├── Check Content.State != NOTLOADING
│   ├── Check CancellationToken
│   ├── SafeInstantiateSceneAsync() wrapper
│   │   ├── Final validation checks
│   │   ├── gltfImport.InstantiateSceneAsync()
│   │   └── Exception handling (NullRef, MissingRef)
│   ├── Set GameObject layers
│   └── Position with RTC_CENTER offset
└── Content.State = DOWNLOADED
```

---

## Disposal Flow

### 1. Disposal Trigger
```
User clicks LayerToolBarButtonDeleteLayer
↓
WebTilePrioritiser.DisposeImmediately() (immediate)
↓
Content.Dispose()
```

### 2. Early Cancellation (`Content.cs`)
```
Content.Dispose()
├── 1. State = NOTLOADING (blocks new async ops)
├── 2. gltfImport.Dispose() (stops GLTFast ASAP)
├── 3. cancellationTokenSource.Cancel()
├── 4. Stop running coroutines
└── 5. Component cleanup...
```

### 3. Async Operation Safety
```
Running SpawnGltfScenes()
├── Check cancellationToken.IsCancellationRequested
├── Check Content.State == NOTLOADING → return early
├── SafeInstantiateSceneAsync() catches:
│   ├── ObjectDisposedException (gltfImport disposed)
│   ├── NullReferenceException (destroyed GameObject)
│   └── MissingReferenceException (Unity destroyed detection)
└── Log graceful cancellation messages
```

### 4. WebGL Resource Cleanup
```
Content.Dispose() continued...
├── Clear MeshFilter.sharedMesh/mesh
│   ├── ClearMeshWebGLResources() → mesh.UploadMeshData(true)
│   ├── mesh.Clear()
│   └── Object.Destroy(mesh)
├── Clear Renderer materials
│   ├── ClearAllTexturesFromMaterial()
│   ├── ClearTexture2DWebGLResources()
│   └── Object.Destroy(material)
├── Clear MeshCollider references
├── Destroy GameObject
└── StartCoroutine(ForceMemoryCleanup())
```

---
