using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SimpleJSON;
using UnityEngine.Networking;
using System;
using System.Linq;
using System.Text;
using System.Collections.Specialized;
using UnityEngine.Events;
using GLTFast;
using Netherlands3D.Coordinates;
using System.Text.RegularExpressions;



#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Netherlands3D.Tiles3D
{
    [RequireComponent(typeof(ReadSubtree))]
    public class Read3DTileset : MonoBehaviour
    {
        public string tilesetUrl = "https://storage.googleapis.com/ahp-research/maquette/kadaster/3dbasisvoorziening/test/landuse_1_1/tileset.json";
        public CoordinateSystem contentCoordinateSystem = CoordinateSystem.WGS84_ECEF;
       
        [Header("API Key (Optional)")]
        [Tooltip("Public API key for production use. This key will be used in production builds.")]
        public string publicKey;
        [Tooltip("Personal API key for testing purposes. This key will override the public key in Unity editor.")]
        public string personalKey;
        [Tooltip("The key name to use for the API key in the query string like 'key', or 'code' etc. Default is 'key' for Google Maps API.")]
        [SerializeField] private string queryKeyName = "key";
        public string QueryKeyName { get => queryKeyName; set => queryKeyName = value; }

        private string absolutePath = "";
        private string rootPath = "";
        private NameValueCollection queryParameters;

        [Header("Tileset")]
        public Tile root;

        TilingMethod tilingMethod = TilingMethod.ExplicitTiling;

        public ImplicitTilingSettings implicitTilingSettings;

        public bool parseAssetMetadata = false;
#if SUBOBJECT
        public bool parseSubObjects = false;
#endif

        [Tooltip("Limits amount of detail higher resolution would cause to load.")]
        public int maxScreenHeightInPixels = 1080;
        public int maximumScreenSpaceError = 5;
        [SerializeField] private float sseComponent = -1;
        private List<Tile> visibleTiles = new List<Tile>();

        [SerializeField] private int maxSimultaneousDownloads = 6;
        [SerializeField] private float distancePriorityWeight = 1.0f;

        private readonly List<Tile> downloadQueue = new();
        private readonly HashSet<Tile> queuedTiles = new();
        private readonly Dictionary<Tile, PriorityData> priorityData = new();

        private struct PriorityData
        {
            public float Distance;
            public float Score;
            public float ScreenSpaceError;
        }

        private Camera currentCamera;

        internal string tilesetFilename = "tileset.json";

        [Header("Optional material override")] public Material materialOverride;

        [Header("Debugging")] public bool debugLog;
        [Tooltip("When enabled, tiles with content will show a colored plane per tile instead of downloading the GLB. Useful for testing layout and gaps.")]
        private bool useColorPlanes = false;
        [Tooltip("Gap factor between adjacent tiles (0..0.5). 0.02 = 2% gap.")]
        public float colorPlaneGapFactor = 0.02f;
        
        public string[] usedExtensions { get; private set; }

        //Custom WebRequestHeader dictionary
        private Dictionary<string, string> customHeaders = new Dictionary<string, string>();
        public Dictionary<string, string> CustomHeaders { get => customHeaders; private set => customHeaders = value; }

        [Space(2)]
        public UnityEvent<string[]> unsupportedExtensionsParsed;

        [HideInInspector] public UnityEvent<UnityWebRequest> OnServerResponseReceived = new();
        [HideInInspector] public UnityEvent<UnityWebRequest.Result> OnServerRequestFailed = new();
        [HideInInspector] public UnityEvent<ContentMetadata> OnLoadAssetMetadata = new();
        [HideInInspector] public UnityEvent<Content> OnTileLoaded = new();
        
        public string CredentialQuery { get; private set; } = string.Empty;
        
        public void ClearKeyFromURL()
        {
            if (CredentialQuery != string.Empty)
            {
                tilesetUrl = tilesetUrl.Replace(CredentialQuery, string.Empty);
            }
        }
        
        public void ConstructURLWithKey()
        {
            ClearKeyFromURL(); //remove existing key if any is there
            UriBuilder uriBuilder = new UriBuilder(tilesetUrl);

            // Keep an existing query and ensure the leading `?` and, if so, a trailing `&` is stripped
            var queryString = uriBuilder.Query.TrimStart('?').TrimEnd('&') ?? string.Empty;
            if (!string.IsNullOrEmpty(queryString))
            {
                queryString += "&";
            }

            uriBuilder.Query = queryString;

            // Use explicit keys set via the UI (personalKey overrides publicKey)
            string keyToUse = !string.IsNullOrEmpty(personalKey) ? personalKey : publicKey;
            if (!string.IsNullOrEmpty(keyToUse))
            {
                CredentialQuery = $"{QueryKeyName}={keyToUse}";
                uriBuilder.Query += CredentialQuery;
            }
            tilesetUrl = uriBuilder.ToString();
        }

        void Start()
        {
            RefreshTiles();
        }

        private void OnEnable()
        {
            if (root !=null)
            {
                InvalidateBounds();
                StartCoroutine(LoadInView());
            }
            
        }
        public void RefreshTiles()
        {
            StopAllCoroutines();
            DisposeAllTilesRecursive(root);
            root = null;
            visibleTiles = new();
            downloadQueue.Clear();
            queuedTiles.Clear();
            priorityData.Clear();

            InitializeURLAndLoadTileSet();
        }

        /// <summary>
        /// Add custom headers for all internal WebRequests
        /// </summary>
        public void AddCustomHeader(string key, string value, bool replace = true)
        {
            if(replace && customHeaders.ContainsKey(key))
                customHeaders[key] = value;
            else
                customHeaders.Add(key, value);
        }
        
        public void ClearCustomHeaders()
        {
            customHeaders.Clear();
        }

        private void DisposeAllTilesRecursive(Tile tile)
        {
            if (tile == null)
                return;

            if (tile.ChildrenCount > 0)
            {
                foreach (var t in tile.children)
                {
                    DisposeAllTilesRecursive(t);
                }
            }

            DisposeTile(tile, true);
        }

        private void DisposeTile(Tile tile, bool forceDispose = true)
        {
            if (tile == null)
            {
                return;
            }

            queuedTiles.Remove(tile);
            downloadQueue.Remove(tile);

            tile.requestedDispose = false;
            tile.requestedUpdate = false;

            if (forceDispose)
            {
                tile.Dispose();
            }
        }

        void InitializeURLAndLoadTileSet()
        {
            ConstructURLWithKey();

            currentCamera = Camera.main;
            StartCoroutine(LoadInView());

            ExtractDatasetPaths();

            
            StartCoroutine(LoadTileset());
        }

        private void ExtractDatasetPaths()
        {
            Uri uri = new(tilesetUrl);
            absolutePath = tilesetUrl.Substring(0, tilesetUrl.LastIndexOf("/") + 1);
            if (tilesetUrl.StartsWith("file://"))
            {
                rootPath = absolutePath;
            }
            else
            {
                rootPath = uri.GetLeftPart(UriPartial.Authority);
            }

            queryParameters = ParseQueryString(uri.Query);
            
    
            foreach (string segment in uri.Segments)
            {
                if (segment.EndsWith(".json"))
                {
                    tilesetFilename = segment;
                    
                    break;
                }
            }
        }

        /// <summary>
        /// TODO: Use existing nl3d query parser / or move to Uri extention?
        /// </summary>
        /// <param name="queryString">?param=value&otherparam=othervalue</param>
        public NameValueCollection ParseQueryString(string queryString)
        {
            // Remove leading '?' if present
            if (queryString.StartsWith("?"))
                queryString = queryString.Substring(1);

            NameValueCollection queryParameters = new NameValueCollection();

            string[] querySegments = queryString.Split('&');
            for (int i = 0; i < querySegments.Length; i++)
            {
                string[] parts = querySegments[i].Split('=');
                if (parts.Length > 1)
                {
                    string key = UnityWebRequest.UnEscapeURL(parts[0]);
                    string value = UnityWebRequest.UnEscapeURL(parts[1]);
                    queryParameters.Add(key, value);
                }
            }

            return queryParameters;
        }

        /// <summary>
        /// Get URL parameter value from a URL string
        /// </summary>
        /// <param name="url">URL like "https://example.com?param=value&param2=value2"</param>
        /// <param name="param">Parameter name to find</param>
        /// <returns>Parameter value or null if not found</returns>
        private string GetUrlParamValue(string url, string param)
        {
            var groups = Regex.Match(url, $"[?&]{param}=([^&#]*)").Groups;
            if (groups.Count < 2) return null;
            return groups[1].Value;
        }

        /// <summary>
        /// Change camera used by tileset 'in view' calculations
        /// </summary>
        /// <param name="camera">Target camera</param>
        public void SetCamera(Camera camera)
        {
            currentCamera = camera;
        }

        /// <summary>
        /// Initialize tileset with these settings.
        /// This allows you initialize this component via code directly.
        /// </summary>
        /// <param name="tilesetUrl">The url pointing to tileset; https://.../tileset.json</param>
        /// <param name="maximumScreenSpaceError">The maximum screen space error for this tileset (default=5)</param>
        public void Initialize(string tilesetUrl,CoordinateSystem contentCoordinateystem = CoordinateSystem.WGS84_ECEF, int maximumScreenSpaceError = 5)
        {
            currentCamera = Camera.main;
            this.tilesetUrl = tilesetUrl;
            this.maximumScreenSpaceError = maximumScreenSpaceError;
            this.contentCoordinateSystem = contentCoordinateystem;
            RefreshTiles();
        }

        public void RecalculateBounds()
        {
            if (root == null) return;

            
            RecalculateAllTileBounds(root);
        }

        public void InvalidateBounds()
        {
            if (root == null) return;

            //Flag all calculated bounds to be recalculated when tile bounds is requested
            InvalidateAllTileBounds(root);
        }

        /// <summary>
        /// Recursive recalculation of tile bounds
        /// </summary>
        /// <param name="tile">Starting tile</param>
        private void RecalculateAllTileBounds(Tile tile)
        {
            if (tile == null) return;

            tile.CalculateUnitBounds();

            if (tile.ChildrenCount > 0)
            {
                foreach (var child in tile.children)
                {
                    RecalculateAllTileBounds(child);
                }
            }
        }

        /// <summary>
        /// Recursive invalidation of tile bounds
        /// tilebounds will be recaluclated when testing for isInView
        /// </summary>
        /// <param name="tile">Starting tile</param>
        private void InvalidateAllTileBounds(Tile tile)
        {
            if (tile == null) return;

            tile.boundsAvailable = false ;

            if (tile.ChildrenCount > 0)
            {
                foreach (var child in tile.children)
                {
                    InvalidateAllTileBounds(child);
                }
            }
        }

        /// <summary>
        /// IEnumerator to load tileset.json from url
        /// </summary>
        IEnumerator LoadTileset()
        {
            UnityWebRequest www = UnityWebRequest.Get(tilesetUrl);
            foreach (var header in customHeaders)
                www.SetRequestHeader(header.Key, header.Value);

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.Log($"Could not load tileset from url:{tilesetUrl} Error:{www.error}");
                OnServerRequestFailed.Invoke(www.result);
            }
            else
            {
                string jsonstring = www.downloadHandler.text;
                ParseTileset.DebugLog = debugLog;
                ParseTileset.subtreeReader = GetComponent<ReadSubtree>();
                JSONNode rootnode = JSON.Parse(jsonstring)["root"];
                root = ParseTileset.ReadTileset(rootnode,this);
                
                var extensions = ParseTileset.GetUsedExtensions(rootnode);
                usedExtensions = extensions.Item1;
                unsupportedExtensionsParsed.Invoke(extensions.Item2);
            }

            OnServerResponseReceived.Invoke(www);
        }

        // Helper om de diepte van een tile te bepalen (root=0)
        private int GetTileDepth(Tile tile)
        {
            int depth = 0;
            var current = tile.parent;
            while (current != null)
            {
                depth++;
                current = current.parent;
            }
            return depth;
        }

        private void RequestContentUpdate(Tile tile)
        {
            if (tile.content!=null)
            {
                return;
            }
            if (!tile.content)
            {
                int tileDepth = GetTileDepth(tile);
                string parentName = $"Depth_{tileDepth}";
                GameObject parentGO = GameObject.Find(parentName);
                if (parentGO == null)
                {
                    parentGO = new GameObject(parentName);
                    parentGO.transform.SetParent(transform, false);
                }

                // If useColorPlanes is enabled, create a simple placeholder plane and mark content as downloaded
                if (useColorPlanes)
                {
                    var colorPlaneGO = new GameObject($"{tile.level},{tile.X},{tile.Y} colorplane");
                    colorPlaneGO.transform.SetParent(parentGO.transform, false);
                    colorPlaneGO.layer = gameObject.layer;

                    var contentComp = colorPlaneGO.AddComponent<Content>();
                    contentComp.tilesetReader = this;
                    contentComp.State = Content.ContentLoadState.DOWNLOADED;
                    contentComp.ParentTile = tile;
                    tile.content = contentComp;

                    // Create colored quad sized to tile bounds with small gap
                    Bounds b = tile.ContentBounds;
                    if (b.size == Vector3.zero)
                    {
                        tile.CalculateUnitBounds();
                        b = tile.ContentBounds;
                    }

                    float gapFactor = Mathf.Clamp(colorPlaneGapFactor, 0f, 0.5f);
                    Vector3 size = b.size;
                    Vector3 scaled = new Vector3(Mathf.Max(0.001f, size.x * (1f - gapFactor)), 1f, Mathf.Max(0.001f, size.z * (1f - gapFactor)));

                    // Create a quad mesh manually (avoids CreatePrimitive which adds colliders and can produce warnings)
                    var quad = new GameObject("placeholder_quad");
                    quad.transform.SetParent(colorPlaneGO.transform, false);
                    quad.transform.position = b.center;
                    // Force the quad to face world-up so parent rotations don't flip it
                    quad.transform.up = Vector3.up;
                    quad.transform.localScale = new Vector3(scaled.x, 1f, scaled.z);

                    var mf = quad.AddComponent<MeshFilter>();
                    var mr = quad.AddComponent<MeshRenderer>();

                    Mesh mesh = new Mesh();
                    mesh.name = "placeholder_quad_mesh";
                    mesh.vertices = new Vector3[] {
                        new Vector3(-0.5f, 0f, -0.5f),
                        new Vector3(0.5f, 0f, -0.5f),
                        new Vector3(0.5f, 0f, 0.5f),
                        new Vector3(-0.5f, 0f, 0.5f)
                    };
                    mesh.uv = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
                    // Ensure triangle winding produces upward-facing normals (visible from above)
                    mesh.triangles = new int[] { 0,2,1, 0,3,2 };
                    mesh.normals = new Vector3[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
                    // Ensure normals/bounds are consistent with world-up
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    mf.mesh = mesh;

                    // Generate or assign material with color per depth, fall back if shader missing
                    // Robust shader fallback for WebGL builds where some shaders may be stripped
                    Shader colorShader = Shader.Find("Unlit/Color");
                    if (colorShader == null) colorShader = Shader.Find("Sprites/Default");
                    if (colorShader == null) colorShader = Shader.Find("Standard");
                    Material mat;
                    if (colorShader != null)
                    {
                        mat = new Material(colorShader);
                        // Common color property name across shaders
                        if (mat.HasProperty("_Color"))
                        {
                            Color c = Color.HSVToRGB((tileDepth * 0.15f) % 1f, 0.6f, 0.9f);
                            mat.SetColor("_Color", c);
                        }
                    }
                    else
                    {
                        // As a last resort create a default material and tint with vertex color
                        mat = new Material(Shader.Find("Sprites/Default"));
                        Color c = Color.HSVToRGB((tileDepth * 0.15f) % 1f, 0.6f, 0.9f);
                        if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
                    }
                    mr.sharedMaterial = mat;

                    // Add a thin border using LineRenderer to show tile edges
                    var borderGo = new GameObject("border");
                    borderGo.transform.SetParent(colorPlaneGO.transform, false);
                    var lr = borderGo.AddComponent<LineRenderer>();
                    lr.positionCount = 5;
                    lr.loop = true;
                    lr.useWorldSpace = true;
                    lr.startWidth = Mathf.Max(0.01f, Mathf.Min(size.x, size.z) * 0.002f);
                    lr.endWidth = lr.startWidth;
                    Shader borderShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
                    Material borderMat = (borderShader != null) ? new Material(borderShader) : new Material(Shader.Find("Sprites/Default"));
                    if (borderMat.HasProperty("_Color")) borderMat.SetColor("_Color", Color.black);
                    lr.material = borderMat;
                    float hx = scaled.x * 0.5f;
                    float hz = scaled.z * 0.5f;
                    Vector3 center = b.center;
                    lr.SetPosition(0, center + new Vector3(-hx, 0.01f, -hz));
                    lr.SetPosition(1, center + new Vector3(hx, 0.01f, -hz));
                    lr.SetPosition(2, center + new Vector3(hx, 0.01f, hz));
                    lr.SetPosition(3, center + new Vector3(-hx, 0.01f, hz));
                    lr.SetPosition(4, center + new Vector3(-hx, 0.01f, -hz));

                    // Notify listeners that content is available
                    contentComp.onTileLoadCompleted.AddListener(OnTileLoaded.Invoke);
                    contentComp.onTileLoadCompleted.Invoke(contentComp);
                    contentComp.onDoneDownloading.Invoke();
                    return;
                }

                // Default behaviour: create Content component and request real content
                var newContentGameObject = new GameObject($"{tile.level},{tile.X},{tile.Y} content");
                newContentGameObject.transform.SetParent(parentGO.transform, false);
                newContentGameObject.layer = gameObject.layer;
                tile.content = newContentGameObject.AddComponent<Content>();
                tile.content.tilesetReader = this;
                tile.content.State = Content.ContentLoadState.NOTLOADING;
                tile.content.ParentTile = tile;
                tile.content.uri = GetFullContentUri(tile);
                
                tile.content.parseAssetMetaData = parseAssetMetadata;
                tile.content.onTileLoadCompleted.AddListener(OnTileLoaded.Invoke);
#if SUBOBJECT
                tile.content.parseSubObjects = parseSubObjects;
                tile.content.contentcoordinateSystem = contentCoordinateSystem;
#endif

                EnqueueTileForLoad(tile);
            }
            // Log de diepte en contentUri
            int depth = GetTileDepth(tile);
            Debug.Log($"Tile loaded: depth={depth}, contentUri={tile.contentUri}");
        }

        private void EnqueueTileForLoad(Tile tile)
        {
            if (tile == null)
            {
                return;
            }

            var content = tile.content;
            if (content == null)
            {
                return;
            }

            if (content.State != Content.ContentLoadState.NOTLOADING)
            {
                return;
            }

            if (queuedTiles.Add(tile))
            {
                downloadQueue.Add(tile);
                tile.requestedUpdate = true;
            }
        }

        private void ProcessDownloadQueue(Camera camera)
        {
            if (downloadQueue.Count == 0)
            {
                return;
            }

            priorityData.Clear();

            bool hasCamera = camera != null;
            Vector3 cameraPosition = hasCamera ? camera.transform.position : Vector3.zero;

            for (int i = downloadQueue.Count - 1; i >= 0; i--)
            {
                var tile = downloadQueue[i];
                if (tile == null || tile.content == null)
                {
                    queuedTiles.Remove(tile);
                    downloadQueue.RemoveAt(i);
                    continue;
                }

                var content = tile.content;
                if (content.State == Content.ContentLoadState.DOWNLOADED || content.State == Content.ContentLoadState.DOWNLOADING)
                {
                    queuedTiles.Remove(tile);
                    downloadQueue.RemoveAt(i);
                    continue;
                }

                float sse = Mathf.Max(0f, tile.screenSpaceError);
                float score = sse;

                float distance = float.MaxValue;
                if (hasCamera)
                {
                    distance = Vector3.Distance(cameraPosition, tile.ContentBounds.ClosestPoint(cameraPosition));
                    if (!float.IsInfinity(distance) && !float.IsNaN(distance))
                    {
                        float distanceWeight = distancePriorityWeight / Mathf.Max(distance, 0.1f);
                        score *= Mathf.Max(0.01f, distanceWeight);
                    }
                    else
                    {
                        distance = float.MaxValue;
                    }
                }

                priorityData[tile] = new PriorityData
                {
                    Distance = distance,
                    Score = score,
                    ScreenSpaceError = sse
                };

                tile.priority = Mathf.RoundToInt(score);
            }

            if (priorityData.Count == 0)
            {
                downloadQueue.Clear();
                return;
            }

            downloadQueue.Sort((a, b) =>
            {
                var infoA = priorityData[a];
                var infoB = priorityData[b];

                int distanceComparison = infoA.Distance.CompareTo(infoB.Distance);
                if (distanceComparison != 0)
                {
                    return distanceComparison;
                }

                int scoreComparison = infoB.Score.CompareTo(infoA.Score);
                if (scoreComparison != 0)
                {
                    return scoreComparison;
                }

                return infoB.ScreenSpaceError.CompareTo(infoA.ScreenSpaceError);
            });

            int activeDownloads = CountActiveDownloads();
            while (downloadQueue.Count > 0 && activeDownloads < maxSimultaneousDownloads)
            {
                var tile = downloadQueue[0];
                downloadQueue.RemoveAt(0);
                queuedTiles.Remove(tile);
                priorityData.Remove(tile);

                var content = tile.content;
                if (content == null || content.State != Content.ContentLoadState.NOTLOADING)
                {
                    continue;
                }

                StartTileDownload(tile);
                activeDownloads++;
            }
        }


        private void StartTileDownload(Tile tile)
        {
            var content = tile.content;
            if (content == null)
            {
                return;
            }

            UnityEngine.Events.UnityAction onCompleted = null;
            onCompleted = () =>
            {
                content.onDoneDownloading.RemoveListener(onCompleted);
                tile.requestedUpdate = false;
            };

            content.onDoneDownloading.AddListener(onCompleted);
            content.Load(materialOverride);
        }

        private int CountActiveDownloads()
        {
            int count = 0;
            for (int i = 0; i < visibleTiles.Count; i++)
            {
                var tile = visibleTiles[i];
                if (tile?.content != null && tile.content.State == Content.ContentLoadState.DOWNLOADING)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Check what tiles should be loaded/unloaded based on view recursively
        /// </summary>
        private IEnumerator LoadInView()
        {
            yield return new WaitUntil(() => root != null);
            while (true)
            {
                SetSSEComponent(currentCamera);
                DisposeTilesOutsideView(currentCamera);
                if (root.ChildrenCount > 0)
                {
                    foreach (var child in root.children)
                    {
                        LoadInViewRecursively(child, currentCamera);
                    }
                }

                ProcessDownloadQueue(currentCamera);

                yield return null;
            }
        }

        /// <summary>
        /// Check for tiles in our visibile tiles list that moved out of the view / max distance.
        /// Request dispose for tiles that moved out of view
        /// </summary>
        /// <param name="currentCamera">Camera to use for visibility check</param>
        private void DisposeTilesOutsideView(Camera currentCamera)
        {
            for (int i = visibleTiles.Count - 1; i >= 0; i--)
            {
                var tile = visibleTiles[i];
                var closestPointOnBounds = tile.ContentBounds.ClosestPoint(currentCamera.transform.position); //Returns original point when inside the bounds
                CalculateTileScreenSpaceError(tile, currentCamera, closestPointOnBounds);
            }

            //Clean up list op previously loaded tiles outside of view
            for (int i = visibleTiles.Count - 1; i >= 0; i--)
            {
                var tile = visibleTiles[i];
                var tileIsInView = tile.IsInViewFrustum(currentCamera);
                if (!tileIsInView)
                {
                    DisposeTile(tile, true);
                    visibleTiles.RemoveAt(i);
                    continue;
                }


                var enoughDetail = tile.screenSpaceError < maximumScreenSpaceError;
                if (enoughDetail) // tile has (more then) enoug detail
                {
                    if (tile.parent.screenSpaceError<maximumScreenSpaceError) //parent tile also has enough detail
                    {
                        // can be removed if a parentTile is loaded
                        if (tile.parent.CountLoadedParents > 0)
                        {
                            DisposeTile(tile, true);
                            visibleTiles.RemoveAt(i);
                            continue;
                        }
                    }
                   

                }

                else //too little detail
                {
                    if (tile.refine=="ADD")
                    {
                        // tile should remain
                    }
                    else if (tile.CountLoadingChildren == 0)
                    {
                        if (tile.CountLoadedChildren > 0)
                        {
                            DisposeTile(tile);

                            visibleTiles.RemoveAt(i);
                        }
                    }


                }
            }
        }

        private void CalculateTileScreenSpaceError(Tile child, Camera currentMainCamera, Vector3 closestPointOnBounds)
        {
            float sse;
            if (currentMainCamera.orthographic)
            {
                //geometric error has no influence anymore so lets calculate the sse camera values
                Bounds bounds = child.ContentBounds;
                Vector3 extents = bounds.extents;
                Vector3 clampedExtents = Vector3.Min(extents, Vector3.one * 1000);
                float halfHeight = currentMainCamera.orthographicSize;
                float halfWidth = halfHeight * currentMainCamera.aspect;
                float fh2 = halfHeight * 2f;
                float fw2 = halfWidth * 2f;
                float frustumDiagonalSq = fw2 * fw2 + fh2 * fh2;
                float tx = clampedExtents.x * 2f;
                float tz = clampedExtents.z * 2f;
                float tileGroundSizeSq = tx * tx + tz * tz;
                float ratio = 0f;
                if (frustumDiagonalSq > 0f)
                    ratio = Mathf.Sqrt(tileGroundSizeSq / frustumDiagonalSq);

                float zoomFactor = Mathf.Clamp(halfHeight / 10f, 0.1f, 10f);
                float rawSSE = sseComponent * ratio * zoomFactor * 2f;
                sse = Mathf.Max(rawSSE, 0.5f);
            }
            else if (Vector3.Distance(currentMainCamera.transform.position, closestPointOnBounds) < 0.1)
            {
                sse = float.MaxValue;
            }
            else
            {
                sse = (sseComponent * (float)child.geometricError) / Vector3.Distance(currentMainCamera.transform.position, closestPointOnBounds);
            }
            child.screenSpaceError = sse;
        }

        private void LoadInViewRecursively(Tile tile, Camera currentCamera)
        {
            var tileIsInView = tile.IsInViewFrustum(currentCamera);
            if (!tileIsInView)
            {
                return;
            }

            if (tile.isLoading == false && tile.ChildrenCount == 0 && tile.contentUri.Contains(".json"))
            {
                tile.isLoading = true;
                StartCoroutine(LoadNestedTileset(tile));
                return;
            }

            if (tile.isLoading == false && tile.ChildrenCount == 0 && tile.contentUri.Contains(".subtree"))
            {
                //UnityEngine.Debug.Log(tile.contentUri);
                ReadSubtree subtreeReader = GetComponent<ReadSubtree>();
                if (subtreeReader.isbusy)
                {
                    return;
                }

                subtreeReader.isbusy = true;
                tile.isLoading = true;

                if (debugLog)
                {
                    Debug.Log("try to download a subtree");
                }
                subtreeReader.DownloadSubtree("", implicitTilingSettings, tile, subtreeLoaded);
                return;
            }

            var closestPointOnBounds = tile.ContentBounds.ClosestPoint(currentCamera.transform.position);
            CalculateTileScreenSpaceError(tile, currentCamera, closestPointOnBounds);
            var enoughDetail = tile.screenSpaceError < maximumScreenSpaceError;
            var Has3DContent = tile.contentUri.Length > 0 && !tile.contentUri.Contains(".json") && !tile.contentUri.Contains(".subtree");
            
            // Direct distance-based LOD selection strategy:
            // 1. If we have enough detail OR no children, load this tile
            // 2. If we don't have enough detail AND have children, traverse deeper first
            
            if (enoughDetail || tile.ChildrenCount == 0)  
            {
                // Load this tile if it has content
                if (Has3DContent)
                {
                    if (!visibleTiles.Contains(tile))
                    {
                        RequestContentUpdate(tile);
                        visibleTiles.Add(tile);
                    }
                }
            }
            else
            {
                // Not enough detail and we have children - traverse deeper first
                if (tile.ChildrenCount > 0)
                {
                    foreach (var childTile in tile.children)
                    {
                        LoadInViewRecursively(childTile, currentCamera);
                    }
                }
                
                // Only load this tile if it's ADD refinement (should show alongside children)
                if (tile.refine == "ADD" && Has3DContent)
                {
                    if (!visibleTiles.Contains(tile))
                    {
                        RequestContentUpdate(tile);
                        visibleTiles.Add(tile);
                    }
                }
            }
        }

        public void subtreeLoaded(Tile tile)
        {
            tile.parent.isLoading = false;
        }

        private IEnumerator LoadNestedTileset(Tile tile)
        {
            if (tilingMethod == TilingMethod.ExplicitTiling)
            {
                if (tile.contentUri.Contains(".json") && !tile.nestedTilesLoaded)
                {
                    string nestedJsonPath = GetFullContentUri(tile);
                    UnityWebRequest www = UnityWebRequest.Get(nestedJsonPath);
                    
                    foreach (var header in customHeaders)
                        www.SetRequestHeader(header.Key, header.Value);
                        
                    yield return www.SendWebRequest();

                    if (www.result != UnityWebRequest.Result.Success)
                    {
                        Debug.Log(www.error + " at " + nestedJsonPath);
                        OnServerRequestFailed.Invoke(www.result);
                    }
                    else
                    {
                        string jsonstring = www.downloadHandler.text;
                        tile.nestedTilesLoaded = true;

                        JSONNode node = JSON.Parse(jsonstring)["root"];
                        ParseTileset.ReadExplicitNode(node, tile);
                    }
                    OnServerResponseReceived.Invoke(www);
                }

                tile.isLoading = false;
            }
            else if (tilingMethod == TilingMethod.ImplicitTiling)
            {
                //Possible future nested subtree support.
            }
        }

        private string GetFullContentUri(Tile tile)
        {
            var relativeContentUrl = tile.contentUri;

            //RD amsterdam specific temp fix.
            relativeContentUrl = relativeContentUrl.Replace("../", "");

            var fullPath = (tile.contentUri.StartsWith("/")) ? rootPath + relativeContentUrl : absolutePath + relativeContentUrl;

            //Combine query to pass on session id and API key (Google Maps 3DTiles API style)
            UriBuilder uriBuilder = new(fullPath);
            NameValueCollection contentQueryParameters = ParseQueryString(uriBuilder.Query);
            foreach (string key in queryParameters.Keys)
            {
                if (!contentQueryParameters.AllKeys.Any(k => k == key))
                {
                    contentQueryParameters.Add(key, queryParameters[key]);
                }
            }
            foreach (string key in contentQueryParameters.Keys)
            {
                if (!queryParameters.AllKeys.Any(k => k == key))
                {
                    queryParameters.Add(key, contentQueryParameters[key]);
                }
            }

            uriBuilder.Query = ToQueryString(contentQueryParameters);
            var url = uriBuilder.ToString();
            return url;
        }

        private string ToQueryString(NameValueCollection queryParameters)
        {
            if (queryParameters.Count == 0) return "";

            StringBuilder queryString = new StringBuilder();
            for (int i = 0; i < queryParameters.Count; i++)
            {
                string key = queryParameters.GetKey(i);
                string[] values = queryParameters.GetValues(i);

                if (!string.IsNullOrEmpty(key) && values != null)
                {
                    for (int j = 0; j < values.Length; j++)
                    {
                        string value = values[j];

                        if (queryString.Length > 0)
                            queryString.Append("&");

                        queryString.AppendFormat("{0}={1}", Uri.EscapeDataString(key), Uri.EscapeDataString(value));
                    }
                }
            }

            return "?" + queryString.ToString();
        }


        /// <summary>
        /// Screen-space error component calculation.
        /// Screen height is clamped to limit the amount of geometry that
        /// would be loaded on very high resolution displays.
        /// </summary>
        public void SetSSEComponent(Camera currentCamera)
        {

            var screenHeight = (maxScreenHeightInPixels > 0) ? Mathf.Min(maxScreenHeightInPixels, Screen.height) : Screen.height;

            if (currentCamera.orthographic)
            {
                sseComponent = screenHeight / currentCamera.orthographicSize;
            }
            else
            {
                var coverage = 2 * Mathf.Tan((Mathf.Deg2Rad * currentCamera.fieldOfView) / 2);
                sseComponent = screenHeight / coverage;
            }
        }

        public void SetCoordinateSystem(CoordinateSystem newContentCoordinateSystem)
        {
            contentCoordinateSystem = newContentCoordinateSystem;
            ScenePosition[] scenepositions = GetComponentsInChildren<ScenePosition>();
            foreach (ScenePosition scenepos in scenepositions)
            {
                if ((int)newContentCoordinateSystem == scenepos.contentposition.CoordinateSystem)
                {
                    continue;
                }
                Coordinate newCoord = new Coordinate(newContentCoordinateSystem);
                newCoord.easting = scenepos.contentposition.easting;
                newCoord.northing = scenepos.contentposition.northing;
                newCoord.height = scenepos.contentposition.height;
                scenepos.contentposition = newCoord;
                scenepos.gameObject.transform.position = newCoord.ToUnity();
            }
            InvalidateBounds();
        }
    }

    public enum TilingMethod
    {
        Unknown,
        ExplicitTiling,
        ImplicitTiling
    }

    public enum RefinementType
    {
        Replace,
        Add
    }

    public enum SubdivisionScheme
    {
        Quadtree,
        Octree
    }

    [System.Serializable]
    public class ImplicitTilingSettings
    {
        public RefinementType refinementType;
        public SubdivisionScheme subdivisionScheme;
        public int availableLevels;
        public int subtreeLevels;
        public string subtreeUri;
        public string contentUri;
        public float geometricError;
        public BoundingVolume boundingVolume;
        public double[] boundingRegion;
    }
}
