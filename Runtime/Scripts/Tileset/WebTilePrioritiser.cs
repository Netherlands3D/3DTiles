using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Events;

namespace Netherlands3D.Tiles3D
{
    /// <summary>
    /// Tile prioritiser optimised for WebGL where we cant use threading.
    /// Modern browsers like Chrome limits parralel downloads from host to 6 per tab.
    /// Threading is not supported for WebGL, so this prioritiser spreads out actions to reduce framedrop spikes.
    /// This prioritiser takes center-of-screen into account combined with the 3D Tile SSE to determine tile priotities.
    /// </summary>
    /// 
    public class WebTilePrioritiser : MonoBehaviour
    {
        [DllImport("__Internal")]
        private static extern bool isMobile();

        [Header("SSE Screen height limitations (0 is disabled)")]
        public int maxScreenHeightInPixels = 0;
        public int maxScreenHeightInPixelsMobile = 0;

        private bool mobileMode = false;
        public bool MobileMode { get => mobileMode; set => mobileMode = value; }
        public int MaxScreenHeightInPixels {
            get
            {
                return (mobileMode) ? maxScreenHeightInPixelsMobile: maxScreenHeightInPixels;
            }
        }

        public UnityEvent<bool> OnMobileModeEnabled;

        [Header("Web limitations")]
        [SerializeField] private int maxSimultaneousDownloads = 6;

        // Removed delayed dispose functionality for simplified memory management

        [Header("Screen space error priority")]
        [SerializeField] private float screenSpaceErrorScoreMultiplier = 10f;

        [Header("Center-first priority")]
        [Tooltip("Prioritise tiles from the screen center outward.")]
        [SerializeField] private bool useCenterOutwardPriority = true;
        [SerializeField, Tooltip("Multiplier for center weight influence")] private float centerWeightMultiplier = 4f;
        [SerializeField, Tooltip("Penalty applied when tile is off-screen (0..1)")] private float offscreenPenalty = 0.1f;
        [SerializeField, Tooltip("Extra boost when no parents are loaded (helps popping roots near center) ")] private float parentNotLoadedBoost = 2f;

        [Header("Center of screen curve")]        
        [SerializeField] private float screenCenterScore = 10f;
        [SerializeField] AnimationCurve screenCenterWeight;

        private Vector2 viewCenter = new Vector2(0.5f, 0.5f);

        // Removed delayedDisposeList for simplified immediate disposal
        private List<Tile> prioritisedTiles = new List<Tile>();
        public List<Tile> PrioritisedTiles { get => prioritisedTiles; private set => prioritisedTiles = value; }

        private bool requirePriorityCheck = false;
        public bool showPriorityNumbers = false;

        [SerializeField]
        private int downloadAvailable = 0;

        private Camera currentCamera;

        private bool pauseNewDownloads = false;

        private Material materialOverride;
        private bool debugLog;

        private void Awake() 
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            MobileMode = isMobile();
#endif
            OnMobileModeEnabled.Invoke(MobileMode);

            materialOverride = GetComponent<Read3DTileset>().materialOverride;
            debugLog = GetComponent<Read3DTileset>().debugLog;
        }

        public void SetMaxScreenHeightInPixels(float pixels)
        {
            maxScreenHeightInPixels = Mathf.RoundToInt(pixels);
        }
        
        public void SetMaxScreenHeightInPixelsMobile(float pixels)
        {
            maxScreenHeightInPixelsMobile = Mathf.RoundToInt(pixels);
        }

        public void PauseDownloads(bool paused)
        {
            pauseNewDownloads = paused;
        }

        /// <summary>
        /// If a tile completed loading, recalcule priorities
        /// </summary>
        private void TileCompletedLoading()
        {
            requirePriorityCheck = true;
        }

        /// <summary>
        /// Request update for this tile by adding it to the prioritised tile list.
        /// Highest priority will be loaded first.
        /// <summary>
        /// Add this tile to the priority queue for loading
        /// </summary>
        public void AddToLoadQueue(Tile tile)
        {
            requirePriorityCheck = true;
            tile.requestedUpdate = true;
            // Avoid duplicates in the queue
            if (!PrioritisedTiles.Contains(tile))
            {
                PrioritisedTiles.Add(tile);
            }
        }

        /// <summary>
        /// Dispose this tile immediately.
        /// Simplified approach - no delayed disposal for better memory management.
        /// </summary>
        public void DisposeImmediately(Tile tile, bool immediately=false)
        {
            PrioritisedTiles.Remove(tile);
            requirePriorityCheck = true;

            tile.requestedDispose = true;
            
            // Always dispose immediately for better memory management
            tile.Dispose();
            tile.requestedUpdate = false;
            tile.requestedDispose = false;
        }

        private void LateUpdate()
        {
            if(requirePriorityCheck)
            {
                CalculatePriorities();
            }
            
            // No more delayed dispose checking needed - simplified approach
        }

        /// <summary>
        /// Calculates the priority list for the added tiles
        /// </summary>
        public void CalculatePriorities()
        {
            foreach (var tile in PrioritisedTiles)
            {
                var sse = Mathf.Max(0f, tile.screenSpaceError);

                // Base score from SSE once (avoid double counting)
                float score = sse * screenSpaceErrorScoreMultiplier;

                // Center-first multiplier
                if (useCenterOutwardPriority)
                {
                    float cw = CenterWeight(tile.ContentBounds.center);
                    // Center weight multiplies the base score to ensure center dominates
                    score *= (1f + cw * centerWeightMultiplier);

                    // Off-screen tiles get a penalty
                    if (!IsInView(tile)) score *= Mathf.Clamp01(offscreenPenalty);
                }
                else
                {
                    // Backward compatible additive center bonus, if desired
                    score += InViewCenterScore(tile.ContentBounds.center, screenCenterScore);
                }

                // Modest boost if no parents loaded, but don’t dwarf center preference
                int loadedParents = tile.CountLoadedParents;
                if (loadedParents < 1)
                {
                    score *= parentNotLoadedBoost;
                }

                tile.priority = (int)score;
            }

            PrioritisedTiles.Sort((obj1, obj2) => obj2.priority.CompareTo(obj1.priority));
            Apply();
        }

        /// <summary>
        /// Apply new priority changes to the tiles
        /// and start new downloads for the highest priority tiles if there is a download slot available.
        /// </summary>
        private void Apply()
        {
            var downloading = PrioritisedTiles.Count(tile => tile.content.State == Content.ContentLoadState.DOWNLOADING);
            downloadAvailable = maxSimultaneousDownloads - downloading;

            //Start a new download first the highest priority if a slot is available
            for (int i = 0; i < PrioritisedTiles.Count; i++)
            {
                if (downloadAvailable <= 0) break;

                var tile = PrioritisedTiles[i];
                if (!pauseNewDownloads && tile.content && tile.content.State == Content.ContentLoadState.NOTLOADING)
                {
                    downloadAvailable--;
                    // Removed noisy start-loading log
                    tile.content.Load(materialOverride);
                    tile.content.onDoneDownloading.AddListener(TileCompletedLoading);
                }
            }
        }

        /// <summary>
        /// Return a score based on world position in center of view using falloff curve
        /// </summary>
        /// <param name="maxScore">Max score in screen center</param>
        /// <returns></returns>
        public float InViewCenterScore(Vector3 position, float maxScore)
        {
            var cam = currentCamera != null ? currentCamera : Camera.main;
            if (cam == null) return 0f;
            var position2D = cam.WorldToViewportPoint(position);
            var distance = Vector2.Distance(position2D, viewCenter);

            return maxScore * screenCenterWeight.Evaluate(1.0f - distance);
        }


        public float sseScore(Tile tile)
        {
            // Kept for compatibility but unused in new scoring; consider removing later
            return tile.screenSpaceError * 100f;
        }

        /// <summary>
        /// Return higher score for closer position to current target camera
        /// </summary>
        /// <param name="position">World position to compare distance to camera</param>
        /// <param name="minDistance">The distance where score is maximum</param>
        /// <param name="maxDistance">The distance where score becomes 0</param>
        /// <param name="maxScore">Max score for closest object</param>
        /// <returns></returns>
        public float DistanceScore(Tile tile)
        {
            // Deprecated: previously aliased to SSE; use new scoring in CalculatePriorities
            return tile.screenSpaceError * screenSpaceErrorScoreMultiplier;
        }

        private bool IsInView(Tile tile)
        {
            var cam = currentCamera != null ? currentCamera : Camera.main;
            if (cam == null) return true; // don’t over-penalise if no camera
            return tile.IsInViewFrustrum(cam);
        }

        private float CenterWeight(Vector3 position)
        {
            var cam = currentCamera != null ? currentCamera : Camera.main;
            if (cam == null) return 0f;
            var position2D = cam.WorldToViewportPoint(position);
            var distance = Vector2.Distance(position2D, viewCenter);
            float t = 1.0f - distance;
            if (screenCenterWeight != null)
            {
                return Mathf.Clamp01(screenCenterWeight.Evaluate(t));
            }
            // Fallback: linear center weight
            return Mathf.Clamp01(t);
        }

        public void SetCamera(Camera currentMainCamera)
        {
            currentCamera = currentMainCamera;
        }
    }
}
