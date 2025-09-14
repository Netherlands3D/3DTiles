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
        
        // Lazy-initialized GltfImport with consistent configuration
        private GLTFast.GltfImport _gltfImportObject;
        public GLTFast.GltfImport GltfImportObject
        {
            get
            {
                if (_gltfImportObject == null)
                {
                    var consoleLogger = new GLTFast.Logging.ConsoleLogger();
                    var materialGenerator = new NL3DMaterialGenerator();
                    _gltfImportObject = new GLTFast.GltfImport(null, null, materialGenerator, consoleLogger);
                }
                return _gltfImportObject;
            }
        }
        
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
        /// After parsing gltf content spawn gltf scenes
        /// </summary>
        /// 
        private void FinishedLoading(bool succes)
        {
            State = ContentLoadState.DOWNLOADED;
            onDoneDownloading.Invoke();
            onTileLoadCompleted.Invoke(this);
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

            // Dispose GltfImport instance to free native memory
            if (_gltfImportObject != null)
            {
                _gltfImportObject.Dispose();
                _gltfImportObject = null;
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
            
            var success = true;
            Uri uri = null;
            if (sourceUri != "")
            {
                uri = new Uri(sourceUri);
            }
    
            // Use shared GltfImportObject instance directly
            success = await GltfImportObject.Load(b3dm.GlbData, uri, GetImportSettings()); // Use modern Load method instead of obsolete LoadGltfBinary

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

            try
            {
                // Check if component is still valid before proceeding
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed, canceling B3DM processing");
                    return false;
                }

                // Spawn scenes directly from Content
                await SpawnGltfScenesAsync(transform, GltfImportObject, rtcCenter, cancellationTokenSource?.Token ?? default
#if SUBOBJECT
                    , b3dm.GlbData
#endif
                );

                // Check again after async operation in case component was destroyed
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed during B3DM processing");
                    return false;
                }

                gameObject.name = sourceUri;

                if (overrideMaterial != null)
                {
                    OverrideAllMaterials(overrideMaterial);
                }

                // Free CPU-side GLTF data to reduce memory in WebGL
                if (_gltfImportObject != null)
                {
                    _gltfImportObject.Dispose();
                    _gltfImportObject = null;
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

            var success = true;
            Uri uri = null;
            if (sourceUri != "")
            {
                uri = new Uri(sourceUri);
            }
            RemoveCesiumRtcFromRequieredExtentions(ref contentBytes);
    
            // Use shared GltfImportObject instance directly
            success = await GltfImportObject.Load(contentBytes, uri, GetImportSettings());

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
            try
            {
                // Check if this component and GameObject are still valid before proceeding
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component or GameObject destroyed, canceling GLB processing");
                    return false;
                }

                // Additional validation before spawning scenes
                if (GltfImportObject == null)
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
                        await SpawnGltfScenesAsync(transform, GltfImportObject, rtcCenter, cancellationTokenSource?.Token ?? default);
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

                if (overrideMaterial != null)
                {
                    OverrideAllMaterials(overrideMaterial);
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

            var success = true;
            Uri uri = null;
            if (sourceUri != "")
            {
                uri = new Uri(sourceUri);
            }
    
            // Use shared GltfImportObject instance directly
            success = await GltfImportObject.Load(uri);

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

            try
            {
                // Check if component is still valid before proceeding
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed, canceling GLTF processing");
                    return false;
                }

                await SpawnGltfScenesAsync(transform, GltfImportObject, null, cancellationTokenSource?.Token ?? default);

                // Check again after async operation in case component was destroyed
                if (this == null || gameObject == null || transform == null)
                {
                    Debug.LogWarning("Content component destroyed during GLTF processing");
                    return false;
                }

                gameObject.name = sourceUri;

                if (overrideMaterial != null)
                {
                    OverrideAllMaterials(overrideMaterial);
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

        #region Inlined Parsed GLTF helpers

        private async Task SpawnGltfScenesAsync(Transform parent, GLTFast.GltfImport gltfImport, double[] rtcCenter, System.Threading.CancellationToken cancellationToken = default
#if SUBOBJECT
            , byte[] glbBuffer = null
#endif
        )
        {
            if (parent == null)
            {
                Debug.LogError("SpawnGltfScenesAsync: parent Transform is null");
                return;
            }
            if (gltfImport == null)
            {
                Debug.LogError("SpawnGltfScenesAsync: gltfImport is null");
                return;
            }

            try
            {
                var scenes = gltfImport.SceneCount;
                for (int i = 0; i < scenes; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Instantiate scene i
                    await SafeInstantiateSceneAsync(gltfImport, parent, i, cancellationToken);

                    // Protect against destroyed parent
                    if (parent == null)
                    {
                        Debug.LogWarning($"SpawnGltfScenesAsync: parent destroyed during scene {i} instantiation");
                        return;
                    }

                    // Set layers
                    var scene = parent.GetChild(i).transform;
                    if (scene != null)
                    {
                        foreach (var child in scene.GetComponentsInChildren<Transform>(true))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (child != null && child.gameObject != null)
                            {
                                child.gameObject.layer = parent.gameObject.layer;
                            }
                        }

                        // Position scene
                        if (ParentTile != null)
                        {
                            PositionGameObject(scene, rtcCenter, ParentTile);
                        }
                    }
                }

#if SUBOBJECT
                // Optional: parse metadata/subobjects here if needed in the future using glbBuffer
#endif
            }
            catch (System.OperationCanceledException)
            {
                Debug.Log("SpawnGltfScenesAsync: Operation was cancelled");
            }
        }

        private async Task SafeInstantiateSceneAsync(GLTFast.GltfImport gltfImport, Transform parent, int sceneIndex, System.Threading.CancellationToken cancellationToken)
        {
            if (gltfImport == null) throw new System.ObjectDisposedException("gltfImport");
            if (parent == null) throw new UnityEngine.MissingReferenceException("parent Transform is null or destroyed");

            cancellationToken.ThrowIfCancellationRequested();
            await gltfImport.InstantiateSceneAsync(parent, sceneIndex);
        }

        private void OverrideAllMaterials(UnityEngine.Material material)
        {
            if (this == null || gameObject == null) return;
            foreach (var renderer in gameObject.GetComponentsInChildren<Renderer>())
            {
                renderer.material = material;
            }
        }

        private void PositionGameObject(Transform scene, double[] rtcCenter, Tile tile)
        {
            if (scene == null || tile == null || tile.content == null) return;

            Matrix4x4 BasisMatrix = Matrix4x4.TRS(scene.position, scene.rotation, scene.localScale);
            TileTransform basistransform = new TileTransform()
            {
                m00 = BasisMatrix.m00, m01 = BasisMatrix.m01, m02 = BasisMatrix.m02, m03 = BasisMatrix.m03,
                m10 = BasisMatrix.m10, m11 = BasisMatrix.m11, m12 = BasisMatrix.m12, m13 = BasisMatrix.m13,
                m20 = BasisMatrix.m20, m21 = BasisMatrix.m21, m22 = BasisMatrix.m22, m23 = BasisMatrix.m23,
                m30 = BasisMatrix.m30, m31 = BasisMatrix.m31, m32 = BasisMatrix.m32, m33 = BasisMatrix.m33,
            };

            TileTransform gltFastToGLTF = new TileTransform() { m00 = -1d, m11 = 1, m22 = 1, m33 = 1 };
            TileTransform yUpToZUp = new TileTransform() { m00 = 1d, m12 = -1d, m21 = 1, m33 = 1d };

            TileTransform geometryInECEF = yUpToZUp * gltFastToGLTF * basistransform;
            TileTransform geometryInCRS = tile.tileTransform * geometryInECEF;

            TileTransform ECEFToUnity = new TileTransform() { m01 = 1d, m12 = 1d, m20 = -1d, m33 = 1d };
            TileTransform geometryInUnity = ECEFToUnity * geometryInCRS;

            Matrix4x4 final = new Matrix4x4()
            {
                m00 = (float)geometryInUnity.m00, m01 = (float)geometryInUnity.m01, m02 = (float)geometryInUnity.m02, m03 = 0f,
                m10 = (float)geometryInUnity.m10, m11 = (float)geometryInUnity.m11, m12 = (float)geometryInUnity.m12, m13 = 0f,
                m20 = (float)geometryInUnity.m20, m21 = (float)geometryInUnity.m21, m22 = (float)geometryInUnity.m22, m23 = 0f,
                m30 = 0f, m31 = 0f, m32 = 0f, m33 = 1f
            };

            final.Decompose(out Vector3 translation, out Quaternion rotation, out Vector3 scale);

            Coordinate sceneCoordinate = new Coordinate(tile.content.contentcoordinateSystem, geometryInCRS.m03, geometryInCRS.m13, geometryInCRS.m23);
            if (rtcCenter != null)
            {
                sceneCoordinate = new Coordinate(tile.content.contentcoordinateSystem, rtcCenter[0], rtcCenter[1], rtcCenter[2]) + sceneCoordinate;
            }

            rotation = Quaternion.AngleAxis(90, Vector3.up) * rotation;
            tile.content.contentCoordinate = sceneCoordinate;

            scene.localScale = scale;
            var scenepos = scene.gameObject.AddComponent<ScenePosition>();
            scenepos.contentposition = sceneCoordinate;
            scene.position = sceneCoordinate.ToUnity();
            scene.rotation = sceneCoordinate.RotationToLocalGravityUp() * rotation;
        }

        #endregion
    }
}
