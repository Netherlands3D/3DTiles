using Netherlands3D.Coordinates;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Netherlands3D.Tiles3D
{
    /// <summary>
    /// Static helper methods for Tile operations to reduce per-instance memory overhead
    /// </summary>
    public static class TileHelper
    {
        /// <summary>
        /// Count loading children for a tile
        /// </summary>
        public static int CountLoadingChildren(Tile tile)
        {
            if (tile.refine == "ADD")
                return 0;

            int result = 0;
            if (tile.ChildrenCount > 0)
            {
                foreach (var childTile in tile.children)
                {
                    if (childTile?.content != null && !childTile.contentUri.Contains(".json"))
                    {
                        if (childTile.content.State != Content.ContentLoadState.DOWNLOADED)
                        {
                            result++;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Count loaded children for a tile
        /// </summary>
        public static int CountLoadedChildren(Tile tile)
        {
            if (tile.refine == "ADD")
                return 0;

            int result = 0;
            if (tile.ChildrenCount > 0)
            {
                foreach (var childTile in tile.children)
                {
                    if (childTile?.content != null && !childTile.contentUri.Contains(".json"))
                    {
                        if (childTile.content.State == Content.ContentLoadState.DOWNLOADED)
                        {
                            result++;
                        }
                    }
                }

                // Recursively count loaded children
                foreach (var childTile in tile.children)
                {
                    if (childTile != null)
                    {
                        result += CountLoadedChildren(childTile);
                    }
                }
            }
            
            return result;
        }

        /// <summary>
        /// Count loaded parents for a tile
        /// </summary>
        public static int CountLoadedParents(Tile tile)
        {
            if (tile.refine == "ADD")
                return 1;

            int result = 0;
            if (tile.parent?.content != null && !tile.parent.contentUri.Contains(".json"))
            {
                if (tile.parent.content.State == Content.ContentLoadState.DOWNLOADED)
                {
                    result = 1;
                }
            }

            if (tile.parent != null)
            {
                return result + CountLoadedParents(tile.parent);
            }
            
            return result;
        }

        /// <summary>
        /// Count loading parents for a tile
        /// </summary>
        public static int CountLoadingParents(Tile tile)
        {
            int result = 0;
            if (tile.parent != null && tile.parent.isLoading && !tile.parent.contentUri.Contains(".json"))
            {
                if (tile.parent.content?.State != Content.ContentLoadState.DOWNLOADED)
                {
                    result = 1;
                }
            }

            if (tile.parent != null)
            {
                return result + CountLoadingParents(tile.parent);
            }
            
            return result;
        }

        /// <summary>
        /// Calculate Euler rotation to vertical for a tile
        /// </summary>
        public static Vector3 EulerRotationToVertical(Tile tile)
        {
            // Use translation from TileTransform instead of raw double[16]
            double tx = tile.tileTransform.m03;
            double ty = tile.tileTransform.m13;
            double tz = tile.tileTransform.m23;

            float posX = (float)(tx / 1000.0);
            float posY = (float)(ty / 1000.0);
            float posZ = (float)(tz / 1000.0);

            float angleX = -Mathf.Rad2Deg * Mathf.Atan(posY / Mathf.Max(posZ, 1e-6f));
            float angleY = -Mathf.Rad2Deg * Mathf.Atan(posX / Mathf.Max(posZ, 1e-6f));
            float angleZ = -Mathf.Rad2Deg * Mathf.Atan(posY / Mathf.Max(posX, 1e-6f));

            return new Vector3(angleX, angleY, angleZ);
        }

        /// <summary>
        /// Calculate quaternion rotation to vertical for a tile
        /// </summary>
        public static Quaternion RotationToVertical(Tile tile)
        {
            double tx = tile.tileTransform.m03;
            double ty = tile.tileTransform.m13;
            double tz = tile.tileTransform.m23;

            float posX = (float)(tx / 1_000_000.0);
            float posY = (float)(ty / 1_000_000.0);
            float posZ = (float)(tz / 1_000_000.0);

            return Quaternion.FromToRotation(new Vector3(posX, posY, posZ), new Vector3(0, 0, 1));
        }

        /// <summary>
        /// Check if tile is in view frustum
        /// </summary>
        public static bool IsInViewFrustum(Tile tile, Camera camera)
        {
            if (camera == null) return false;
            
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            return GeometryUtility.TestPlanesAABB(planes, tile.ContentBounds);
        }

        /// <summary>
        /// Check if tile is critical for navigation (has .json/.subtree or loading children)
        /// </summary>
        public static bool IsCriticalForNavigation(Tile tile)
        {
            // Navigation nodes are always critical
            if (!string.IsNullOrEmpty(tile.contentUri) && 
                (tile.contentUri.Contains(".json") || tile.contentUri.Contains(".subtree")))
                return true;
                
            // Tiles with loading children are critical
            return CountLoadingChildren(tile) > 0;
        }

        /// <summary>
        /// Check if tile should be kept for display purposes
        /// </summary>
        public static bool ShouldKeepForDisplay(Tile tile)
        {
            // Always keep if critical for navigation
            if (IsCriticalForNavigation(tile))
                return true;
                
            // Keep tiles with loaded content that might still be needed
            if (tile.content?.State == Content.ContentLoadState.DOWNLOADED)
            {
                // If we have loaded children, we can be replaced
                return CountLoadedChildren(tile) == 0;
            }
                
            return false;
        }

        /// <summary>
        /// Calculate screen space error for a tile
        /// </summary>
        public static float CalculateScreenSpaceError(Tile tile, Camera camera, Vector3 closestPointOnBounds)
        {
            if (camera == null) return float.MaxValue;

            float sse;
            if (camera.orthographic)
            {
                Bounds bounds = tile.ContentBounds;
                float maxBoundsSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                sse = maxBoundsSize / camera.orthographicSize;
            }
            else
            {
                float distance = Vector3.Distance(closestPointOnBounds, camera.transform.position);
                if (distance <= 0) distance = 0.1f;
                
                sse = (float)(tile.geometricError / distance);
            }
            
            return sse;
        }

        /// <summary>
        /// Get the screen space error of the parent tile, recursively searching up the hierarchy
        /// </summary>
        /// <param name="tile">The tile to get parent SSE for</param>
        /// <returns>Parent's screen space error, or 0 if no parent with downloaded content found</returns>
        public static float GetParentSSE(Tile tile)
        {
            float result = 0;
            if (tile.parent != null)
            {
                if (tile.parent.content != null)
                {
                    if (tile.parent.content.State == Content.ContentLoadState.DOWNLOADED)
                    {
                        result = tile.parent.screenSpaceError;
                    }
                }
                if (result == 0)
                {
                    result = GetParentSSE(tile.parent);
                }
            }
            return result;
        }

        /// <summary>
        /// Calculate Unity bounds from the tile's bounding volume
        /// </summary>
        /// <param name="tile">The tile to calculate bounds for</param>
        public static void CalculateUnitBounds(Tile tile)
        {
            if (tile.boundingVolume == null || tile.boundingVolume.values.Length == 0)
            {
                tile.boundsAreValid = false;
                return;
            }

            tile.boundsAvailable = true;
            switch (tile.boundingVolume.boundingVolumeType)
            {
                case BoundingVolumeType.Box:
                    CalculateBoxBounds(tile);
                    break;
                case BoundingVolumeType.Sphere:
                    CalculateSphereBounds(tile);
                    break;
                case BoundingVolumeType.Region:
                    CalculateRegionBounds(tile);
                    break;
                default:
                    break;
            }

            tile.boundsAvailable = true;
        }

        private static void CalculateBoxBounds(Tile tile)
        {
            var values = tile.boundingVolume.values;
            var coordSystem = tile.tileSet.contentCoordinateSystem;

            Coordinate boxCenterEcef = new Coordinate(coordSystem, values[0], values[1], values[2]);
            Coordinate Xaxis = new Coordinate(coordSystem, values[3], values[4], values[5]);
            Coordinate Yaxis = new Coordinate(coordSystem, values[6], values[7], values[8]);
            Coordinate Zaxis = new Coordinate(coordSystem, values[9], values[10], values[11]);

            var bounds = new Bounds();
            bounds.center = boxCenterEcef.ToUnity();

            bounds.Encapsulate((boxCenterEcef + Xaxis + Yaxis + Zaxis).ToUnity());
            bounds.Encapsulate((boxCenterEcef + Xaxis + Yaxis - Zaxis).ToUnity());
            bounds.Encapsulate((boxCenterEcef + Xaxis - Yaxis + Zaxis).ToUnity());
            bounds.Encapsulate((boxCenterEcef + Xaxis - Yaxis - Zaxis).ToUnity());
            
            bounds.Encapsulate((boxCenterEcef - Xaxis + Yaxis + Zaxis).ToUnity());
            bounds.Encapsulate((boxCenterEcef - Xaxis - Yaxis + Zaxis).ToUnity());
            bounds.Encapsulate((boxCenterEcef - Xaxis + Yaxis - Zaxis).ToUnity());
            bounds.Encapsulate((boxCenterEcef - Xaxis - Yaxis - Zaxis).ToUnity());

            double deltaX = Math.Abs(Xaxis.value1) + Math.Abs(Yaxis.value1) + Math.Abs(Zaxis.value1);
            double deltaY = Math.Abs(Xaxis.value2) + Math.Abs(Yaxis.value2) + Math.Abs(Zaxis.value2);
            double deltaZ = Math.Abs(Xaxis.value3) + Math.Abs(Yaxis.value3) + Math.Abs(Zaxis.value3);
            
            tile.BottomLeft = new Coordinate(coordSystem, boxCenterEcef.value1 - deltaX, boxCenterEcef.value2 - deltaY, boxCenterEcef.value3 - deltaZ);
            tile.TopRight = new Coordinate(coordSystem, boxCenterEcef.value1 + deltaX, boxCenterEcef.value2 + deltaY, boxCenterEcef.value3 + deltaZ);
            tile.ContentBounds = bounds;
        }

        private static void CalculateSphereBounds(Tile tile)
        {
            var values = tile.boundingVolume.values;
            var sphereRadius = values[3];
            var sphereCentre = new Coordinate(CoordinateSystem.WGS84_ECEF, values[0], values[1], values[2]).ToUnity();
            var sphereMin = new Coordinate(CoordinateSystem.WGS84_ECEF, values[0] - sphereRadius, values[1] - sphereRadius, values[2] - sphereRadius).ToUnity();
            var sphereMax = new Coordinate(CoordinateSystem.WGS84_ECEF, values[0] + sphereRadius, values[1] + sphereRadius, values[2] + sphereRadius).ToUnity();
            
            var bounds = new Bounds();
            bounds.size = Vector3.zero;
            bounds.center = sphereCentre;
            bounds.Encapsulate(sphereMin);
            bounds.Encapsulate(sphereMax);
            
            tile.BottomLeft = new Coordinate(CoordinateSystem.WGS84_ECEF, values[0] - sphereRadius, values[1] - sphereRadius, values[2] - sphereRadius);
            tile.TopRight = new Coordinate(CoordinateSystem.WGS84_ECEF, values[0] + sphereRadius, values[1] + sphereRadius, values[2] + sphereRadius);
            tile.ContentBounds = bounds;
        }

        private static void CalculateRegionBounds(Tile tile)
        {
            var values = tile.boundingVolume.values;
            
            //Array order: west, south, east, north, minimum height, maximum height
            double West = (values[0] * 180.0f) / Mathf.PI;
            double South = (values[1] * 180.0f) / Mathf.PI;
            double East = (values[2] * 180.0f) / Mathf.PI;
            double North = (values[3] * 180.0f) / Mathf.PI;
            double MaxHeight = values[4];
            double minHeight = values[5];

            var mincoord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, West, minHeight).Convert(CoordinateSystem.WGS84_ECEF);
            var maxcoord = new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, West, minHeight).Convert(CoordinateSystem.WGS84_ECEF);

            // Calculate bounds by checking all 8 corners of the region
            var corners = new[]
            {
                new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, West, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF),
                new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, East, minHeight).Convert(CoordinateSystem.WGS84_ECEF),
                new Coordinate(CoordinateSystem.WGS84_LatLonHeight, South, East, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF),
                new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, West, minHeight).Convert(CoordinateSystem.WGS84_ECEF),
                new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, West, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF),
                new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, East, minHeight).Convert(CoordinateSystem.WGS84_ECEF),
                new Coordinate(CoordinateSystem.WGS84_LatLonHeight, North, East, MaxHeight).Convert(CoordinateSystem.WGS84_ECEF)
            };

            foreach (var coord in corners)
            {
                if (coord.easting < mincoord.easting) mincoord.easting = coord.easting;
                if (coord.northing < mincoord.northing) mincoord.northing = coord.northing;
                if (coord.height < mincoord.height) mincoord.height = coord.height;
                if (coord.easting > maxcoord.easting) maxcoord.easting = coord.easting;
                if (coord.northing > maxcoord.northing) maxcoord.northing = coord.northing;
                if (coord.height > maxcoord.height) maxcoord.height = coord.height;
            }

            var unityMin = mincoord.ToUnity();
            var unityMax = maxcoord.ToUnity();

            var bounds = new Bounds();
            bounds.size = Vector3.zero;
            bounds.center = unityMin;
            bounds.Encapsulate(unityMax);

            tile.BottomLeft = mincoord;
            tile.TopRight = maxcoord;
            tile.ContentBounds = bounds;
        }

        /// <summary>
        /// Check if all children have downloaded content
        /// </summary>
        /// <param name="tile">The tile to check children for</param>
        /// <returns>True if all children have downloaded content, or if no children exist</returns>
        public static bool ChildrenHaveContent(Tile tile)
        {
            if (tile.ChildrenCount > 0) 
            { 
                foreach (var child in tile.children)
                {
                    if (!child.content || child.content.State != Content.ContentLoadState.DOWNLOADED) return false;
                    break;
                }
            }
            return true;
        }

        /// <summary>
        /// Get the maximum nesting depth of child tiles
        /// </summary>
        /// <param name="tile">The tile to calculate nesting depth for</param>
        /// <returns>Maximum depth of nested children</returns>
        public static int GetNestingDepth(Tile tile)
        {
            int maxDepth = 1;
            if (tile.ChildrenCount > 0)
            {
                foreach (var child in tile.children)
                {
                    int depth = GetNestingDepth(child) + 1;
                    if (depth > maxDepth) maxDepth = depth;
                }
            }
            return maxDepth;
        }

        /// <summary>
        /// Check if a point is within the tile's bounds with margin
        /// </summary>
        /// <param name="tile">The tile to check bounds for</param>
        /// <param name="point">The point to check</param>
        /// <param name="margin">Margin to add to bounds check</param>
        /// <returns>True if point is within bounds</returns>
        public static bool IsPointInbounds(Tile tile, Coordinate point, double margin)
        {
            if (point.PointsLength > 2)
            {
                if (point.value3 + margin < tile.BottomLeft.value3)
                {
                    return false;
                }

                if (point.value3 - margin > tile.TopRight.value3)
                {
                    return false;
                }
            }

            if (point.value1 + margin < tile.BottomLeft.value1)
            {
                return false;
            }
            if (point.value2 + margin < tile.BottomLeft.value2)
            {
                return false;
            }
            if (point.value1 - margin > tile.TopRight.value1)
            {
                return false;
            }
            if (point.value2 - margin > tile.TopRight.value2)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Check if the tile is in the camera's view frustum
        /// </summary>
        /// <param name="tile">The tile to check</param>
        /// <param name="camera">The camera to check against</param>
        /// <returns>True if tile is in view</returns>
        public static bool IsInViewFrustrum(Tile tile, Camera camera)
        {
            if (!tile.boundsAvailable && tile.boundsAreValid)
            {
                if (tile.boundingVolume.values.Length > 0)
                {
                    CalculateUnitBounds(tile);
                }
                else
                {
                    tile.inView = false;
                }
            }
            
            if (tile.boundsAvailable)
            {
                tile.inView = false;
                if (IsPointInbounds(tile, new Coordinate(camera.transform.position).Convert(tile.tileSet.contentCoordinateSystem), 8000d))
                {
                    tile.inView = camera.InView(tile.ContentBounds);
                }
            }
            
            return tile.inView;
        }
    }
}
