using System.Collections.Generic;
using UnityEngine;
using System.Threading.Tasks;
using GLTFast;
using System;
using System.Text.RegularExpressions;
using System.Text;
using Newtonsoft.Json;
using Netherlands3D.Coordinates;
using SimpleJSON;


#if SUBOBJECT
using Netherlands3D.SubObjects;
#endif

namespace Netherlands3D.Tiles3D
{
    [Serializable]
    public class ParsedGltf
    {
        public GltfImport gltfImport;
        public byte[] glbBuffer;
        public double[] rtcCenter = null;
        public CoordinateSystem coordinatesystem;

        public Dictionary<int, Color> uniqueColors = new Dictionary<int, Color>();
        
        // Reusable collections to prevent heap allocations during subobject parsing
        private List<Vector2Int> reusableVertexFeatureIds = new List<Vector2Int>();
        private List<string> reusableBagIdList = new List<string>();
        private List<ObjectMappingItem> reusableObjectMappingItems = new List<ObjectMappingItem>();

        JSONNode gltfJsonRoot = null;

        public bool isSupported()
        {
            ReadGLTFJson();
            if (gltfJsonRoot==null)
            {
                Debug.LogError("gltf doesn't contain a valid JSON");
                return false;
            }

            JSONNode extensionsRequiredNode = gltfJsonRoot["extensionsRequired"];
            if (extensionsRequiredNode == null)
            {
                return true;
            }
            int extensionsRequiredCount = extensionsRequiredNode.Count;
            int cesiumRTCIndex = -1;
            for (int ii = 0; ii < extensionsRequiredCount; ii++)
            {
                if (extensionsRequiredNode[ii].Value == "CESIUM_RTC")
                {
                    cesiumRTCIndex = ii;
                    continue;
                }

            }
            if (cesiumRTCIndex < 0)
            {
                return true ;
            }


            return false;
        }

        public List<int> featureTableFloats = new List<int>();
        Transform parentTransform;
        Tile tile;
        /// <summary>
        /// Iterate through all scenes and instantiate them
        /// </summary>
        /// <param name="parent">Parent spawned scenes to Transform</param>
        /// <returns>Async Task</returns>
        /// 
        private void ReadGLTFJson()
        {
            int jsonstart = 20;
            int jsonlength = (glbBuffer[15]) * 256;
            jsonlength = (jsonlength + glbBuffer[14]) * 256;
            jsonlength = (jsonlength + glbBuffer[13]) * 256;
            jsonlength = (jsonlength + glbBuffer[12]);

            string gltfjsonstring = Encoding.UTF8.GetString(glbBuffer, jsonstart, jsonlength);


            if (gltfjsonstring.Length > 0)
            {

                gltfJsonRoot = JSON.Parse(gltfjsonstring);
            }
        }


        public async Task SpawnGltfScenes(Transform parent, System.Threading.CancellationToken cancellationToken = default)
        {
            if (parent == null)
            {
                Debug.LogError("SpawnGltfScenes: parent Transform is null");
                return;
            }

            // Additional Unity null check (handles destroyed objects)
            if (parent == null)
            {
                Debug.LogError("SpawnGltfScenes: parent Transform has been destroyed");
                return;
            }

            parentTransform = parent;
            if (gltfImport == null)
            {
                Debug.LogError("SpawnGltfScenes: gltfImport is null");
                return;
            }

            try
            {
                Content parentContent = parent.GetComponent<Content>();
                if (parentContent!=null)
                {
                    tile = parentContent.ParentTile;
                }

                if (tile == null)
                {
                    Debug.LogWarning("SpawnGltfScenes: Could not find parent Tile, positioning may be incorrect");
                }

                var scenes = gltfImport.SceneCount;

                //Spawn all scenes (InstantiateMainSceneAsync only possible if main scene was referenced in gltf)
                for (int i = 0; i < scenes; i++)
                {
                    // Check cancellation token first
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // Check if parent still exists before each async call (Unity's == operator handles destroyed objects)
                    if (parent == null) 
                    {
                        Debug.LogWarning($"SpawnGltfScenes: parent Transform destroyed during scene {i} instantiation");
                        return;
                    }

                    // Additional check for Content component state to see if disposal was requested
                    Content currentParentContent = parent.GetComponent<Content>();
                    if (currentParentContent == null || currentParentContent.State == Content.ContentLoadState.NOTLOADING)
                    {
                        Debug.LogWarning($"SpawnGltfScenes: Content disposed during scene {i} instantiation");
                        return;
                    }

                    try
                    {
                        // Additional validation before calling GLTFast
                        if (gltfImport == null)
                        {
                            Debug.LogError($"SpawnGltfScenes: gltfImport became null during scene {i} processing");
                            return;
                        }

                        // Check cancellation before async operation
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        // Additional validation right before GLTFast call
                        if (parent == null || gltfImport == null)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: parent or gltfImport became null before scene {i} instantiation");
                            return;
                        }
                        
                        // Check if gltfImport has been disposed (try to access a property)
                        try
                        {
                            var sceneCount = gltfImport.SceneCount; // This will throw if disposed
                            if (i >= sceneCount)
                            {
                                Debug.LogWarning($"SpawnGltfScenes: Scene index {i} out of range (SceneCount: {sceneCount})");
                                return;
                            }
                        }
                        catch (System.ObjectDisposedException)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: gltfImport was disposed before scene {i} instantiation");
                            return;
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: Error accessing gltfImport before scene {i}: {ex.Message}");
                            return;
                        }
                        
                        // Check Content state again right before instantiation
                        Content preInstantiationContent = parent.GetComponent<Content>();
                        if (preInstantiationContent == null || preInstantiationContent.State == Content.ContentLoadState.NOTLOADING)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: Content disposed right before scene {i} instantiation");
                            return;
                        }

                        try
                        {
                            // Wrap the InstantiateSceneAsync in a protected call
                            await SafeInstantiateSceneAsync(gltfImport, parent, i, cancellationToken);
                        }
                        catch (System.ObjectDisposedException)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: gltfImport was disposed during scene {i} instantiation");
                            return;
                        }
                        catch (System.NullReferenceException ex)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: Null reference during scene {i} instantiation - likely destroyed GameObject: {ex.Message}");
                            return;
                        }
                        catch (UnityEngine.MissingReferenceException ex)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: Missing reference during scene {i} instantiation - GameObject was destroyed: {ex.Message}");
                            return;
                        }
                        
                        // Check cancellation after async operation
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        // Check again if we're still valid after async operation
                        Content postAsyncContent = parent?.GetComponent<Content>();
                        if (postAsyncContent == null || postAsyncContent.State == Content.ContentLoadState.NOTLOADING)
                        {
                            Debug.LogWarning($"SpawnGltfScenes: Content disposed during scene {i} async instantiation");
                            return;
                        }
                    }
                    catch (System.OperationCanceledException)
                    {
                        Debug.Log($"SpawnGltfScenes: Scene {i} instantiation was cancelled (this is normal during disposal)");
                        throw; // Re-throw to let calling code know it was cancelled
                    }
                    catch (System.NullReferenceException ex)
                    {
                        Debug.LogError($"SpawnGltfScenes: Null reference in scene {i}: {ex.Message}\nStackTrace: {ex.StackTrace}");
                        return;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"SpawnGltfScenes: Error instantiating scene {i}: {ex.Message}\nStackTrace: {ex.StackTrace}");
                        return;
                    }
                    
                    // Check again after async operation (Unity's == operator handles destroyed objects)
                    if (parent == null || i >= parent.childCount) 
                    {
                        Debug.LogWarning($"SpawnGltfScenes: parent or children destroyed after scene {i} instantiation");
                        return;
                    }

                    // Check cancellation before scene processing
                    cancellationToken.ThrowIfCancellationRequested();

                    var scene = parent.GetChild(i).transform;
                    if (scene == null || scene.gameObject == null) 
                    {
                        Debug.LogWarning($"SpawnGltfScenes: scene {i} is null or destroyed, skipping");
                        continue;
                    }

                    //set unitylayer for all gameon=bjects in scene to unityLayer of container
                    try 
                    {
                        foreach (var child in scene.GetComponentsInChildren<Transform>(true)) //getting the Transform components ensures the layer of each recursive child is set 
                        {
                            // Check cancellation during child processing
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            if (child != null && child.gameObject != null)
                            {
                                child.gameObject.layer = parent.gameObject.layer;
                            }
                        }
                    }
                    catch (System.OperationCanceledException)
                    {
                        Debug.Log($"SpawnGltfScenes: Layer setting was cancelled during scene {i} processing");
                        throw; // Re-throw to let calling code know it was cancelled
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"SpawnGltfScenes: Error setting layers for scene {i}: {ex.Message}");
                    }
                    
                    // Check cancellation before positioning
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // Revalidate tile and content before positioning (they could become null during async operations)
                    Content currentContent = parent.GetComponent<Content>();
                    Tile currentTile = currentContent?.ParentTile;
                    
                    if (currentTile != null && currentTile.content != null)
                    {
                        PositionGameObject(scene, rtcCenter, currentTile);
                    }
                    else
                    {
                        Debug.LogWarning($"SpawnGltfScenes: tile or tile.content became null during scene {i} processing, skipping positioning");
                    }
                }
            }
            catch (System.OperationCanceledException)
            {
                Debug.Log("SpawnGltfScenes: Operation was cancelled (this is normal during disposal)");
                throw; // Re-throw to let calling code know it was cancelled
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"SpawnGltfScenes: Unexpected error during scene processing: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }
        void PositionGameObject(Transform scene, double[] rtcCenter, Tile tile)
        {
            if (scene == null || tile == null)
            {
                Debug.LogWarning("PositionGameObject: scene or tile is null");
                return;
            }

            // Additional null check for tile.content
            if (tile.content == null)
            {
                Debug.LogWarning("PositionGameObject: tile.content is null");
                return;
            }

            //get the transformationMAtrix from the gameObject created bij GltFast
            Matrix4x4 BasisMatrix = Matrix4x4.TRS(scene.position, scene.rotation, scene.localScale);
            TileTransform basistransform = new TileTransform()
            {
                m00 = BasisMatrix.m00,
                m01 = BasisMatrix.m01,
                m02 = BasisMatrix.m02,
                m03 = BasisMatrix.m03,

                m10 = BasisMatrix.m10,
                m11 = BasisMatrix.m11,
                m12 = BasisMatrix.m12,
                m13 = BasisMatrix.m13,

                m20 = BasisMatrix.m20,
                m21 = BasisMatrix.m21,
                m22 = BasisMatrix.m22,
                m23 = BasisMatrix.m23,

                m30 = BasisMatrix.m30,
                m31 = BasisMatrix.m31,
                m32 = BasisMatrix.m32,
                m33 = BasisMatrix.m33,
            };

            // transformation from created gameObject back to GLTF-space
            // this transformation has to be changed when moving to Unith.GLTFast version 4.0 or older (should then be changed to m00=1,m11=1,m22=-1,m33=1)
            TileTransform gltFastToGLTF = new TileTransform()
            {
                m00 = -1d,
                m11 = 1,
                m22 = 1,
                m33 = 1,
            };

            //transformation y-up to Z-up, to change form gltf-space to 3dtile-space
            TileTransform yUpToZUp = new TileTransform()
            {
                m00 = 1d,
                m12 = -1d,
                m21 = 1,
                m33 = 1d
            };

            //get the transformation of the created gameObject in 3dTile-space
            TileTransform geometryInECEF = yUpToZUp*gltFastToGLTF * basistransform;

            //apply the tileTransform
            TileTransform geometryInCRS = tile.tileTransform * geometryInECEF;

            //transformation from ECEF to Unity
            TileTransform ECEFToUnity = new TileTransform() //from ecef to Unity
            {
                m01 = 1d,   //unityX = ecefY
                m12 = 1d,   //unity = ecefZ
                m20 = -1d,  //unityZ = ecef-x
                m33=1d
            };

            // move the transformation to Unity-space
            TileTransform geometryInUnity = ECEFToUnity * geometryInCRS;

            // create a transformation using floats to be able to extract scale and rotation in unity-space
            Matrix4x4 final = new Matrix4x4()
            {
                m00 = (float)geometryInUnity.m00,
                m01 = (float)geometryInUnity.m01,
                m02 = (float)geometryInUnity.m02,
                m03 = 0f,

                m10 = (float)geometryInUnity.m10,
                m11 = (float)geometryInUnity.m11,
                m12 = (float)geometryInUnity.m12,
                m13 = 0f,

                m20 = (float)geometryInUnity.m20,
                m21 = (float)geometryInUnity.m21,
                m22 = (float)geometryInUnity.m22,
                m23 = 0f,

                m30 = 0f,
                m31=0f,
                m32=0f,
                m33=1f
            };
            Vector3 translation;
            Vector3 scale;
            Quaternion rotation;
            // get rotation and scale in unity-space
            final.Decompose(out translation, out rotation, out scale);

            // get the coordinate of the origin of the created gameobject in the 3d-tiles CoordinateSystem
            Coordinate sceneCoordinate = new Coordinate(tile.content.contentcoordinateSystem, geometryInCRS.m03, geometryInCRS.m13, geometryInCRS.m23);
            if (rtcCenter != null)
            {
                sceneCoordinate = new Coordinate(tile.content.contentcoordinateSystem, rtcCenter[0], rtcCenter[1], rtcCenter[2])+sceneCoordinate;
            }

            /// TEMPORARY FIX
            /// rotationToGRavityUp applies an extra rotation of -90 degrees around the up-axis in case of ECEF-coordinateSystems. 
            /// dis should not be done and has to be removed
            /// until that time, we rotate by 90 degrees around the up-axis to counter the applied rotation
            rotation = Quaternion.AngleAxis(90, Vector3.up) * rotation;
            tile.content.contentCoordinate = sceneCoordinate;
            
            //apply scale, position and rotation to the gameobject
            scene.localScale = scale;
            ScenePosition scenepos = scene.gameObject.AddComponent<ScenePosition>();
            scenepos.contentposition = sceneCoordinate;
            scene.position = sceneCoordinate.ToUnity();
            scene.rotation = sceneCoordinate.RotationToLocalGravityUp() * rotation;
            

        }
        public void ParseAssetMetaData(Content content)
        {
            //Extract json from glb
            var gltfAndBin = ExtractJsonAndBinary(glbBuffer);
            var gltfJsonText = gltfAndBin.Item1;

            //Deserialize json using JSON.net instead of Unity's JsonUtility ( gave silent error )
            var gltfRoot = JsonConvert.DeserializeObject<GltfMeshFeatures.GltfRootObject>(gltfJsonText);
            var metadata = content.gameObject.AddComponent<ContentMetadata>();
            metadata.asset = gltfRoot.asset;

            content.tilesetReader.OnLoadAssetMetadata.Invoke(metadata);
        }

        /// <summary>
        /// Parse subobjects from gltf data
        /// </summary>
        /// <param name="parent">Parent transform where scenes were spawned in</param>
        public void ParseSubObjects(Transform parent)
        {
            //Extract json from glb
            var gltfAndBin = ExtractJsonAndBinary(glbBuffer);
            var gltfJsonText = gltfAndBin.Item1;
            var binaryBlob = gltfAndBin.Item2;

            //Deserialize json using JSON.net instead of Unity's JsonUtility ( gave silent error )
            var gltfFeatures = JsonConvert.DeserializeObject<GltfMeshFeatures.GltfRootObject>(gltfJsonText);

            var featureIdBufferViewIndex = 0;
            foreach (var mesh in gltfFeatures.meshes)
            {
                foreach (var primitive in mesh.primitives)
                {
                    featureIdBufferViewIndex = primitive.attributes._FEATURE_ID_0;
                }
            }
            if (featureIdBufferViewIndex == -1)
            {
                Debug.LogWarning("_FEATURE_ID_0 was not found in the dataset. This is required to find BAG id's.");
                return;
            }

            //Use feature ID as bufferView index and get bufferview
            var featureAccessor = gltfFeatures.accessors[featureIdBufferViewIndex];
            var targetBufferView = gltfFeatures.bufferViews[featureAccessor.bufferView];

            // Note: Compression is not supported in this implementation
            // var compressed = gltfFeatures.extensionsRequired.Contains("EXT_meshopt_compression"); //Needs testing

            var featureIdBuffer = GetFeatureBuffer(gltfFeatures.buffers, targetBufferView, binaryBlob);
            if (featureIdBuffer == null || featureIdBuffer.Length == 0)
            {
                Debug.LogWarning("Getting feature buffer failed.");
                return;
            }

            //Parse feature table into reusable List to avoid heap allocations
            reusableVertexFeatureIds.Clear();
            var stride = targetBufferView.byteStride;
            int currentFeatureTableIndex = -1;
            int vertexCount = 0;
            int accessorOffset = featureAccessor.byteOffset;
            for (int i = 0; i < featureIdBuffer.Length; i += stride)
            {
                //TODO: Read componentType from accessor to determine how to read the featureTableIndex
                var featureTableIndex = (int)BitConverter.ToSingle(featureIdBuffer, i+accessorOffset); 
                
                if (currentFeatureTableIndex != featureTableIndex)
                {
                    if (currentFeatureTableIndex != -1)
                        reusableVertexFeatureIds.Add(new Vector2Int(currentFeatureTableIndex, vertexCount));

                    currentFeatureTableIndex = featureTableIndex;
                    vertexCount = 1;
                }
                else
                {
                    vertexCount++;
                }
            }
            //Finish last feature table entry
            reusableVertexFeatureIds.Add(new Vector2Int(currentFeatureTableIndex, vertexCount));

            //Retrieve EXT_structural_metadata tables
            var propertyTables = gltfFeatures.extensions.EXT_structural_metadata.propertyTables;

            //Now parse the property tables BAGID using reusable list
            reusableBagIdList.Clear();
            foreach (var propertyTable in propertyTables)
            {
                //Now parse the data from the buffer using stringOffsetType=UINT32
                var bagpandid = propertyTable.properties.bagpandid; //Based on Tyler dataset key naming
                var identificatie = propertyTable.properties.identificatie;  //Based on PG2B3DM dataset key naming

                var bufferViewIndex = (bagpandid != null) ? bagpandid.values : identificatie.values; //Values reference the bufferView index
                var count = propertyTable.count;
                var bufferView = gltfFeatures.bufferViews[bufferViewIndex];
                var stringSpan = bufferView.byteLength / count; //string length in bytes

                //Directly convert the buffer to a list of strings
                var stringBytesSpan = new Span<byte>(binaryBlob, (int)bufferView.byteOffset, bufferView.byteLength);
                for (int i = 0; i < count; i++)
                {
                    var stringBytesSpanSlice = stringBytesSpan.Slice(i * stringSpan, stringSpan);
                    // Avoid ToArray() by using Encoding.ASCII.GetString(ReadOnlySpan<byte>)
                    var stringBytesString = Encoding.ASCII.GetString(stringBytesSpanSlice);
                    reusableBagIdList.Add(stringBytesString);
                }
                break; //Just support one for now.
            }

#if SUBOBJECT

            foreach (Transform child in parent)
            {
                Debug.Log(child.name,child.gameObject);
                //Add subobjects to the spawned gameobject
                child.gameObject.AddComponent<MeshCollider>();
                ObjectMapping objectMapping = child.gameObject.AddComponent<ObjectMapping>();
                
                // Clear and reuse ObjectMappingItem list to avoid per-child allocations
                reusableObjectMappingItems.Clear();

                //For each uniqueFeatureIds, add a subobject
                int offset = 0;
                for (int i = 0; i < reusableVertexFeatureIds.Count; i++)
                {
                    var uniqueFeatureId = reusableVertexFeatureIds[i];
                    var bagId = reusableBagIdList[uniqueFeatureId.x];
                    
                    //Remove any prefixes/additions to the bag id
                    bagId = Regex.Replace(bagId, "[^0-9]", "");

                    var subObject = new ObjectMappingItem()
                    {
                        objectID = bagId,
                        firstVertex = offset,
                        verticesLength = uniqueFeatureId.y
                    };
                    reusableObjectMappingItems.Add(subObject);
                    offset += uniqueFeatureId.y;
                }
                
                // Copy to final list (ObjectMapping expects a List<ObjectMappingItem>)
                objectMapping.items = new List<ObjectMappingItem>(reusableObjectMappingItems);
            }
            return;
#endif
            Debug.LogWarning("Subobjects are not supported in this build. Please use the Netherlands3D.SubObjects package.");
        }

        private byte[] GetFeatureBuffer(GltfMeshFeatures.Buffer[] buffers, GltfMeshFeatures.BufferView bufferView, byte[] glbBuffer)
        {
            // Simple byte array copy - no compression support needed
            Debug.Log("Use buffer directly");
            byte[] result = new byte[bufferView.byteLength];
            System.Array.Copy(glbBuffer, (int)bufferView.byteOffset, result, 0, (int)bufferView.byteLength);
            return result;
        }

        public static (string, byte[]) ExtractJsonAndBinary(byte[] glbData)
        {
            if (glbData.Length < 12)
                Debug.Log("GLB file is too short.");

            //Check the magic bytes to ensure it's a GLB file
            var magicBytes = BitConverter.ToUInt32(glbData, 0);
            if (magicBytes != 0x46546C67) // "glTF"
                Debug.Log("Not a valid GLB file.");

            var version = BitConverter.ToUInt32(glbData, 4);
            var length = BitConverter.ToUInt32(glbData, 8);

            if (version != 2)
                Debug.Log("Unsupported GLB version.");

            if (glbData.Length != length)
                Debug.Log("GLB file length does not match the declared length.");

            //Find the JSON chunk
            var jsonChunkLength = BitConverter.ToUInt32(glbData, 12);
            if (jsonChunkLength == 0)
                Debug.Log("JSON chunk is missing.");
            var jsonChunkOffset = 20; //GLB header (12 bytes) + JSON chunk header (8 bytes)

            //Extract JSON as a string
            var json = Encoding.UTF8.GetString(glbData, jsonChunkOffset, (int)jsonChunkLength);

            // Find the binary chunk
            var binaryChunkLength = length - jsonChunkLength - 28; //28 = GLB header (12 bytes) + JSON chunk header (8 bytes) + JSON chunk length (4 bytes) + BIN chunk header (8 bytes)
            if (binaryChunkLength == 0)
                Debug.Log("BIN chunk is missing.");
            var binaryChunkOffset = jsonChunkOffset + (int)jsonChunkLength + 8; //JSON chunk header (8 bytes) + JSON chunk length (4 bytes)

            //Extract binary data as a byte array
            var binaryData = new byte[binaryChunkLength];
            System.Buffer.BlockCopy(glbData, binaryChunkOffset, binaryData, 0, (int)binaryChunkLength);

            return (json, binaryData);
        }

        public void OverrideAllMaterials(UnityEngine.Material overrideMaterial)
        {
            if (parentTransform == null)
            {
                return;
            }
            foreach (var renderer in parentTransform.GetComponentsInChildren<Renderer>())
            {
                renderer.material = overrideMaterial;
            }
        }

        /// <summary>
        /// Clear reusable collections to prepare for reuse and prevent memory leaks
        /// Call this when reusing a ParsedGltf instance for a new GLB file
        /// </summary>
        public void ClearReusableCollections()
        {
            reusableVertexFeatureIds.Clear();
            reusableBagIdList.Clear();
            reusableObjectMappingItems.Clear();
            uniqueColors.Clear();
        }

        /// <summary>
        /// Dispose GltfImport - use only when completely done with content
        /// </summary>
        public void Dispose()
        {
            ClearReusableCollections();
            
            // Dispose the GltfImport if it exists
            if (gltfImport != null)
            {
                gltfImport.Dispose();
                gltfImport = null;
            }
        }



        /// <summary>
        /// Safe wrapper for InstantiateSceneAsync that handles destroyed GameObjects gracefully
        /// </summary>
        private async Task SafeInstantiateSceneAsync(GltfImport gltfImport, Transform parent, int sceneIndex, System.Threading.CancellationToken cancellationToken)
        {
            // Final validation before calling GLTFast
            if (gltfImport == null)
            {
                throw new System.ObjectDisposedException("gltfImport");
            }
            
            if (parent == null)
            {
                throw new UnityEngine.MissingReferenceException("parent Transform is null or destroyed");
            }

            // Additional Unity null check
            if (parent == null)
            {
                throw new UnityEngine.MissingReferenceException("parent Transform GameObject was destroyed");
            }

            try
            {
                // Check cancellation one more time before the async call
                cancellationToken.ThrowIfCancellationRequested();
                
                // Call GLTFast InstantiateSceneAsync directly but catch all Unity-related exceptions
                await gltfImport.InstantiateSceneAsync(parent, sceneIndex);
            }
            catch (System.NullReferenceException ex)
            {
                // Convert to a more specific exception type we can handle
                throw new UnityEngine.MissingReferenceException($"GameObject was destroyed during scene {sceneIndex} instantiation: {ex.Message}", ex);
            }
            catch (UnityEngine.MissingReferenceException)
            {
                // Re-throw MissingReferenceException as-is
                throw;
            }
            catch (System.Exception ex) when (ex.Message.Contains("destroyed") || ex.Message.Contains("missing"))
            {
                // Catch any other exception that mentions destroyed or missing objects
                throw new UnityEngine.MissingReferenceException($"GameObject reference error during scene {sceneIndex} instantiation: {ex.Message}", ex);
            }
        }
    }
}