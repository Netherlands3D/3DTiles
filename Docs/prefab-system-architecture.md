# Prefab System Architecture

This document explains how the 3D Tiles system uses Unity's prefab system to manage different types of tile layers and their components.

## Overview

The 3D Tiles system uses a sophisticated prefab-based architecture to automatically instantiate the correct components for different types of tile layers (Google Reality Mesh, regular 3D Tiles, etc.). This system ensures that layer-specific components like `ProgressiveRefine3DTiles` are automatically attached when needed.

## Architecture Components

### 1. PrefabLibrary System

The core of the system is the `PrefabLibrary.asset` ScriptableObject that contains organized groups of prefabs:

```
PrefabLibrary.asset
├── Basislagen (Basic Layers)
├── ObjectenBibliotheek (Object Library) 
├── 3DTiles
│   ├── Regular 3D Tiles prefab
│   ├── Google Reality Mesh prefab (GUID: 71fff5c85650fcd4d9f59e4e182b5e95)
│   └── Other tile variants
└── CustomLayers
```

**Location**: `Assets/Scriptables/PrefabLibrary.asset`

### 2. Service Registration

The PrefabLibrary is connected to the layer system through the main scene:

```
Main.unity scene
└── "Netherlands3D" GameObject  
    └── Layers.cs component (Service)
        └── [SerializeField] prefabLibrary = PrefabLibrary.asset
```

The `Layers.cs` component is registered as a service through the `ServiceLocator` pattern, making it accessible via `App.Layers`.

### 3. Layer Creation Flow

When a layer is created, the following sequence occurs:

1. **Layer Request**: Code calls `App.Layers.Add()` with a prefab identifier
2. **Service Lookup**: `App.Layers` resolves to `ServiceLocator.GetService<Layers>()`
3. **Prefab Resolution**: `prefabLibrary.GetPrefabById(prefabIdentifier)` finds the correct prefab
4. **Instantiation**: Unity instantiates the prefab with ALL its pre-configured components
5. **Component Activation**: Components like `ProgressiveRefine3DTiles` automatically start via `Awake()`

## Google Reality Mesh Example

### Prefab Configuration

The "Fotorealistische wereld (Google 3D Tiles)" prefab contains these components:

- `Transform`
- `Read3DTileset` (core tile loading)
- `ReadSubtree` (implicit tiling support)
- `GoogleRealityMeshConfigurationAdapter` (API key management)
- **`ProgressiveRefine3DTiles`** (LOD refinement behavior)
- `Tile3DLayerGameObject` (layer system integration)
- Other utility components

**Key Point**: The `ProgressiveRefine3DTiles` component is **pre-attached** to the prefab, not added dynamically.

### Component Settings

In the prefab, `ProgressiveRefine3DTiles` has these default values:
```csharp
idleMaximumScreenSpaceError: 10
movingMaximumScreenSpaceError: 40  
detailIncrementStepPerFrame: 1
```

### Layer Identification

The system knows to use the Google Reality Mesh prefab through:

1. **Prefab Identifier**: Each prefab has a unique GUID-based identifier
2. **Layer Creation**: Code specifies which prefab to use via `layerBuilder.OfType(prefabIdentifier)`
3. **Automatic Matching**: The PrefabLibrary finds the matching prefab and instantiates it

## Layer Preset System

For common layer types, the system uses Layer Presets:

```csharp
[LayerPreset("3d-tiles")]
public sealed class OGC3DTiles : ILayerPreset
{
    private const string PrefabIdentifier = "395dd4e52bd3b42cfb24f183f3839bba";
    
    public ILayerBuilder Apply(ILayerBuilder builder, LayerPresetArgs args)
    {
        return builder
            .OfType(PrefabIdentifier)  // This selects the prefab!
            .AddProperty(new Tile3DLayerPropertyData(args.Url));
    }
}
```

Usage:
```csharp
await App.Layers.Add("3d-tiles", new OGC3DTiles.Args("https://example.com/tileset.json"));
```

## Adding Custom Components

To add custom behavior to specific tile types:

### Method 1: Modify Existing Prefab
1. Open the prefab in the editor
2. Add your custom component
3. Configure default values
4. Save the prefab

### Method 2: Create New Prefab Variant
1. Create a prefab variant of an existing tile prefab
2. Add/modify components as needed
3. Add the new prefab to `PrefabLibrary.asset`
4. Create a new Layer Preset or use the prefab identifier directly

## Debugging Prefab Usage

To debug which prefab is being used:

1. **Check PrefabIdentifier**: Look at the GUID being passed to `OfType()`
2. **Inspect PrefabLibrary**: Find the GUID in `PrefabLibrary.asset`
3. **Component Verification**: Check if the instantiated GameObject has expected components
4. **ServiceLocator**: Verify the `Layers` service is properly registered

## Common Issues

### Missing Components
- **Cause**: Component not attached to prefab
- **Solution**: Edit the prefab and add the missing component

### Wrong Prefab Used
- **Cause**: Incorrect prefab identifier in code
- **Solution**: Verify the GUID matches the intended prefab in PrefabLibrary

### Service Not Found
- **Cause**: `Layers` service not registered or scene not loaded
- **Solution**: Ensure Main.unity scene is loaded and "Netherlands3D" GameObject is active

## Files Reference

| File | Purpose |
|------|---------|
| `Assets/Scriptables/PrefabLibrary.asset` | Contains all prefab references organized by category |
| `Assets/Scenes/Main.unity` | Contains the "Netherlands3D" GameObject with Layers service |
| `Assets/_Application/Services/Layers.cs` | Core layer management service |
| `Assets/_Application/Layers/LayerSpawner.cs` | Handles prefab instantiation |
| `Assets/_Application/Projects/PrefabLibrary.cs` | PrefabLibrary ScriptableObject class |
| Various Layer Preset classes | Define preset configurations for common layer types |

## Best Practices

1. **Use Layer Presets** for common configurations instead of hardcoding prefab identifiers
2. **Pre-configure components** on prefabs rather than adding them dynamically
3. **Test component settings** in the prefab editor before using in production
4. **Document custom prefabs** and their intended use cases
5. **Use meaningful names** for prefab identifiers when creating new layer types

This architecture provides a clean separation between layer logic and prefab configuration, making it easy to add new tile types and behaviors without modifying core system code.