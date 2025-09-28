using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Netherlands3D.Tiles3D
{
    public class DownloadHelper : MonoBehaviour
    {
        // Keep a coroutine-based helper so callers can StartCoroutine(downloadData(...)).
        // Important: we return a copy of the downloaded bytes to the caller and dispose
        // the UnityWebRequest before invoking the callback. Returning the DownloadHandler
        // instance directly is unsafe because disposing the UnityWebRequest invalidates it.
        public void downloadData(string url, System.Action<byte[]> returnTo)
        {
            StartCoroutine(DownloadData(url, returnTo));
        }

        IEnumerator DownloadData(string url, System.Action<byte[]> returnTo)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(url))
            {
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log($"Could not load tileset from url:{url} Error:{www.error}");
                    // safe to invoke with null to indicate failure
                    returnTo?.Invoke(null);
                    yield break;
                }

                // Copy downloaded data so we can dispose the request early.
                byte[] data = null;
                try
                {
                    data = www.downloadHandler?.data;
                }
                catch
                {
                    data = null;
                }

                // Dispose happens automatically by the using block when we exit.
                // Invoke the callback with the copied data.
                returnTo?.Invoke(data);
            }
        }
    }
}
