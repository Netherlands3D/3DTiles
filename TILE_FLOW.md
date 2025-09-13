# 3D Tiles Loading and Disposal Flow

## Overview
This document describes the complete flow of loading and disposing 3D tiles in the Netherlands3D.Tiles3D package, including memory management and error handling strategies.

## Architecture Components

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

## Memory Management Strategy

### WebGL-Specific Optimizations
1. **Immediate Disposal** (no delayed queue)
2. **Explicit WebGL Buffer Release** via `mesh.UploadMeshData(true)`
3. **Comprehensive Texture Cleanup** across all shader properties
4. **Periodic Resources.UnloadUnusedAssets()**
5. **Conservative GC.Collect()** (only every 100 frames)

### Async Safety Mechanisms
1. **CancellationToken propagation** through entire loading chain
2. **Multiple validation layers** before GLTFast calls
3. **Exception wrapping** to convert Unity null references
4. **State checking** (Content.State == NOTLOADING)
5. **Early disposal** of GltfImport before cancellation

---

## Error Handling Strategy

### Expected Graceful Messages
- ✅ `"Content disposed right before scene X instantiation"`
- ✅ `"Operation was cancelled (this is normal during disposal)"`
- ✅ `"GLB processing was cancelled (Content disposed)"`

### Handled Exceptions
- `ObjectDisposedException` → gltfImport was disposed
- `NullReferenceException` → GameObject was destroyed
- `MissingReferenceException` → Unity detected destroyed object
- `OperationCanceledException` → Normal cancellation

### Critical Errors (should not occur)
- ❌ `"SpawnGltfScenes: Error instantiating scene 0: The object... has been destroyed"`

---

## Performance Considerations

### Loading Optimizations
- **Progressive loading** based on camera distance/priority
- **Async/await** pattern for non-blocking loading
- **Early mesh upload** to GPU during loading

### Memory Optimizations
- **Immediate disposal** instead of delayed queuing
- **WebGL buffer management** with explicit cleanup
- **Texture reference tracking** and safe destruction
- **Component-level cleanup** before GameObject destruction

### Threading Safety
- **Main thread requirement** for Unity API calls
- **CancellationToken** for cross-thread coordination
- **ConfigureAwait(false)** where appropriate

---

## Debugging Guidelines

### Memory Leak Investigation
1. Check browser Memory tab for TypedArray growth
2. Monitor WebGLBuffer/WebGLTexture counts
3. Verify `gltfImport.Dispose()` is called
4. Check for exceptions during disposal

### Null Reference Debugging
1. Verify cancellation token propagation
2. Check Content.State transitions
3. Monitor disposal timing vs async operations
4. Validate exception handling coverage

### Performance Debugging
1. Profile async operation timing
2. Monitor GC pressure and frequency
3. Check mesh/texture memory usage
4. Validate WebGL resource cleanup

---

## Future Improvements

### Potential Optimizations
- [ ] **Object pooling** for frequently loaded/unloaded tiles
- [ ] **Shared material instances** to reduce memory duplication
- [ ] **LOD system** for distance-based quality reduction
- [ ] **Streaming texture compression** for WebGL

### Architecture Improvements
- [ ] **Unified importer interface** to reduce ImportGlb/ImportGltf duplication
- [ ] **Event-driven disposal** instead of polling-based detection
- [ ] **Resource tracking system** for detailed memory analysis
- [ ] **Configurable cleanup strategies** per platform

---

## Version History

### Current Version (September 2025)
- ✅ Implemented comprehensive cancellation token system
- ✅ Added SafeInstantiateSceneAsync wrapper
- ✅ Improved disposal timing (gltfImport first)
- ✅ Enhanced WebGL resource cleanup
- ✅ Removed delayed disposal queue complexity
- ✅ Removed legacy DisposeNativeArrays() calls (modern GLTFast handles automatically)
- ✅ Maintained unified architecture (Content.cs handles all loading/processing)

### Previous Issues Resolved
- ❌ Memory heap growth in WebGL builds
- ❌ Null reference exceptions during tile deletion
- ❌ Race conditions between async loading and disposal
- ❌ Incomplete WebGL buffer cleanup
