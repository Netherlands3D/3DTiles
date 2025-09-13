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

        private Coroutine runningContentRequest;
        public Read3DTileset tilesetReader;
        [SerializeField] private Tile parentTile;
        public Tile ParentTile { get => parentTile; set => parentTile = value; }

        public UnityEvent onDoneDownloading = new();
        public UnityEvent<Content> onTileLoadCompleted = new();

        private UnityEngine.Material overrideMaterial;
        private GLTFast.GltfImport gltfImport; // Reference to dispose later
        
        // Cancellation token for async operations
        private System.Threading.CancellationTokenSource cancellationTokenSource;

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
            }

            if (State == ContentLoadState.DOWNLOADING || State == ContentLoadState.DOWNLOADED)
                return;

            State = ContentLoadState.DOWNLOADING;
            parentTile.isLoading = true;
            
            // Start download using TIleContentLoader
            runningContentRequest = StartCoroutine(
                Netherlands3D.Tiles3D.TileContentLoader.DownloadContent(
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

        private void DownloadedData(byte[] data,string uri)
        {
            if (data == null)
            {
                FinishedLoading(false);
                return;
            }
            
            // Check if we're still valid before starting async operation
            if (this == null || gameObject == null)
            {
                Debug.LogWarning("Content destroyed before async loading could start");
                return;
            }
            
            // Create cancellation token for this async operation
            if (cancellationTokenSource == null)
                cancellationTokenSource = new System.Threading.CancellationTokenSource();
            
            // Set state to PARSING to indicate async operation in progress
            State = ContentLoadState.PARSING;
            
            // Process the downloaded data using TIleContentLoader
            _ = Netherlands3D.Tiles3D.TileContentLoader.LoadContent(
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

            //Direct abort of downloads
            if (State == ContentLoadState.DOWNLOADING && runningContentRequest != null)
            {
                StopCoroutine(runningContentRequest);       
            }
           
            State = ContentLoadState.DOWNLOADED;

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
    }
}
