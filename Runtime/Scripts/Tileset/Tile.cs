using Netherlands3D.Coordinates;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Netherlands3D.Tiles3D
{
   [System.Serializable]
    public class Tile : IDisposable
    {
       

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
        public Bounds ContentBounds = new Bounds();
        public BoundingVolume boundingVolume;
        public Coordinate BottomLeft;
        public Coordinate TopRight;
        public bool boundsAreValid = true;

        // load and dispose properties

        public bool inView = false;
        public bool requestedDispose = false;
        public bool requestedUpdate = false;
        internal bool nestedTilesLoaded = false;
        public bool isLoading = false;

        // tiletree properties
        public Tile parent;
        private List<Tile> _children;
        // Lazy children list to avoid per-instance allocation
        public List<Tile> children { get { return _children ??= new List<Tile>(); } }
        public int ChildrenCount => _children?.Count ?? 0;
        

        //tileproperties
        public TileTransform tileTransform = TileTransform.Identity();
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

        public int CountLoadingChildren => TileHelper.CountLoadingChildren(this);
        public int CountLoadedChildren => TileHelper.CountLoadedChildren(this);

        public int CountLoadedParents => TileHelper.CountLoadedParents(this);

        public int CountLoadingParents => TileHelper.CountLoadingParents(this);

        public Vector3 EulerRotationToVertical => TileHelper.EulerRotationToVertical(this);

        public Quaternion RotationToVertical => TileHelper.RotationToVertical(this);

        public bool ChildrenHaveContent => TileHelper.ChildrenHaveContent(this);

        public int GetNestingDepth => TileHelper.GetNestingDepth(this);

        public bool IsInViewFrustrum(Camera ofCamera) => TileHelper.IsInViewFrustrum(this, ofCamera);

        public void CalculateUnitBounds() => TileHelper.CalculateUnitBounds(this);

        public float getParentSSE => TileHelper.GetParentSSE(this);

        public void Dispose()
        {
            if (content != null)
            {
                content.Dispose();
                content = null;
            }
        }


    }
}
