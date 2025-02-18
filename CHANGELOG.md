# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.10.4] - 19-2-2025

### Fixed
- Reduced garbage by using new value variables instead of points array for coordinates.

## [1.10.3] - 23-1-2025

### Fixed
- Fixed BoundingVolume-transformation

## [1.10.2] - 14-1-2025

### Fixed
- QueryParameters now also works with previous keys

## [1.10.1] - 10-1-2025

### Fixed
- QueryParameters for tileContent no longer overwritten when trying to add authentication-parameters 

## [1.10.0] - 13-12-2024

### Added
- Added full support for tiletransforms

## [1.9.0] - 12-12-2024

### Added
- Added function to clear the key query from the url, this will allow keys to be removed if this is desired, and it will prevent multiple valid keys from being added to the url as was the case before

## [1.8.0] - 11-12-2024

### Added
- Added support for CESIUM_RTC extension in gltf and glb-files
- Added support for refine-method Add
- Added support for tiletransforms, (translation only)

## [1.7.0] - 24-06-2024

### Added
- Added new event that returns new Metadata objects if meta data parsing is enabled. Metadata is read from gtlf/glb the Asset node

## [1.6.1] - 18-06-2024

### Fixed
- Fixed the way tileset urls and keys are combined, solving issue with empty url query parameters

## [1.6.0] - 18-06-2024

### Added
- Added new event that returns the internal UnityWebRequest after requesting the tileset .json

## [1.5.3] - 13-06-2024

### Fixed
- Instantiated gltf scenes now recursively acquire the layer of the Content gameObject.

## [1.5.2] - 07-06-2024

### Fixed
- Tile-locations were incorrect after re-enabeling tileLayer

## [1.5.1] - 07-06-2024

### Fixed

- tiles, very far away from the camera where loaded because cameraFrustumCheck couldn't handle very large positions. added pre-check, checking if camera is close to or inside bounds, calculating in the coordaintesystem of the 3d-tileset

## [1.5.0] - 04-06-2024

### Added

- Added method 'AddCustomHeader' to set custom headers for the internal web requests
- Added property 'QueryKeyName' to change the key name from the default used by Google API ('key') to something else (For example 'code' or 'api_key' etc.)

## [1.4.1] - 14-05-2024

### Removed

- Validation whether the filename for the tileset is `tileset.json`, Google -and possibly others- use `root.json`.

## [1.4.0] - 29-04-2024

### Added

- The usedExtensions node in the root node are now parsed and checked against supported extensions

## [1.3.0] - 24-04-2024

### Added

- Added function to refresh the tiles, for example after the url changes.
- Added rudimentary check if URL is valid. 

### Fixed

- move all loaded scenes inside gltf to coordinate instead of just scene index 0


## [1.2.4] - 23-04-2024

### Fixed

- Removed forced assignment to layer 11 of the tile objects. Tile objects will take the parent object's layer by default, and will allow for manual assignment thereafter.

## [1.2.3] - 08-04-2024

### Fixed

- fixed calculation of bounds for items that do not have a boundingVolume available
- added public RecalculateBounds method to recalculate all tilebounds in tileset
- removed private legacy method 

## [1.2.1] - 16-02-2024

### Fixed

- fix calculating unitybounds from boudingVolumeType Box.
- fix reading subdivisionSceme

## [1.2.0] - 16-02-2024

### Added

- added support for boudingVolumeType Box in combination with Implicit Tiling

## [1.1.0] - 28-11-2023

### Added

- Added ability to read meshfeatures and store them using the subobjects-package
- Added support for implicit Tiling 

## [1.0.2] - 19-09-2023

### Changed

- Added unity keyword and value in package.json for discoverability in the package manager

## [1.0.1] - 15-09-2023

### Changed

- moved to version 1.0.1. of dependency eu.netherlands3d.coordinates. the bugfix makes sure the 3dTiles are allways correctly orientated.

## [1.0.0]

### Added

- First release
