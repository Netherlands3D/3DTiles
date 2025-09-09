using Netherlands3D.Coordinates;
//using PlasticPipe.PlasticProtocol.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Netherlands3D.Tiles3D
{
    [System.Serializable]
    public class Tile : IDisposable
    {
        // Disk caching support
        private string _tileId;
        public string TileId 
        { 
            get => _tileId ??= GenerateUniqueTileId();
            set => _tileId = value;
        }
        
        private bool _isLoadedFromCache = false;
        public bool IsLoadedFromCache => _isLoadedFromCache;
        
        public static int TotalCacheHits { get; private set; } = 0;
        public static int TotalCacheStores { get; private set; } = 0;
        
        public static void LogCacheStats()
        {
            Debug.Log($"📊 [CACHE STATS] Hits: {TotalCacheHits}, Stores: {TotalCacheStores}");
        }
        
        public static void LogCacheDirectory()
        {
            try
            {
                string persistentPath = Application.persistentDataPath;
                string cacheDir = System.IO.Path.Combine(persistentPath, "TileCache");
                
                Debug.Log($"🗂️  [PERSISTENT PATH] {persistentPath}");
                Debug.Log($"📁 [CACHE PATH] {cacheDir}");
                
                if (System.IO.Directory.Exists(cacheDir))
                {
                    var files = System.IO.Directory.GetFiles(cacheDir, "*.cache");
                    Debug.Log($"� [CACHE FILES] {files.Length} cached tiles found");
                    
                    // Show first few filenames as examples
                    for (int i = 0; i < Math.Min(5, files.Length); i++)
                    {
                        string fileName = System.IO.Path.GetFileName(files[i]);
                        var fileInfo = new System.IO.FileInfo(files[i]);
                        Debug.Log($"   • {fileName} ({fileInfo.Length} bytes)");
                    }
                    if (files.Length > 5)
                    {
                        Debug.Log($"   ... and {files.Length - 5} more files");
                    }
                }
                else
                {
                    Debug.Log($"� [CACHE DIR] Directory does not exist yet");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"❌ [CACHE DIR] Error checking cache directory: {e.Message}");
            }
        }
        
        public static void OpenCacheDirectoryInFinder()
        {
            try
            {
                string persistentPath = Application.persistentDataPath;
                string cacheDir = System.IO.Path.Combine(persistentPath, "TileCache");
                
                // Create directory if it doesn't exist
                if (!System.IO.Directory.Exists(cacheDir))
                {
                    System.IO.Directory.CreateDirectory(cacheDir);
                }
                
                // Open in Finder (macOS)
                System.Diagnostics.Process.Start("open", $"\"{persistentPath}\"");
                Debug.Log($"🍎 [FINDER] Opened cache directory in Finder: {persistentPath}");
                Debug.Log($"💡 [TIP] Look for the 'TileCache' folder inside this directory");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"❌ [FINDER] Could not open cache directory: {e.Message}");
            }
        }
        
        /// <summary>
        /// Generate unique TileId that works for Google Photorealistic tiles and other tile formats
        /// </summary>
        private string GenerateUniqueTileId()
        {
            // Priority 1: Use contentUri if available (works for Google Photorealistic)
            if (!string.IsNullOrEmpty(contentUri))
            {
                // Clean up URI to make it filesystem-safe
                return "uri_" + contentUri.Replace("/", "_")
                                           .Replace("\\", "_")
                                           .Replace(":", "_")
                                           .Replace("?", "_")
                                           .Replace("&", "_")
                                           .Replace("=", "_")
                                           .Replace(".", "_");
            }
            
            // Priority 2: Use level/X/Y for implicit tiling (Cesium style)
            if (level >= 0 && X >= 0 && Y >= 0)
            {
                return $"lxy_{level}_{X}_{Y}";
            }
            
            // Priority 3: Use bounding volume center + geometric error
            if (boundingVolume?.values != null && boundingVolume.values.Length >= 3 && geometricError > 0)
            {
                // Use center coordinates and geometric error as unique identifier
                double centerX = boundingVolume.values[0];
                double centerY = boundingVolume.values[1]; 
                double centerZ = boundingVolume.values[2];
                
                return $"bbox_{centerX:F6}_{centerY:F6}_{centerZ:F6}_{geometricError:F2}"
                    .Replace(".", "_").Replace("-", "n"); // Make filesystem-safe
            }
            
            // Priority 4: Use parent relationship + index
            if (parent != null)
            {
                int siblingIndex = parent.children?.IndexOf(this) ?? 0;
                return $"{parent.TileId}_child_{siblingIndex}";
            }
            
            // Last resort: Use object hash + timestamp
            return $"tile_{GetHashCode():X8}_{DateTime.Now.Ticks:X8}";
        }

        public bool disposed;

        public bool requestDispose;

        public bool visibleTileCheckedNotInView = false;

        public bool suspiciousBottomLeft = false;

        public int suspiciousCounter = 0;

        public bool rootIsInView = false;

        public bool isRoot = false;
        public bool isRootChild = false;

        public void setRootChild() {
            foreach (var child in children)
            {
                child.isRootChild = true;
                child.setRootChild();
            }
        }


        //implicit tiling properties
        public int level;
        public int X;
        public int Y;
        public bool hascontent;
        public Read3DTileset tileSet;
        //webtileprioritizer properties
        public int priority = 0;
        public int childrenCountDelayingDispose = 0;

        //BoundingVolume properties
        internal bool boundsAvailable = false;
        private Bounds unityBounds = new Bounds();
        public BoundingVolume boundingVolume;
        public Coordinate BottomLeft;
        public Coordinate TopRight;
        bool boundsAreValid = true;

        // load and dispose properties

        public bool inView = false;
        public bool requestedDispose = false;
        public bool requestedUpdate = false;
        internal bool nestedTilesLoaded = false;
        public bool isLoading = false;



        // tiletree properties
        public Tile parent;

        public bool HasParent;

        [SerializeField] public List<Tile> children = new List<Tile>();


        //tileproperties

        public TileTransform tileTransform = TileTransform.Identity();
        public double[] transform = new double[16] { 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0 };
        public double geometricError;
        public float screenSpaceError = float.MaxValue;
        public string refine;
        public string contentUri = "";
        public Content content; //Gltf content

        public static Action<Tile> OnTileCreated;

        public Tile()
        {
            OnTileCreated?.Invoke(this);
        }

        public int CountLoadingChildren()
        {
            int result = 0;
            if (refine == "ADD")
            {
                return 0;
            }
            foreach (var childTile in children)
            {
                if (childTile.content != null)
                {
                    if (childTile.contentUri.Contains(".json") == false)
                    {
                        if (childTile.content.State != Content.ContentLoadState.DOWNLOADED)
                        {
                            result += 1;
                        }

                    }
                }
            }


            return result;
        }
        public int loadedChildren;
        public int CountLoadedChildren()
        {
            int result = 0;
            if (refine == "ADD")
            {
                return 0;
            }
            foreach (var childTile in children)
            {
                if (childTile.content != null)
                {
                    if (childTile.contentUri.Contains(".json") == false)
                    {

                        if (childTile.content.State != Content.ContentLoadState.DOWNLOADING)
                        {
                            result++;
                        }

                    }
                }
            }
            foreach (var childTile in children)
            {
                result += childTile.CountLoadedChildren();
            }
            loadedChildren = result;
            return result;
        }

        public int CountLoadedParents()
        {
            if (refine == "ADD")
            {
                return 1;
            }
            int result = 0;
            if (parent != null)
            {
                if (parent.content != null)
                {
                    if (parent.contentUri.Contains(".json") == false)
                    {
                        if (parent.content.State == Content.ContentLoadState.DOWNLOADED)
                        {
                            result = 1;
                        }
                    }
                }
            }

            if (parent != null)
            {
                return result + parent.CountLoadedParents();
            }
            return result;
        }

        public int CountLoadingParents()
        {
            int result = 0;
            if (parent != null)
            {
                if (parent.isLoading)
                {
                    if (parent.contentUri.Contains(".json") == false)
                    {
                        if (parent.content != null)
                        {
                            if (parent.content.State != Content.ContentLoadState.DOWNLOADED)
                            {
                                result = 1;
                            }
                        }
                    }
                }
            }
            if (parent != null)
            {
                return result + parent.CountLoadingParents();
            }
            return result;
        }

        public Bounds ContentBounds
        {
            get
            {
                return unityBounds;
            }
            set => unityBounds = value;
        }

        public Vector3 EulerRotationToVertical()
        {
            float posX = (float)(transform[12] / 1000); // measured for earth-center to prime meridian (greenwich)
            float posY = (float)(transform[13] / 1000); // measured from earth-center to 90degrees east at equator
            float posZ = (float)(transform[14] / 1000); // measured from earth-center to nothpole

            float angleX = -Mathf.Rad2Deg * Mathf.Atan(posY / posZ);
            float angleY = -Mathf.Rad2Deg * Mathf.Atan(posX / posZ);
            float angleZ = -Mathf.Rad2Deg * Mathf.Atan(posY / posX);
            Vector3 result = new Vector3(angleX, angleY, angleZ);
            return result;
        }

        public Quaternion RotationToVertical()
        {
            float posX = (float)(transform[12] / 1000000); // measured for earth-center to prime meridian (greenwich)
            float posY = (float)(transform[13] / 1000000); // measured from earth-center to 90degrees east at equator
            float posZ = (float)(transform[14] / 1000000); // measured from earth-center to nothpole

            Quaternion rotation = Quaternion.FromToRotation(new Vector3(posX, posY, posZ), new Vector3(0, 0, 1));

            return rotation;
        }

        public bool ChildrenHaveContent()
        {
            if (children.Count > 0)
            {
                foreach (var child in children)
                {
                    if (!child.content || child.content.State != Content.ContentLoadState.DOWNLOADED) return false;
                    break;
                }
            }
            return true;
        }

        public int GetNestingDepth()
        {
            int maxDepth = 1;
            foreach (var child in children)
            {
                int depth = child.GetNestingDepth() + 1;
                if (depth > maxDepth) maxDepth = depth;

            }
            return maxDepth;
        }

        // public enum TileStatus
        // {
        //     unloaded,
        //     loaded
        // }

        public int isInViewCounter = 0;


        public bool IsInViewFrustrum(Camera ofCamera)
        {
            isInViewCounter++;

            if (!boundsAvailable && boundsAreValid)
            {
                if (boundingVolume.values.Length > 0)
                {
                    CalculateUnitBounds();
                }
                else
                {
                    inView = false;
                }

            }




            if (boundsAvailable)
            {

                suspiciousBottomLeft = AllApproximatelyEqual(BottomLeft.value1, BottomLeft.value2, BottomLeft.value3);
                if (suspiciousBottomLeft) suspiciousCounter++;



                inView = false;
                if (IsPointInbounds(new Coordinate(ofCamera.transform.position).Convert(tileSet.contentCoordinateSystem), 8000d))
                {
                    inView = ofCamera.InView(unityBounds);
                }

            }

            return inView;
        }

        const double Tolerance = 1e-9;
        bool Approximately(double a, double b)
        {
            return Math.Abs(a - b) < Tolerance;
        }

        bool AllApproximatelyEqual(double v1, double v2, double v3)
        {
            return Approximately(v1, v2) && Approximately(v2, v3);
        }



        bool IsPointInbounds(Coordinate point, double margin)
        {
            if (point.PointsLength > 2)
            {
                if (point.value3 + margin < BottomLeft.value3)
                {
                    return false;
                }

                if (point.value3 - margin > TopRight.value3)
                {
                    return false;
                }
            }

            if (point.value1 + margin < BottomLeft.value1)
            {
                return false;
            }
            if (point.value2 + margin < BottomLeft.value2)
            {
                return false;
            }
            if (point.value1 - margin > TopRight.value1)
            {
                return false;
            }
            if (point.value2 - margin > TopRight.value2)
            {
                return false;
            }
            return true;
        }

        public void CalculateUnitBounds()
        {
            if (boundingVolume == null || boundingVolume.values.Length == 0)
            {
                boundsAreValid = false;
                return;
            }

            boundsAvailable = true;
            switch (boundingVolume.boundingVolumeType)
            {
                case BoundingVolumeType.Box:

                    Coordinate boxCenterEcef = new Coordinate(tileSet.contentCoordinateSystem, boundingVolume.values[0], boundingVolume.values[1], boundingVolume.values[2]);

                    Coordinate Xaxis = new Coordinate(tileSet.contentCoordinateSystem, boundingVolume.values[3], boundingVolume.values[4], boundingVolume.values[5]);
                    Coordinate Yaxis = new Coordinate(tileSet.contentCoordinateSystem, boundingVolume.values[6], boundingVolume.values[7], boundingVolume.values[8]);
                    Coordinate Zaxis = new Coordinate(tileSet.contentCoordinateSystem, boundingVolume.values[9], boundingVolume.values[10], boundingVolume.values[11]);




                    unityBounds = new Bounds();
                    unityBounds.center = boxCenterEcef.ToUnity();

                    unityBounds.Encapsulate((boxCenterEcef + Xaxis + Yaxis + Zaxis).ToUnity());
                    unityBounds.Encapsulate((boxCenterEcef + Xaxis + Yaxis - Zaxis).ToUnity());
                    unityBounds.Encapsulate((boxCenterEcef + Xaxis - Yaxis + Zaxis).ToUnity());
                    unityBounds.Encapsulate((boxCenterEcef + Xaxis - Yaxis - Zaxis).ToUnity());

                    unityBounds.Encapsulate((boxCenterEcef - Xaxis + Yaxis + Zaxis).ToUnity());
                    unityBounds.Encapsulate((boxCenterEcef - Xaxis - Yaxis + Zaxis).ToUnity());

                    unityBounds.Encapsulate((boxCenterEcef - Xaxis + Yaxis - Zaxis).ToUnity());
                    unityBounds.Encapsulate((boxCenterEcef - Xaxis - Yaxis - Zaxis).ToUnity());


                    double deltaX = Math.Abs(Xaxis.value1) + Math.Abs(Yaxis.value1) + Math.Abs(Zaxis.value1);
                    double deltaY = Math.Abs(Xaxis.value2) + Math.Abs(Yaxis.value2) + Math.Abs(Zaxis.value2);
                    double deltaZ = Math.Abs(Xaxis.value3) + Math.Abs(Yaxis.value3) + Math.Abs(Zaxis.value3);
                    BottomLeft = new Coordinate(tileSet.contentCoordinateSystem, boxCenterEcef.value1 - deltaX, boxCenterEcef.value2 - deltaY, boxCenterEcef.value3 - deltaZ);
                    TopRight = new Coordinate(tileSet.contentCoordinateSystem, boxCenterEcef.value1 + deltaX, boxCenterEcef.value2 + deltaY, boxCenterEcef.value3 + deltaZ);


                    break;
                case BoundingVolumeType.Sphere:
                    var sphereRadius = boundingVolume.values[0];
                    var sphereCentre = CoordinateConverter.ECEFToUnity(new Vector3ECEF(boundingVolume.values[0], boundingVolume.values[1], boundingVolume.values[2]));
                    var sphereMin = CoordinateConverter.ECEFToUnity(new Vector3ECEF(boundingVolume.values[0] - sphereRadius, boundingVolume.values[1] - sphereRadius, boundingVolume.values[2] - sphereRadius));
                    var sphereMax = CoordinateConverter.ECEFToUnity(new Vector3ECEF(boundingVolume.values[0] + sphereRadius, boundingVolume.values[1] + sphereRadius, boundingVolume.values[2] + sphereRadius));
                    unityBounds.size = Vector3.zero;
                    unityBounds.center = sphereCentre;
                    unityBounds.Encapsulate(sphereMin);
                    unityBounds.Encapsulate(sphereMax);
                    BottomLeft = new Coordinate(CoordinateSystem.WGS84_ECEF, boundingVolume.values[0] - sphereRadius, boundingVolume.values[1] - sphereRadius, boundingVolume.values[2] - sphereRadius);
                    TopRight = new Coordinate(CoordinateSystem.WGS84_ECEF, boundingVolume.values[0] + sphereRadius, boundingVolume.values[1] + sphereRadius, boundingVolume.values[2] + sphereRadius);
                    break;
                case BoundingVolumeType.Region:
                    //Array order: west, south, east, north, minimum height, maximum height
                    double West = (boundingVolume.values[0] * 180.0f) / Mathf.PI;
                    double South = (boundingVolume.values[1] * 180.0f) / Mathf.PI;
                    double East = (boundingVolume.values[2] * 180.0f) / Mathf.PI;
                    double North = (boundingVolume.values[3] * 180.0f) / Mathf.PI;
                    double MaxHeight = boundingVolume.values[4];
                    double minHeight = boundingVolume.values[5];

                    var mincoord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, West, minHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    var maxcoord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, West, minHeight).Convert(CoordinateSystem.WGS84_ECEF);

                    var coord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, West, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                    if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                    if (coord.height < mincoord.height) mincoord.height = coord.height;
                    if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                    if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                    if (coord.height > maxcoord.height) maxcoord.height = coord.height;

                    coord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, East, minHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                    if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                    if (coord.height < mincoord.height) mincoord.height = coord.height;
                    if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                    if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                    if (coord.height > maxcoord.height) maxcoord.height = coord.height;

                    coord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, East, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                    if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                    if (coord.height < mincoord.height) mincoord.height = coord.height;
                    if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                    if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                    if (coord.height > maxcoord.height) maxcoord.height = coord.height;

                    coord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, West, minHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                    if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                    if (coord.height < mincoord.height) mincoord.height = coord.height;
                    if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                    if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                    if (coord.height > maxcoord.height) maxcoord.height = coord.height;

                    coord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, West, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                    if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                    if (coord.height < mincoord.height) mincoord.height = coord.height;
                    if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                    if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                    if (coord.height > maxcoord.height) maxcoord.height = coord.height;

                    coord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, East, minHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                    if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                    if (coord.height < mincoord.height) mincoord.height = coord.height;
                    if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                    if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                    if (coord.height > maxcoord.height) maxcoord.height = coord.height;

                    coord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, East, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF);
                    if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                    if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                    if (coord.height < mincoord.height) mincoord.height = coord.height;
                    if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                    if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                    if (coord.height > maxcoord.height) maxcoord.height = coord.height;

                    var unityMin = mincoord.ToUnity();
                    var unityMax = maxcoord.ToUnity();

                    unityBounds.size = Vector3.zero;
                    unityBounds.center = unityMin;
                    unityBounds.Encapsulate(unityMax);

                    BottomLeft = mincoord;
                    TopRight = maxcoord;
                    break;
                default:
                    break;
            }

            boundsAvailable = true;
        }

        public float getParentSSE()
        {
            float result = 0;
            if (parent != null)
            {



                if (parent.content != null)
                {
                    if (parent.content.State == Content.ContentLoadState.DOWNLOADED)
                    {
                        result = parent.screenSpaceError;
                    }
                }
                if (result == 0)
                {
                    result = parent.getParentSSE();
                }
            }
            return result;
        }

        void DestroyChildren()
        {
            foreach (var child in children)
            {
                child.parent = null;
                child.DestroyChildren();
            }
            children.Clear();
        }

        public void Dispose()
        {
            disposed = true;

            // Try to cache tile data before disposal (WebGL safe)
            StoreToCache(); // Synchronous version for WebGL

            foreach (var child in children)
            {
                child.parent = null;
                child.Dispose();
            }
            children.Clear();
            parent = null;

            if (content != null)
            {
                // content.tilesetReader.visibleTiles.Remove(this); 

                content.Dispose();
                content = null;
            }
        }
        
        /// <summary>
        /// Store tile data to disk cache (WebGL-safe synchronous operation)
        /// </summary>
        public void StoreToCache()
        {
            if (string.IsNullOrEmpty(contentUri)) return;
            
            try
            {
                // Use persistentDataPath which maps to IndexedDB in WebGL
                string cacheDir = System.IO.Path.Combine(Application.persistentDataPath, "TileCache");
                if (!System.IO.Directory.Exists(cacheDir))
                {
                    System.IO.Directory.CreateDirectory(cacheDir);
                }
                
                string cacheFile = System.IO.Path.Combine(cacheDir, $"{TileId}.cache");
                
                // Store tile metadata as JSON
                var tileInfo = new TileCacheInfo
                {
                    geometricError = this.geometricError,
                    contentUri = this.contentUri
                };
                
                string json = JsonUtility.ToJson(tileInfo);
                System.IO.File.WriteAllText(cacheFile, json);
                
                TotalCacheStores++;
                // Debug.Log($"💾 [CACHE] Stored metadata for tile {TileId}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to store tile {TileId} to file cache: {e.Message}");
            }
        }
        
        /// <summary>
        /// Try to load tile data from file cache (WebGL -> IndexedDB)
        /// </summary>
        public bool LoadFromCache()
        {
            try
            {
                string cacheDir = System.IO.Path.Combine(Application.persistentDataPath, "TileCache");
                string cacheFile = System.IO.Path.Combine(cacheDir, $"{TileId}.cache");
                
                if (System.IO.File.Exists(cacheFile))
                {
                    string json = System.IO.File.ReadAllText(cacheFile);
                    var tileInfo = JsonUtility.FromJson<TileCacheInfo>(json);
                    
                    if (tileInfo != null)
                    {
                        this.geometricError = tileInfo.geometricError;
                        this.contentUri = tileInfo.contentUri;
                           
                        _isLoadedFromCache = true;
                        TotalCacheHits++;
                        // Debug.Log($"✅ [CACHE] Loaded metadata for tile {TileId} from file cache");
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load tile {TileId} from file cache: {e.Message}");
            }
            
            return false;
        }

        ~Tile()
        {
            Debug.Log($"tile finalized parentisnull:{parent == null} childrencount:{children.Count}");
        }
        

        
        /// <summary>
        /// Serializable tile cache information
        /// </summary>
        [System.Serializable]
        public class TileCacheInfo
        {
            public double geometricError;
            public string contentUri;
            public long cachedTime;
        }
    }
}
