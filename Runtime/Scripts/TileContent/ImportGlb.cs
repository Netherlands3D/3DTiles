using GLTFast;
using Netherlands3D.Coordinates;
using SimpleJSON;
using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Netherlands3D.Tiles3D
{
    public class ImportGlb
    {
        private static ImportSettings importSettings = new ImportSettings() { AnimationMethod = AnimationMethod.None };

        public async Task Load(byte[] data, Tile tile, Transform containerTransform, Action<bool> succesCallback, string sourcePath, bool parseAssetMetaData = false, bool parseSubObjects = false, UnityEngine.Material overrideMaterial = null, bool verbose = false)
        {
            var consoleLogger = new GLTFast.Logging.ConsoleLogger();
            
            var materialGenerator = new NL3DMaterialGenerator();
            GltfImport gltf = new GltfImport(null, null, materialGenerator, consoleLogger);
            
            var success = true;
            Uri uri = null;
            if (sourcePath != "")
            {
                uri = new Uri(sourcePath);
            }
            RemoveCesiumRtcFromRequieredExtentions(ref data);

            if(verbose)
                Debug.Log("starting gltfLoad");
    
            success = await gltf.LoadGltfBinary(data, uri, importSettings);
            
            if(verbose)
                Debug.Log("gltfLoad has finished");

            if (success == false)
            {
                Debug.Log("cant load glb: " + sourcePath);
                succesCallback.Invoke(false);
                return;
            }
            
            // Validate data before processing
            if (data == null || data.Length == 0)
            {
                Debug.LogError("GLB data is null or empty");
                succesCallback.Invoke(false);
                return;
            }

            double[] rtcCenter = GetRTCCenterFromGlb(data);
            var parsedGltf = new ParsedGltf()
            {
                gltfImport = gltf,
                rtcCenter = rtcCenter,
            };

            try
            {
                // Check if containerTransform is still valid before proceeding
                if (containerTransform == null)
                {
                    Debug.LogWarning("Container transform is null, canceling GLB processing");
                    succesCallback.Invoke(false);
                    return;
                }

                // Additional validation before spawning scenes
                if (parsedGltf.gltfImport == null)
                {
                    Debug.LogError("GltfImport is null, cannot spawn scenes");
                    succesCallback.Invoke(false);
                    return;
                }

                // Add timeout to prevent hanging
                var timeout = System.TimeSpan.FromSeconds(30); // 30 second timeout
                using (var cts = new System.Threading.CancellationTokenSource(timeout))
                {
                    try
                    {
                        await parsedGltf.SpawnGltfScenes(containerTransform);
                    }
                    catch (System.OperationCanceledException)
                    {
                        Debug.LogError("GLB processing timed out after 30 seconds");
                        succesCallback.Invoke(false);
                        return;
                    }
                }

                // Check again after async operation in case transform was destroyed
                if (containerTransform == null)
                {
                    Debug.LogWarning("Container transform destroyed during GLB processing");
                    succesCallback.Invoke(false);
                    return;
                }

                containerTransform.gameObject.name = sourcePath;

                // Register GltfImport with Content for later disposal
                Content content = containerTransform.GetComponent<Content>();
                if (content == null)
                {
                    Debug.LogWarning("Content component destroyed during GLB processing");
                    succesCallback.Invoke(false);
                    return;
                }
                
                content.RegisterGltfImport(gltf);

                if (parseAssetMetaData)
                {
                    if (content != null)
                    {
                        // parsedGltf.ParseAssetMetaData(content);
                    }

                }

                //Check if mesh features addon is used to define subobjects
#if SUBOBJECT
                if (parseSubObjects)
                {
                    // parsedGltf.ParseSubObjects(containerTransform);
                }
#endif

                if (overrideMaterial != null)
                {
                    parsedGltf.OverrideAllMaterials(overrideMaterial);
                }

                succesCallback.Invoke(true);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error processing GLB content: {ex.Message}");
                succesCallback.Invoke(false);
            }
            finally
            {
                // Modern GLTFast automatically handles native array disposal
                // No manual disposal needed anymore
            }
        }
       
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

            return;
            //string ExtentionsRequiredString = "\"extensionsRequired\"";
            //int extentionsStart = jsonstring.IndexOf(ExtentionsRequiredString);
            //if (extentionsStart < 0)
            //{
            //    return;
            //}
            //int extentionstringEnd = extentionsStart + ExtentionsRequiredString.Length;

            //int arrayEnd = jsonstring.IndexOf("]", extentionstringEnd);
            //string cesiumString = "\"CESIUM_RTC\"";
            //int cesiumstringStart = jsonstring.IndexOf(cesiumString, extentionstringEnd);
            //if (cesiumstringStart < 0)
            //{
            //    Debug.Log("no cesium_rtc required");
            //    return;
            //}
            //Debug.Log("cesium_rtc required");
            //int cesiumstringEnd = cesiumstringStart + cesiumString.Length;
            //int seperatorPosition = jsonstring.IndexOf(",", extentionstringEnd);


            //int removalStart = cesiumstringStart;
            //int removalEnd = cesiumstringEnd;
            //if (seperatorPosition > arrayEnd)
            //{
            //    removalStart = extentionsStart - 1;
            //    removalEnd = arrayEnd + 1;
            //}
            //else
            //{
            //    if (seperatorPosition < cesiumstringStart)
            //    {
            //        removalStart = seperatorPosition;
            //    }
            //    if (seperatorPosition > cesiumstringEnd)
            //    {
            //        removalEnd = seperatorPosition;
            //    }
            //}

            //for (int i = removalStart; i < removalEnd; i++)
            //{
            //    b3dm.GlbData[i + jsonstart] = 0x20;
            //}








        }
    }
}
