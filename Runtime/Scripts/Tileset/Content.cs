using GLTFast;
using Netherlands3D.Coordinates;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using System.Text;
using SimpleJSON;
#if UNITY_EDITOR
using System.IO.Compression;
#endif


namespace Netherlands3D.Tiles3D
{
    [System.Serializable]
    public class Content : MonoBehaviour, IDisposable
    {
        public string uri = "";
        public Coordinate contentCoordinate;
        public CoordinateSystem contentcoordinateSystem;

#if SUBOBJECT
        public bool parseSubObjects = true;
#endif

        public bool parseAssetMetaData = false;

        public Read3DTileset tilesetReader;
        [SerializeField] private Tile parentTile;
        public Tile ParentTile { get => parentTile; set => parentTile = value; }

        public UnityEvent onDoneDownloading = new();
        public UnityEvent<Content> onTileLoadCompleted = new();

        private UnityEngine.Material overrideMaterial;
        private GLTFast.GltfImport gltfImport; // Reference to dispose later
        
        // Cancellation token for async operations
        private System.Threading.CancellationTokenSource cancellationTokenSource;
        private Coroutine runningDownloadCoroutine;

        Dictionary<string, string> headers = null;
        public enum ContentLoadState
        {
            NOTLOADING,
            DOWNLOADING,
            DOWNLOADED,
            PARSING,
        }
        private ContentLoadState state = ContentLoadState.NOTLOADING;
        public ContentLoadState State
        {
            get => state;
            set
            {
                state = value;
            }
        }
#if UNITY_EDITOR
        /// <summary>
        /// Draw wire cube in editor with bounds and color coded state
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (ParentTile == null) return;

            Color color = Color.white;
            switch (State)
            {
                case ContentLoadState.NOTLOADING:
                    color = Color.red;
                    break;
                case ContentLoadState.DOWNLOADING:
                    color = Color.yellow;
                    break;
                case ContentLoadState.DOWNLOADED:
                    color = Color.green;
                    break;
                default:
                    break;
            }

            Gizmos.color = color;
            var parentTileBounds = ParentTile.ContentBounds;
            Gizmos.DrawWireCube(parentTileBounds.center, parentTileBounds.size);

            Gizmos.color = Color.blue;
            Gizmos.DrawLine(parentTileBounds.center, parentTileBounds.center + (ParentTile.priority * Vector3.up));
        }
#endif

        /// <summary>
        /// Load the content from an url
        /// </summary>
        public void Load(UnityEngine.Material overrideMaterial = null, Dictionary<string, string> headers = null, bool verbose = false)
        {
            if (State == ContentLoadState.DOWNLOADING || State == ContentLoadState.DOWNLOADED)
                return;

            this.headers = headers;
            if (overrideMaterial != null)
            {
                this.overrideMaterial = overrideMaterial;
            }

            // Start async loading
            _ = LoadAsync();
        }

        /// <summary>
        /// Async method that handles the complete loading flow
        /// </summary>
        private async Task LoadAsync()
        {
            try
            {
                // Step 1: Set loading state
                State = ContentLoadState.DOWNLOADING;
                parentTile.isLoading = true;
                
                // Create cancellation token for this loading operation
                if (cancellationTokenSource == null)
                    cancellationTokenSource = new System.Threading.CancellationTokenSource();

                // Step 2: Download content using coroutine
                byte[] contentBytes = await DownloadContentCoroutineAsync(uri, headers);
                
                // Check if we're still valid after download
                if (this == null || gameObject == null || cancellationTokenSource.Token.IsCancellationRequested)
                {
                    Debug.LogWarning("Content was destroyed or cancelled during download");
                    return;
                }

                if (contentBytes == null)
                {
                    FinishedLoading(false);
                    return;
                }

                // Step 3: Process content
                State = ContentLoadState.PARSING;
                bool success = await ProcessContentAsync(contentBytes, uri);
                
                // Step 4: Finish loading
                FinishedLoading(success);
            }
            catch (System.OperationCanceledException)
            {
                Debug.Log("Content loading was cancelled (this is normal during disposal)");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error loading content from {uri}: {ex.Message}");
                FinishedLoading(false);
            }
        }

        /// <summary>
        /// Register GltfImport for later disposal
        /// </summary>
        public void RegisterGltfImport(GLTFast.GltfImport gltfImport)
        {
            this.gltfImport = gltfImport;
        }

        /// <summary>
        /// After parsing gltf content spawn gltf scenes
        /// </summary>
        /// 
        private void FinishedLoading(bool succes)
        {
            State = ContentLoadState.DOWNLOADED;
            onDoneDownloading.Invoke();
            onTileLoadCompleted.Invoke(this);
        }

        private void OverrideAllMaterials(Transform parent)
        {
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>())
            {
                renderer.material = overrideMaterial;
            }
        }

        /// <summary>
        /// Clean up coroutines and content gameobjects
        /// </summary>
        public void Dispose()
        {
            onDoneDownloading.RemoveAllListeners();

            if (State == ContentLoadState.PARSING)
            {
                onDoneDownloading.AddListener(Dispose);
                return;
            }

            // Cancel any running async operations
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }

            // Stop any running download coroutines
            if (runningDownloadCoroutine != null)
            {
                StopCoroutine(runningDownloadCoroutine);
                runningDownloadCoroutine = null;
            }
           
            State = ContentLoadState.NOTLOADING;

            // Dispose GltfImport to free native memory
            if (gltfImport != null)
            {
                gltfImport.Dispose();
                gltfImport = null;
            }

            // Check if GameObject is still valid before proceeding with component cleanup
            if (this == null || gameObject == null)
            {
                return;
            }

            if (overrideMaterial == null)
            {
                Renderer[] meshrenderers = this.gameObject.GetComponentsInChildren<Renderer>();
                ClearRenderers(meshrenderers);
            }
            MeshFilter[] meshFilters = this.gameObject.GetComponentsInChildren<MeshFilter>();
            foreach (var meshFilter in meshFilters)
            {
                //the order of destroying sharedmesh before mesh matters for cleaning up native shells
                if (meshFilter.sharedMesh != null)
                {
                    UnityEngine.Mesh mesh = meshFilter.sharedMesh;
                    meshFilter.sharedMesh.Clear();
                    Destroy(mesh);
                    meshFilter.sharedMesh = null;
                }
                if (meshFilter.mesh != null)
                {
                    UnityEngine.Mesh mesh = meshFilter.mesh;
                    meshFilter.mesh.Clear();
                    Destroy(mesh);
                    meshFilter.mesh = null;
                }
            }

            // Also clean up colliders that might reference meshes
            if (this != null && gameObject != null)
            {
                Collider[] colliders = this.gameObject.GetComponentsInChildren<Collider>();
                foreach (var collider in colliders)
                {
                    if (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                    {
                        // Clear mesh reference before destroying
                        meshCollider.sharedMesh = null;
                    }
                }

                Destroy(this.gameObject);
            }            
        }

        //todo we need to come up with a way to get all used texture slot property names from the gltf package
        private void ClearRenderers(Renderer[] renderers)
        {
            foreach (Renderer r in renderers)
            {
                Material mat = r.sharedMaterial;
                if (mat == null) continue;

                int mainTexNameID = NL3DShaders.MainTextureShaderProperty;

                if (mat.HasProperty(mainTexNameID))
                {
                    Texture tex = mat.GetTexture(mainTexNameID);

                    if (tex != null)
                    {
                        mat.SetTexture(mainTexNameID, null);
                        UnityEngine.Object.Destroy(tex);
                        tex = null;
                    }
                }
                UnityEngine.Object.Destroy(mat);
                r.sharedMaterial = null;
            }
        }

        #region Content Loading Methods (hybrid coroutine + async approach)
        
        /// <summary>
        /// Download content using coroutine wrapped in async Task
        /// </summary>
        private async Task<byte[]> DownloadContentCoroutineAsync(string url, Dictionary<string, string> customHeaders = null)
        {
            var tcs = new TaskCompletionSource<byte[]>();
            
            // Start coroutine and wait for completion
            runningDownloadCoroutine = StartCoroutine(DownloadContentCoroutine(url, customHeaders, tcs));
            
            return await tcs.Task;
        }

        /// <summary>
        /// Coroutine that handles the actual download
        /// </summary>
        private IEnumerator DownloadContentCoroutine(string url, Dictionary<string, string> customHeaders, TaskCompletionSource<byte[]> tcs)
        {
            using (var webRequest = UnityWebRequest.Get(url))
            {
                if (customHeaders != null)
                {
                    foreach (var header in customHeaders)
                        webRequest.SetRequestHeader(header.Key, header.Value);
                }

                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"{url} -> {webRequest.error}");
                    tcs.SetResult(null);
                }
                else
                {
                    tcs.SetResult(webRequest.downloadHandler.data);
                }
            }
        }

        /// <summary>
        /// Process downloaded content data asynchronously
        /// </summary>
        private async Task<bool> ProcessContentAsync(byte[] contentBytes, string sourceUri)
        {
            // Get content type from binary header
            ContentType contentType = GetContentTypeFromBinaryHeader(contentBytes);

            if (contentType == ContentType.undefined)
            {
                contentType = ContentType.gltf;
            }

            // Handle different content types
            try
            {
                switch (contentType)
                {
                    case ContentType.b3dm:
                        return await ProcessB3dmAsync(contentBytes, sourceUri);
                    case ContentType.glb:
                        return await ProcessGlbAsync(contentBytes, sourceUri);
                    case ContentType.gltf:
                        return await ProcessGltfAsync(contentBytes, sourceUri);
                    case ContentType.pnts:
                    case ContentType.i3dm:
                    case ContentType.cmpt:
                    case ContentType.subtree:
                    case ContentType.tileset:
                    default:
                        Debug.LogWarning($"Unsupported content type: {contentType}");
                        return false;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing {contentType} content: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ProcessB3dmAsync(byte[] contentBytes, string sourceUri)
        {
            Debug.Log("Starting B3DM processing");

            // Early validation - check if component is still valid
            if (this == null || gameObject == null || transform == null)
            {
                Debug.LogWarning("Content component destroyed before B3DM processing started");
                return false;
            }

            // Check cancellation token
            if (cancellationTokenSource != null && cancellationTokenSource.Token.IsCancellationRequested)
            {
                Debug.LogWarning("B3DM processing cancelled before starting");
                return false;
            }

            var memoryStream = new System.IO.MemoryStream(contentBytes);
            var b3dm = B3dmReader.ReadB3dm(memoryStream);
            
            double[] rtcCenter = GetRTCCenterFromB3dm(b3dm);

            RemoveCesiumRtcFromRequieredExtentions(ref b3dm);
            if (rtcCenter == null)
            {
                rtcCenter = GetRTCCenterFromGlb(b3dm.GlbData); // Reuse existing method
            }
            
            var materialGenerator = new NL3DMaterialGenerator();
            GltfImport gltf = new GltfImport(null, null, materialGenerator);
            
            var success = true;
            Uri uri = null;
            if (sourceUri != "")
            {
                uri = new Uri(sourceUri);
            }
    
            success = await gltf.Load(b3dm.GlbData, uri, GetImportSettings()); // Use modern Load method instead of obsolete LoadGltfBinary

            // Check again after async operation
            if (this == null || gameObject == null || transform == null)
            {
                Debug.LogWarning("Content component destroyed during B3DM loading");
                return false;
            }

            if (success == false)
            {
                Debug.Log("cant load b3dm: " + sourceUri);
                return false;
            }

            var parsedGltf = new ParsedGltf()
            {
                gltfImport = gltf,
                rtcCenter = rtcCenter,
#if SUBOBJECT
                glbBuffer = b3dm.GlbData //Store the glb buffer for access in subobjects
#endif
            };

            try
            {
                // Check if component is still valid before proceeding
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed, canceling B3DM processing");
                    return false;
                }

                await parsedGltf.SpawnGltfScenes(transform);

                // Check again after async operation in case component was destroyed
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed during B3DM processing");
                    return false;
                }

                gameObject.name = sourceUri;

                // Register GltfImport for later disposal
                RegisterGltfImport(gltf);

                if (overrideMaterial != null)
                {
                    parsedGltf.OverrideAllMaterials(overrideMaterial);
                }

                // Final validation before success
                if (this == null || gameObject == null)
                {
                    Debug.LogWarning("Content component destroyed before B3DM processing completed");
                    return false;
                }

                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing B3DM content: {ex.Message}");
                return false;
            }
            finally
            {
            }
        }

        private async Task<bool> ProcessGlbAsync(byte[] contentBytes, string sourceUri)
        {
            // Early validation - check if component is still valid
            if (this == null || gameObject == null || transform == null)
            {
                Debug.LogWarning("Content component destroyed before GLB processing started");
                return false;
            }

            // Check cancellation token
            if (cancellationTokenSource != null && cancellationTokenSource.Token.IsCancellationRequested)
            {
                Debug.LogWarning("GLB processing cancelled before starting");
                return false;
            }

            var consoleLogger = new GLTFast.Logging.ConsoleLogger();
            
            var materialGenerator = new NL3DMaterialGenerator();
            GltfImport gltf = new GltfImport(null, null, materialGenerator, consoleLogger);
            
            var success = true;
            Uri uri = null;
            if (sourceUri != "")
            {
                uri = new Uri(sourceUri);
            }
            RemoveCesiumRtcFromRequieredExtentions(ref contentBytes);
    
            success = await gltf.Load(contentBytes, uri, GetImportSettings());

            // Check again after async operation
            if (this == null || gameObject == null || transform == null)
            {
                Debug.LogWarning("Content component destroyed during GLB loading");
                return false;
            }

            if (success == false)
            {
                Debug.Log("cant load glb: " + sourceUri);
                return false;
            }
            
            // Validate data before processing
            if (contentBytes == null || contentBytes.Length == 0)
            {
                Debug.LogError("GLB data is null or empty");
                return false;
            }

            double[] rtcCenter = GetRTCCenterFromGlb(contentBytes);
            var parsedGltf = new ParsedGltf()
            {
                gltfImport = gltf,
                rtcCenter = rtcCenter,
            };

            try
            {
                // Check if this component and GameObject are still valid before proceeding
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component or GameObject destroyed, canceling GLB processing");
                    return false;
                }

                // Additional validation before spawning scenes
                if (parsedGltf.gltfImport == null)
                {
                    Debug.LogError("GltfImport is null, cannot spawn scenes");
                    return false;
                }

                // Add timeout to prevent hanging
                var timeout = System.TimeSpan.FromSeconds(30); // 30 second timeout
                using (var cts = new System.Threading.CancellationTokenSource(timeout))
                {
                    try
                    {
                        await parsedGltf.SpawnGltfScenes(transform);
                    }
                    catch (System.OperationCanceledException)
                    {
                        Debug.LogError("GLB processing timed out after 30 seconds");
                        return false;
                    }
                }

                // Check again after async operation in case component was destroyed
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component or GameObject destroyed during GLB processing");
                    return false;
                }

                gameObject.name = sourceUri;

                // Register GltfImport for later disposal
                RegisterGltfImport(gltf);

                if (overrideMaterial != null)
                {
                    parsedGltf.OverrideAllMaterials(overrideMaterial);
                }

                // Final validation before success
                if (this == null || gameObject == null)
                {
                    Debug.LogWarning("Content component destroyed before GLB processing completed");
                    return false;
                }

                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing GLB content: {ex.Message}");
                return false;
            }
            finally
            {
            }
        }

        private async Task<bool> ProcessGltfAsync(byte[] contentBytes, string sourceUri)
        {
            // Early validation - check if component is still valid
            if (this == null || gameObject == null || transform == null)
            {
                Debug.LogWarning("Content component destroyed before GLTF processing started");
                return false;
            }

            // Check cancellation token
            if (cancellationTokenSource != null && cancellationTokenSource.Token.IsCancellationRequested)
            {
                Debug.LogWarning("GLTF processing cancelled before starting");
                return false;
            }

            var materialGenerator = new NL3DMaterialGenerator();
            GltfImport gltf = new GltfImport(null, null, materialGenerator);
            
            var success = true;
            Uri uri = null;
            if (sourceUri != "")
            {
                uri = new Uri(sourceUri);
            }
    
            success = await gltf.Load(uri);

            // Check again after async operation
            if (this == null || gameObject == null || transform == null)
            {
                Debug.LogWarning("Content component destroyed during GLTF loading");
                return false;
            }

            if (success == false)
            {
                Debug.Log("cant load gltf: " + sourceUri);
                return false;
            }

            var parsedGltf = new ParsedGltf()
            {
                gltfImport = gltf,
            };

            try
            {
                // Check if component is still valid before proceeding
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed, canceling GLTF processing");
                    return false;
                }

                await parsedGltf.SpawnGltfScenes(transform);

                // Check again after async operation in case component was destroyed
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed during GLTF processing");
                    return false;
                }

                gameObject.name = sourceUri;

                // Register GltfImport for later disposal
                RegisterGltfImport(gltf);

                if (overrideMaterial != null)
                {
                    parsedGltf.OverrideAllMaterials(overrideMaterial);
                }

                // Final validation before success
                if (this == null || gameObject == null)
                {
                    Debug.LogWarning("Content component destroyed before GLTF processing completed");
                    return false;
                }

                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing GLTF content: {ex.Message}");
                return false;
            }
            finally
            {
            }
        }
        
        
        enum ContentType
        {
            undefined,
            b3dm,
            pnts,
            i3dm,
            cmpt,
            glb,
            gltf,
            subtree,
            tileset
        }

        private ContentType GetContentTypeFromBinaryHeader(byte[] content)
        {
            if (content == null || content.Length < 4)
                return ContentType.undefined;
                
            // Read magic bytes
            string magic = Encoding.UTF8.GetString(content, 0, 4);
            
            switch (magic)
            {
                case "b3dm":
                    return ContentType.b3dm;
                case "pnts":
                    return ContentType.pnts;
                case "i3dm":
                    return ContentType.i3dm;
                case "cmpt":
                    return ContentType.cmpt;
                case "glTF":
                    return ContentType.glb;
                case "subt":
                    return ContentType.subtree;
                default:
                    return ContentType.undefined;
            }
        }

        /// <summary>
        /// Get import settings for GLTFast loading
        /// </summary>
        private static ImportSettings GetImportSettings()
        {
            return new ImportSettings() { AnimationMethod = AnimationMethod.None };
        }

        /// <summary>
        /// Extract RTC center from GLB data
        /// </summary>
        private static double[] GetRTCCenterFromGlb(byte[] GlbData)
        {
            int jsonstart = 20;
            int jsonlength = (GlbData[15]) * 256;
            jsonlength = (jsonlength + GlbData[14]) * 256;
            jsonlength = (jsonlength + GlbData[13]) * 256;
            jsonlength = (jsonlength + GlbData[12]);

            string gltfjsonstring = Encoding.UTF8.GetString(GlbData, jsonstart, jsonlength);

            if (gltfjsonstring.Length > 0)
            {
                JSONNode rootnode = JSON.Parse(gltfjsonstring);
                JSONNode extensionsNode = rootnode["extensions"];
                if (extensionsNode == null)
                {
                    return null;
                }
                JSONNode cesiumRTCNode = extensionsNode["CESIUM_RTC"];
                if (cesiumRTCNode == null)
                {
                    return null;
                }
                JSONNode centernode = cesiumRTCNode["center"];
                if (centernode == null)
                {
                    return null;
                }

                double[] rtcCenter = new double[3];

                for (int i = 0; i < 3; i++)
                {
                    rtcCenter[i] = centernode[i].AsDouble;
                }
                return rtcCenter;
            }

            return null;
        }


        /// <summary>
        /// Extract RTC center from B3DM feature table
        /// </summary>
        private static double[] GetRTCCenterFromB3dm(B3dm b3dm)
        {
            string batchttableJSONstring = b3dm.FeatureTableJson;
            JSONNode root = JSON.Parse(batchttableJSONstring);
            JSONNode centernode = root["RTC_CENTER"];
            if (centernode == null)
            {
                return null;
            }
            if (centernode.Count != 3)
            {
                return null;
            }
            double[] result = new double[3];
            result[0] = centernode[0].AsDouble;
            result[1] = centernode[1].AsDouble;
            result[2] = centernode[2].AsDouble;
            return result;
        }

        /// <summary>
        /// Remove Cesium RTC from required extensions
        /// </summary>
        private static void RemoveCesiumRtcFromRequieredExtentions(ref byte[] GlbData)
        {
            int jsonstart = 20;
            int jsonlength = (GlbData[15]) * 256;
            jsonlength = (jsonlength + GlbData[14]) * 256;
            jsonlength = (jsonlength + GlbData[13]) * 256;
            jsonlength = (jsonlength + GlbData[12]);

            string jsonstring = Encoding.UTF8.GetString(GlbData, jsonstart, jsonlength);

            JSONNode gltfJSON = JSON.Parse(jsonstring);
            JSONNode extensionsRequiredNode = gltfJSON["extensionsRequired"];
            if (extensionsRequiredNode == null)
            {
                return;
            }
            int extensionsRequiredCount = extensionsRequiredNode.Count;
            int cesiumRTCIndex = -1;
            for (int ii = 0; ii < extensionsRequiredCount; ii++)
            {
                if (extensionsRequiredNode[ii].Value == "CESIUM_RTC")
                {
                    cesiumRTCIndex = ii;
                }
            }
            if (cesiumRTCIndex < 0)
            {
                return;
            }

            if (extensionsRequiredCount == 1)
            {
                gltfJSON.Remove(extensionsRequiredNode);
            }
            else
            {
                extensionsRequiredNode.Remove(cesiumRTCIndex);
            }
            jsonstring = gltfJSON.ToString();

            byte[] resultbytes = Encoding.UTF8.GetBytes(jsonstring);

            int i = 0;
            for (i = 0; i < resultbytes.Length; i++)
            {
                GlbData[jsonstart + i] = resultbytes[i];
            }
            for (int j = i; j < jsonlength; j++)
            {
                GlbData[jsonstart + j] = 0x20;
            }
        }

        /// <summary>
        /// Remove Cesium RTC from required extensions in B3DM
        /// </summary>
        private static void RemoveCesiumRtcFromRequieredExtentions(ref B3dm b3dm)
        {
            int jsonstart = 20;
            int jsonlength = (b3dm.GlbData[15]) * 256;
            jsonlength = (jsonlength + b3dm.GlbData[14]) * 256;
            jsonlength = (jsonlength + b3dm.GlbData[13]) * 256;
            jsonlength = (jsonlength + b3dm.GlbData[12]);

            string jsonstring = Encoding.UTF8.GetString(b3dm.GlbData, jsonstart, jsonlength);

            JSONNode gltfJSON = JSON.Parse(jsonstring);
            JSONNode extensionsRequiredNode = gltfJSON["extensionsRequired"];
            if (extensionsRequiredNode == null)
            {
                return;
            }
            int extensionsRequiredCount = extensionsRequiredNode.Count;
            int cesiumRTCIndex = -1;
            for (int ii = 0; ii < extensionsRequiredCount; ii++)
            {
                if (extensionsRequiredNode[ii].Value == "CESIUM_RTC")
                {
                    cesiumRTCIndex = ii;
                }
            }
            if (cesiumRTCIndex < 0)
            {
                return;
            }

            if (extensionsRequiredCount == 1)
            {
                gltfJSON.Remove(extensionsRequiredNode);
            }
            else
            {
                extensionsRequiredNode.Remove(cesiumRTCIndex);
            }
            jsonstring = gltfJSON.ToString();

            byte[] resultbytes = Encoding.UTF8.GetBytes(jsonstring);

            int i = 0;
            for (i = 0; i < resultbytes.Length; i++)
            {
                b3dm.GlbData[jsonstart + i] = resultbytes[i];
            }
            for (int j = i; j < jsonlength; j++)
            {
                b3dm.GlbData[jsonstart + j] = 0x20;
            }
        }

        #endregion
    }
}
