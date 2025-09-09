using GLTFast;
using Netherlands3D.Coordinates;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;


namespace Netherlands3D.Tiles3D
{
    [System.Serializable]
    public class Content : MonoBehaviour, IDisposable
    {
        bool _disposed;

        public string uri = "";
        public Coordinate contentCoordinate;
        public CoordinateSystem contentcoordinateSystem;

#if SUBOBJECT
        public bool parseSubObjects = true;
#endif

        public bool parseAssetMetaData = false;

        private Coroutine runningContentRequest;
        public Read3DTileset tilesetReader;
        [SerializeField] private Tile parentTile;
        public Tile ParentTile { get => parentTile; set => parentTile = value; }

        public UnityEvent<Tile> onDoneDownloading = new();

        private UnityEngine.Material overrideMaterial;

        // private GltfImport gltf;

        public ParsedGltf parsedGltf;


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
            this.headers = headers;
            if (overrideMaterial != null)
            {
                this.overrideMaterial = overrideMaterial;
                Debug.Log("overrideMaterial is used!");
            }

            if (State == ContentLoadState.DOWNLOADING || State == ContentLoadState.DOWNLOADED)
                return;

            // Check if tile metadata is cached (for debug purposes only)
            bool hasCachedMetadata = parentTile.LoadFromCache();
            if (hasCachedMetadata)
            {
                Debug.Log($"[CACHE HIT] 📦 Using cached metadata for tile {parentTile.TileId}");
            }

            State = ContentLoadState.DOWNLOADING;
            parentTile.isLoading = true;
            TIleContentLoader.debugLog = verbose;
            runningContentRequest = StartCoroutine(
           TIleContentLoader.DownloadContent(
               uri,
               transform,
               ParentTile,
               DownloadedData,
               parseAssetMetaData,
               parseSubObjects,
               overrideMaterial,
               false,
               headers

               )
           );
            return;

        }

        private async Task DownloadedData(byte[] data, string uri)
        {
            if (data == null)
            {
                FinishedLoading(false);
                return;
            }
            await TIleContentLoader.LoadContent(
                data,
                uri,
                transform,
                ParentTile,
                FinishedLoading,
                parseAssetMetaData,
                parseSubObjects,
                overrideMaterial,
                false,
                headers
                );
        }

        /// <summary>
        /// After parsing gltf content spawn gltf scenes
        /// </summary>
        /// 
        private void FinishedLoading(bool succes)
        {
            State = ContentLoadState.DOWNLOADED;
            
            // Store successfully downloaded tile metadata to cache
            if (succes && parentTile != null)
            {
                parentTile.StoreToCache();
                Debug.Log($"[CACHE STORE] 💾 Saved metadata for tile {parentTile.TileId}");
            }
            
            onDoneDownloading.Invoke(parentTile);
        }
        //       

        private void OverrideAllMaterials(Transform parent)
        {
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>())
            {
                renderer.material = overrideMaterial;
            }
        }

        // /// <summary>
        // /// Clean up coroutines and content gameobjects
        // /// </summary>
        // public void Dispose()
        // {             
        //     onDoneDownloading.RemoveAllListeners();

        //     parentTile = null;

        //     // if (State == ContentLoadState.PARSING)
        //     // {
        //     //     onDoneDownloading.AddListener(Dispose);
        //     //     return;
        //     // }

        //     //Direct abort of downloads
        //     if (State == ContentLoadState.DOWNLOADING && runningContentRequest != null)
        //     {
        //         StopCoroutine(runningContentRequest);
        //     }

        //     //State = ContentLoadState.DOWNLOADED;


        //     // if (gltf != null)
        //     // {
        //     //     gltf.Dispose();     
        //     // }

        //     if (parsedGltf != null)
        //     {
        //         parsedGltf.Dispose(); // hier wordt gltfImport.Dispose() aangeroepen
        //         parsedGltf = null;
        //     }


        //     if (overrideMaterial == null)
        //     {
        //         Renderer[] meshrenderers = this.gameObject.GetComponentsInChildren<Renderer>();
        //         ClearRenderers(meshrenderers);
        //     }
        //     MeshFilter[] meshFilters = this.gameObject.GetComponentsInChildren<MeshFilter>();
        //     foreach (var meshFilter in meshFilters)
        //     {
        //         //the order of destroying sharedmesh before mesh matters for cleaning up native shells
        //         if (meshFilter.sharedMesh != null)
        //         {
        //             UnityEngine.Mesh mesh = meshFilter.sharedMesh;
        //             meshFilter.sharedMesh.Clear();
        //             Destroy(mesh);
        //             meshFilter.sharedMesh = null;
        //         }
        //         if (meshFilter.mesh != null)
        //         {
        //             UnityEngine.Mesh mesh = meshFilter.mesh;
        //             meshFilter.mesh.Clear();
        //             Destroy(mesh);
        //             meshFilter.mesh = null;
        //         }
        //     }

        //     Destroy(this.gameObject);
        // }


        static readonly List<Renderer> _renderers = new();
        static readonly List<Material> _mats = new();
        static readonly List<MeshFilter> _meshFilters = new();
        static readonly List<MeshCollider> _meshColliders = new();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            onDoneDownloading.RemoveAllListeners();
            parentTile = null;

            if (State == ContentLoadState.DOWNLOADING && runningContentRequest != null)
            {
                StopCoroutine(runningContentRequest);
                runningContentRequest = null;
            }

            parsedGltf?.Dispose();
            parsedGltf = null;

            CleanupGameObject(this.gameObject, cleanupMaterialsAndTextures: overrideMaterial == null);

            Destroy(this.gameObject);
        }

        void CleanupGameObject(GameObject root, bool cleanupMaterialsAndTextures)
        {
            if (!root) return;

            if (cleanupMaterialsAndTextures)
            {
                _renderers.Clear();
                root.GetComponentsInChildren(true, _renderers);

                foreach (var r in _renderers)
                {
                    _mats.Clear();
                    r.GetSharedMaterials(_mats);

                    for (int i = 0; i < _mats.Count; i++)
                    {
                        var mat = _mats[i];
                        if (!mat) continue;

                        var ids = mat.GetTexturePropertyNameIDs();
                        for (int t = 0; t < ids.Length; t++)
                        {
                            var tex = mat.GetTexture(ids[t]);
                            if (!tex) continue;

                            mat.SetTexture(ids[t], null);

                            if (tex is RenderTexture rt) rt.Release();

                            if (tex.hideFlags == HideFlags.None)
                                Destroy(tex);
                        }

                        if (mat.hideFlags == HideFlags.None)
                            Destroy(mat);

                        _mats[i] = null;
                    }

                    r.sharedMaterials = Array.Empty<Material>();
                    r.SetPropertyBlock(null);
                }
                _renderers.Clear();
            }

            _meshFilters.Clear();
            root.GetComponentsInChildren(true, _meshFilters);
            foreach (var mf in _meshFilters)
            {
                if (mf.sharedMesh)
                {
                    var m = mf.sharedMesh;
                    mf.sharedMesh = null;
                    Destroy(m);
                }
                if (mf.mesh)
                {
                    var inst = mf.mesh;
                    mf.mesh = null;
                    Destroy(inst);
                }
            }
            _meshFilters.Clear();

            _meshColliders.Clear();
            root.GetComponentsInChildren(true, _meshColliders);
            foreach (var mc in _meshColliders)
            {
                if (mc.sharedMesh)
                {
                    var m = mc.sharedMesh;
                    mc.sharedMesh = null;
                    Destroy(m);
                }
            }
            _meshColliders.Clear();
        }
    












        //todo we need to come up with a way to get all used texture slot property names from the gltf package
        private void ClearRenderers(Renderer[] renderers)
        {
            foreach (Renderer r in renderers)
            {
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null) continue;

                    foreach (var name in mat.GetTexturePropertyNames())
                    {
                        var tex = mat.GetTexture(name);
                        if (tex != null)
                        {
                            // Debug.Log($"IEK IEK! gameobject: {r.gameObject.name} Destroy texture:{name}");
                            mat.SetTexture(name, null);
                            Destroy(tex);
                        }
                    }

                    Destroy(mat);
                }
                r.sharedMaterials = new Material[0]; // verbreek alle links
            }
        }

        void OnDestroy()
        {
            DestroyMeshesAndTextures();
        }

        void DestroyMeshesAndTextures()
        {
            var renderers = this.gameObject.GetComponentsInChildren<Renderer>();

            foreach (Renderer r in renderers)
            {
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null) continue;

                    foreach (var name in mat.GetTexturePropertyNames())
                    {
                        var tex = mat.GetTexture(name);
                        if (tex != null)
                        {
                         //   Debug.Log($"gameobject: {r.gameObject.name} Destroy texture:{name}");
                            mat.SetTexture(name, null);
                            Destroy(tex);
                        }
                    }

                    Destroy(mat);
                }
                r.sharedMaterials = new Material[0]; // verbreek alle links

            }

            // MeshFilters → meshes opruimen
            MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
            foreach (MeshFilter mf in meshFilters)
            {
                if (mf.mesh != null)
                {
                    Destroy(mf.mesh);
                    mf.mesh = null;
                }
            }



        }
    }
}
