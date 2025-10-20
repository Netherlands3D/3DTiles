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

        #region Memory tuning options
        //Call UploadMeshData(true) on all spawned meshes to release CPU-side mesh data.")]
        public bool makeMeshesNoLongerReadable = true;
        //Attempt to make textures non-readable to release CPU-side texture data.")]
        public bool makeTexturesNoLongerReadable = true;
        //Call Resources.UnloadUnusedAssets (and optional GC.Collect) after spawning a tile.")]
        public bool unloadUnusedAssetsAfterSpawn = false;
        //When unloading unused assets, also force a GC.Collect (can stall; use sparingly).")]
        public bool forceGCCollect = false;
        //Clamp texture size after load. 0 disables downscaling.")]
        public int maxTextureSize = 2048;
        //Set all textures to Bilinear and anisoLevel=1 to limit sampling cost.")]
        public bool simplifyTextureSampling = true;
        #endregion

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
        public static Action<Content> OnContentCreated;

        #region Debug/Logging
        //Always log URLs of b3dm/glb content being requested/processed")] 
        private bool logContentUrls = false;
        #endregion

        // Cancellation token for async operations
        private System.Threading.CancellationTokenSource cancellationTokenSource;
        private Coroutine runningDownloadCoroutine;

        private void Awake()
        {
            OnContentCreated?.Invoke(this);
        }



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
        public void Load(UnityEngine.Material overrideMaterial = null, Dictionary<string, string> headers = null)
        {
            if (State == ContentLoadState.DOWNLOADING || State == ContentLoadState.DOWNLOADED)
                return;

            this.headers = headers;
            if (overrideMaterial != null)
            {
                this.overrideMaterial = overrideMaterial;
            }

            if (logContentUrls && !string.IsNullOrEmpty(uri))
            {
                Debug.Log($"[Tiles] Queue content: {uri}");
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

            // Clean up skinned meshes as well
            var skinnedRenderers = this.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>();
            foreach (var smr in skinnedRenderers)
            {
                if (smr.sharedMesh != null)
                {
                    var mesh = smr.sharedMesh;
                    smr.sharedMesh = null;
                    mesh.Clear();
                    Destroy(mesh);
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

            // Optionally unload unused assets after disposal
            if (unloadUnusedAssetsAfterSpawn)
            {
                _ = Resources.UnloadUnusedAssets();
                if (forceGCCollect) System.GC.Collect();
            }
        }

        //todo we need to come up with a way to get all used texture slot property names from the gltf package
        private void ClearRenderers(Renderer[] renderers)
        {
            foreach (Renderer r in renderers)
            {
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                int mainTexNameID = NL3DShaders.MainTextureShaderProperty;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;

                    // If an override material is used, just detach it from the renderer
                    if (overrideMaterial != null && mat == overrideMaterial)
                    {
                        mats[i] = null;
                        continue;
                    }

                    // Clear commonly used texture slots to ensure WebGL textures are released
                    TryClearAndDestroyTexture(mat, mainTexNameID);
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_MainTexture"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_BaseMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_BaseColorMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_MetallicGlossMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_SpecGlossMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_BumpMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_NormalMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_OcclusionMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_EmissionMap"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_SecondaryTexture"));
                    TryClearAndDestroyTexture(mat, Shader.PropertyToID("_MaskTexture"));
                    UnityEngine.Object.Destroy(mat);
                    mats[i] = null;
                }
                r.sharedMaterials = mats;
            }
        }

        private static void TryClearAndDestroyTexture(Material mat, int propId)
        {
            if (!mat || !mat.HasProperty(propId)) return;
            var tex = mat.GetTexture(propId);
            if (tex != null)
            {
                mat.SetTexture(propId, null);
                UnityEngine.Object.Destroy(tex);
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

            try
            {
                return await tcs.Task;
            }
            finally
            {
                runningDownloadCoroutine = null;
            }
        }

        /// <summary>
        /// Coroutine that handles the actual download
        /// </summary>
        private IEnumerator DownloadContentCoroutine(string url, Dictionary<string, string> customHeaders, TaskCompletionSource<byte[]> tcs)
        {
            using (var webRequest = UnityWebRequest.Get(url))
            {
                webRequest.disposeDownloadHandlerOnDispose = true;
                webRequest.timeout = 30;
                if (customHeaders != null)
                {
                    foreach (var header in customHeaders)
                        webRequest.SetRequestHeader(header.Key, header.Value);
                }

                if (logContentUrls)
                {
                    Debug.Log($"[Tiles] Content URL: {url}");
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
                if (logContentUrls && !string.IsNullOrEmpty(sourceUri))
                {
                    Debug.Log($"[Tiles] Processing content URL: {sourceUri} ({contentType})");
                }
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

            // Parse B3DM in-place (no long-lived helper object) to reduce temporary allocations
            string featureTableJson = null;
            byte[] featureTableBinary = null;
            string batchTableJson = null;
            byte[] batchTableBinary = null;
            byte[] glbData = null;

            var memoryStream = new System.IO.MemoryStream(contentBytes, writable: false);
            try
            {
                using (var reader = new System.IO.BinaryReader(memoryStream, Encoding.UTF8, leaveOpen: true))
                {
                    // Read header
                    var magic = Encoding.UTF8.GetString(reader.ReadBytes(4));
                    var version = reader.ReadUInt32();
                    var fileLength = (int)reader.ReadUInt32();

                    int featureTableJsonLength = (int)reader.ReadUInt32();
                    int featureTableBinaryLength = (int)reader.ReadUInt32();
                    int batchTableJsonLength = (int)reader.ReadUInt32();
                    int batchTableBinaryLength = (int)reader.ReadUInt32();

                    // Read feature table
                    if (featureTableJsonLength > 0)
                        featureTableJson = Encoding.UTF8.GetString(reader.ReadBytes(featureTableJsonLength));
                    else
                        featureTableJson = null;
                    if (featureTableBinaryLength > 0)
                        featureTableBinary = reader.ReadBytes(featureTableBinaryLength);

                    // Read batch table
                    if (batchTableJsonLength > 0)
                        batchTableJson = Encoding.UTF8.GetString(reader.ReadBytes(batchTableJsonLength));
                    if (batchTableBinaryLength > 0)
                        batchTableBinary = reader.ReadBytes(batchTableBinaryLength);

                    // Read GLB header (12 bytes) and total GLB length
                    var remaining = fileLength - (28 + featureTableJsonLength + featureTableBinaryLength + batchTableJsonLength + batchTableBinaryLength);
                    if (remaining < 12)
                    {
                        throw new System.IO.EndOfStreamException("B3DM GLB segment too small to contain header");
                    }

                    byte[] glbHeader = reader.ReadBytes(12);
                    if (glbHeader.Length != 12) throw new System.IO.EndOfStreamException("Failed to read GLB header from B3DM");

                    int totalGlbLength = glbHeader[11] * 256;
                    totalGlbLength = (totalGlbLength + glbHeader[10]) * 256;
                    totalGlbLength = (totalGlbLength + glbHeader[9]) * 256;
                    totalGlbLength = totalGlbLength + glbHeader[8];

                    if (totalGlbLength < 12) throw new System.IO.InvalidDataException($"Invalid GLB length {totalGlbLength}");

                    glbData = new byte[totalGlbLength];
                    System.Buffer.BlockCopy(glbHeader, 0, glbData, 0, 12);
                    int bytesToRead = totalGlbLength - 12;
                    int read = reader.Read(glbData, 12, bytesToRead);
                    if (read != bytesToRead) throw new System.IO.EndOfStreamException($"Expected {bytesToRead} GLB bytes, got {read}");
                }
            }
            finally
            {
                // Dispose memoryStream but keep glbData and other locals
                memoryStream.Dispose();
                memoryStream = null;
                contentBytes = null;
            }

            double[] rtcCenter = GetRTCCenterFromFeatureTableJson(featureTableJson);

            // If no RTC center in featureTable, look in GLB JSON
            if (rtcCenter == null)
            {
                rtcCenter = GetRTCCenterFromGlb(glbData);
            }

            // Optionally remove CESIUM_RTC from GLB JSON
            RemoveCesiumRtcFromRequieredExtentions(ref glbData);

            var success = true;
            Uri uri = null;
            if (sourceUri != "")
            {
                uri = new Uri(sourceUri);
            }

            var import = await GltfImportPool.Acquire();
            bool disposeImport = false;
            try
            {
                success = await import.Load(glbData, uri, GetImportSettings());

                if (!IsAlive())
                {
                    Debug.LogWarning("Content component destroyed during B3DM loading");
                    disposeImport = true;
                    return false;
                }

                if (!success)
                {
                    Debug.Log("cant load b3dm: " + sourceUri);
                    disposeImport = true;
                    return false;
                }

                var finalized = await FinalizeAfterSuccessfulLoadAsync(import, rtcCenter, sourceUri
#if SUBOBJECT
                    , glbData
#endif
                );

                if (!finalized)
                {
                    disposeImport = true;
                }

                return finalized;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing B3DM content: {ex.Message}");
                disposeImport = true;
                return false;
            }
            finally
            {
                ReturnImport(import, disposeImport);
                // Clear local buffers so GC can reclaim memory
                glbData = null;
                featureTableBinary = null;
                batchTableBinary = null;
                featureTableJson = null;
                batchTableJson = null;
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
            // Validate data before processing (must be before we clear the buffer)
            if (contentBytes == null || contentBytes.Length == 0)
            {
                Debug.LogError("GLB data is null or empty");
                return false;
            }

            // Optionally patch GLB JSON to remove RTC extension
            RemoveCesiumRtcFromRequieredExtentions(ref contentBytes);

            // Extract RTC center before we drop the buffer
            double[] rtcCenter = GetRTCCenterFromGlb(contentBytes);

            var import = await GltfImportPool.Acquire();
            bool disposeImport = false;
            try
            {
                success = await import.Load(contentBytes, uri, GetImportSettings());
                contentBytes = null;

                if (!IsAlive())
                {
                    Debug.LogWarning("Content component destroyed during GLB loading");
                    disposeImport = true;
                    return false;
                }

                if (!success)
                {
                    Debug.Log("cant load glb: " + sourceUri);
                    disposeImport = true;
                    return false;
                }

                var finalized = await FinalizeAfterSuccessfulLoadAsync(import, rtcCenter, sourceUri);
                if (!finalized)
                {
                    disposeImport = true;
                }
                return finalized;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing GLB content: {ex.Message}");
                disposeImport = true;
                return false;
            }
            finally
            {
                ReturnImport(import, disposeImport);
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

            var import = await GltfImportPool.Acquire();
            bool disposeImport = false;
            try
            {
                success = await import.Load(uri);

                if (!IsAlive())
                {
                    Debug.LogWarning("Content component destroyed during GLTF loading");
                    disposeImport = true;
                    return false;
                }

                if (!success)
                {
                    Debug.Log("cant load gltf: " + sourceUri);
                    disposeImport = true;
                    return false;
                }

                var finalized = await FinalizeAfterSuccessfulLoadAsync(import, null, sourceUri);
                if (!finalized)
                {
                    disposeImport = true;
                }
                return finalized;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing GLTF content: {ex.Message}");
                disposeImport = true;
                return false;
            }
            finally
            {
                ReturnImport(import, disposeImport);
            }
        }

        private bool IsAlive()
        {
            return this != null && gameObject != null && transform != null;
        }

        private static void ReturnImport(GLTFast.GltfImport import, bool dispose)
        {
            if (dispose)
            {
                GltfImportPool.ReleaseAndDispose(import);
            }
            else
            {
                GltfImportPool.Release(import);
            }
        }

        private async Task<bool> FinalizeAfterSuccessfulLoadAsync(GLTFast.GltfImport import, double[] rtcCenter, string sourceUri
#if SUBOBJECT
            , byte[] glbBuffer = null
#endif
        )
        {
            // Check if this component and GameObject are still valid before proceeding
            if (!IsAlive())
            {
                Debug.LogWarning("Content component destroyed, canceling processing");
                return false;
            }

            if (import == null)
            {
                Debug.LogError("GltfImport is null, cannot spawn scenes");
                return false;
            }

            // Add timeout to prevent hanging
            var timeout = System.TimeSpan.FromSeconds(30);
            using (var cts = new System.Threading.CancellationTokenSource(timeout))
            {
                try
                {
                    await SpawnGltfScenesAsync(transform, import, rtcCenter, cancellationTokenSource?.Token ?? default
#if SUBOBJECT
                        , glbBuffer
#endif
                    );
                }
                catch (System.OperationCanceledException)
                {
                    Debug.LogError("Content processing timed out during scene instantiation");
                    return false;
                }
            }

            if (!IsAlive())
            {
                Debug.LogWarning("Content component destroyed during processing");
                return false;
            }

            gameObject.name = sourceUri;

            if (overrideMaterial != null)
            {
                OverrideAllMaterials(overrideMaterial);
            }

            // Parse asset metadata if enabled (for copyright attribution, etc.)
            if (parseAssetMetaData)
            {
                ParseAndNotifyAssetMetadata(import);
            }

            // Optionally release CPU-side copies
            if (makeMeshesNoLongerReadable || makeTexturesNoLongerReadable || simplifyTextureSampling || maxTextureSize > 0)
            {
                ReleaseCpuCopies(makeMeshesNoLongerReadable, makeTexturesNoLongerReadable);
                TuneTextures(simplifyTextureSampling, maxTextureSize);
            }

            // Optionally unload unused assets/force GC to return memory
            if (unloadUnusedAssetsAfterSpawn)
            {
                await Resources.UnloadUnusedAssets();
                if (forceGCCollect) System.GC.Collect();
            }

            if (!IsAlive())
            {
                Debug.LogWarning("Content component destroyed before processing completed");
                return false;
            }

            return true;
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
        private static double[] GetRTCCenterFromFeatureTableJson(string featureTableJson)
        {
            if (string.IsNullOrEmpty(featureTableJson)) return null;
            JSONNode root = JSON.Parse(featureTableJson);
            JSONNode centernode = root["RTC_CENTER"];
            if (centernode == null) return null;
            if (centernode.Count != 3) return null;
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

        // B3dm-specific overload removed. In-place GLB JSON patching should use
        // the byte[] overload above: RemoveCesiumRtcFromRequieredExtentions(ref byte[] GlbData)

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
                try
                {
                    var mats = renderer.sharedMaterials;
                    if (mats == null || mats.Length == 0)
                    {
                        renderer.sharedMaterial = material;
                        continue;
                    }
                    for (int i = 0; i < mats.Length; i++) mats[i] = material;
                    renderer.sharedMaterials = mats;
                }
                catch
                {
                    renderer.sharedMaterial = material;
                }
            }
        }

        // Release CPU copies of meshes and textures to reduce WebGL heap usage
        private void ReleaseCpuCopies(bool releaseMeshes, bool releaseTextures)
        {
            if (this == null || gameObject == null) return;

            if (releaseMeshes)
            {
                var meshFilters = gameObject.GetComponentsInChildren<MeshFilter>(true);
                foreach (var mf in meshFilters)
                {
                    var mesh = mf.sharedMesh;
                    if (mesh != null)
                    {
                        try { mesh.UploadMeshData(true); } catch { }
                    }
                }

                var skinned = gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var smr in skinned)
                {
                    var mesh = smr.sharedMesh;
                    if (mesh != null)
                    {
                        try { mesh.UploadMeshData(true); } catch { }

                    }
                }
            }

            if (releaseTextures)
            {
                var renderers = gameObject.GetComponentsInChildren<Renderer>(true);
                foreach (var r in renderers)
                {
                    var mats = r.sharedMaterials;
                    foreach (var mat in mats)
                    {
                        if (mat == null) continue;
                        // Try common texture slots
                        TryMakeNonReadable(mat, "_MainTexture");
                        TryMakeNonReadable(mat, "_MainTex");
                        TryMakeNonReadable(mat, "_BaseMap");
                        TryMakeNonReadable(mat, "_BaseColorMap");
                        TryMakeNonReadable(mat, "_MetallicGlossMap");
                        TryMakeNonReadable(mat, "_BumpMap");
                        TryMakeNonReadable(mat, "_OcclusionMap");
                        TryMakeNonReadable(mat, "_EmissionMap");
                        TryMakeNonReadable(mat, "_SecondaryTexture");
                        TryMakeNonReadable(mat, "_MaskTexture");
                    }
                }
            }
        }

        private static void TryMakeNonReadable(Material mat, string prop)
        {
            if (!mat.HasProperty(prop)) return;
            var tex = mat.GetTexture(prop) as Texture2D;
            if (tex == null) return;
            try
            {
                tex.Apply(false, true); // makeNoLongerReadable
            }
            catch { }
        }

        // Reduce texture sampling cost and optionally downscale too-large textures
        private void TuneTextures(bool simplifySampling, int maxSize)
        {
            if (this == null || gameObject == null) return;

            var processed = new System.Collections.Generic.HashSet<Texture2D>();
            var renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                foreach (var mat in mats)
                {
                    if (mat == null) continue;
                    TuneTextureProperty(mat, "_MainTex", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_MainTexture", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_BaseMap", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_BaseColorMap", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_MetallicGlossMap", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_BumpMap", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_OcclusionMap", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_EmissionMap", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_SecondaryTexture", simplifySampling, maxSize, processed);
                    TuneTextureProperty(mat, "_MaskTexture", simplifySampling, maxSize, processed);
                }
            }
        }

        private void TuneTextureProperty(Material mat, string prop, bool simplifySampling, int maxSize, System.Collections.Generic.HashSet<Texture2D> processed)
        {
            if (!mat.HasProperty(prop)) return;
            var tex = mat.GetTexture(prop) as Texture2D;
            if (tex == null) return;
            if (processed.Contains(tex)) return;
            processed.Add(tex);

            if (simplifySampling)
            {
                tex.anisoLevel = 1;
                tex.filterMode = FilterMode.Bilinear;
            }

            if (maxSize > 0 && (tex.width > maxSize || tex.height > maxSize))
            {
                var targetW = tex.width;
                var targetH = tex.height;
                float scale = (float)maxSize / Mathf.Max(tex.width, tex.height);
                targetW = Mathf.Max(1, Mathf.RoundToInt(tex.width * scale));
                targetH = Mathf.Max(1, Mathf.RoundToInt(tex.height * scale));

                var downsized = DownscaleTexture(tex, targetW, targetH);
                if (downsized != null)
                {
                    mat.SetTexture(prop, downsized);
                    Destroy(tex);
                }
            }
        }

        private Texture2D DownscaleTexture(Texture2D src, int width, int height)
        {
            try
            {
                var prevActive = RenderTexture.active;
                var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                tex.Apply(false, true); // no mipmaps, non-readable
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                return tex;
            }
            catch
            {
                return null;
            }
        }

        private void PositionGameObject(Transform scene, double[] rtcCenter, Tile tile)
        {
            if (scene == null || tile == null || tile.content == null) return;

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

            TileTransform gltFastToGLTF = new TileTransform() { m00 = -1d, m11 = 1, m22 = 1, m33 = 1 };
            TileTransform yUpToZUp = new TileTransform() { m00 = 1d, m12 = -1d, m21 = 1, m33 = 1d };

            TileTransform geometryInECEF = yUpToZUp * gltFastToGLTF * basistransform;
            TileTransform geometryInCRS = tile.tileTransform * geometryInECEF;

            TileTransform ECEFToUnity = new TileTransform() { m01 = 1d, m12 = 1d, m20 = -1d, m33 = 1d };
            TileTransform geometryInUnity = ECEFToUnity * geometryInCRS;

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
                m31 = 0f,
                m32 = 0f,
                m33 = 1f
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

        /// <summary>
        /// Parse asset metadata from GLB and notify listeners
        /// </summary>
        private void ParseAndNotifyAssetMetadata(GLTFast.GltfImport import)
        {
            try
            {
                // Check if the GLTFast import has asset data available
                if (import?.GetSourceRoot()?.Asset != null)
                {
                    var assetData = import.GetSourceRoot().Asset;

                    // Create ContentMetadata component to hold the asset information
                    var contentMetadataComponent = gameObject.AddComponent<ContentMetadata>();

                    // Convert GLTFast asset to our GltfMeshFeatures.Asset format
                    contentMetadataComponent.asset = new GltfMeshFeatures.Asset()
                    {
                        copyright = assetData.copyright,
                        generator = assetData.generator,
                        version = assetData.version,
                        minVersion = assetData.minVersion
                    };

                    // Notify the Read3DTileset that we have asset metadata
                    if (tilesetReader != null && contentMetadataComponent.asset != null)
                    {
                        tilesetReader.OnLoadAssetMetadata.Invoke(contentMetadataComponent);
                        Debug.Log($"Content: Loaded asset metadata - Copyright: {contentMetadataComponent.asset.copyright}");
                    }
                }
                else
                {
                    Debug.LogWarning("Content: No asset metadata found in GLB");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Content: Error parsing asset metadata: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            // Ensure parent tile releases its reference even if we get destroyed externally
            if (parentTile != null && parentTile.content == this)
            {
                parentTile.content = null;
            }

            // Defensive: stop pending async work so no callbacks run on a destroyed component
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }

            if (runningDownloadCoroutine != null)
            {
                StopCoroutine(runningDownloadCoroutine);
                runningDownloadCoroutine = null;
            }

            onDoneDownloading.RemoveAllListeners();
            onTileLoadCompleted.RemoveAllListeners();
        }


        #endregion
    }
}
