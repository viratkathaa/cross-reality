using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using FalAi.Models;

/// <summary>
/// Client for communicating with FAL AI SAM-3D API.
/// Handles image upload and GLB model retrieval.
/// </summary>
public class FalAiClient : MonoBehaviour
{
    // FAL AI API endpoints
    private const string FAL_API_BASE = "https://fal.run";
    private const string SAM3D_ENDPOINT = "/fal-ai/sam-3/3d-objects";
    private const string REQUEST_ID_HEADER = "X-Fal-Request-Id";

    [SerializeField] private string apiKey = "YOUR_FAL_AI_API_KEY"; // Replace with your key
    [SerializeField] private float requestTimeout = 120f;
    [SerializeField] private float pollingInterval = 2f;
    [SerializeField] private int maxPollingAttempts = 60; // 60 attempts * 2 seconds = 2 minutes max wait

    public event Action<FalAiResponse> OnResponseReceived;
    public event Action<string> OnError;
    public event Action<float> OnProgress;

    private string requestId;
    private int pollingAttempts;

    /// <summary>
    /// Sends an image to FAL AI - uploads it first to get a proper URL, then calls SAM-3D.
    /// </summary>
    public void SendImageToFalAi(Texture2D image, string objectPrompt = "object")
    {
        if (image == null)
        {
            OnError?.Invoke("Image is null");
            return;
        }

        StartCoroutine(UploadThenProcess(image, objectPrompt));
    }

    /// <summary>
    /// Uploads image to FAL AI storage first, then calls SAM-3D with the returned URL.
    /// This is more reliable than sending base64 data URIs.
    /// </summary>
    private IEnumerator UploadThenProcess(Texture2D image, string objectPrompt)
    {
        OnProgress?.Invoke(0.05f);
        Debug.Log("[FalAiClient] Starting FAL AI upload (2-step: initiate + PUT)...");

        byte[] pngBytes = image.EncodeToPNG();
        string imageUrl = null;

        // ── Step 1: POST to initiate endpoint to get a pre-signed GCS upload URL ──
        string initiateUrl = "https://rest.alpha.fal.ai/storage/upload/initiate";
        string initiateBody = $"{{\"content_type\":\"image/png\",\"file_size\":{pngBytes.Length},\"file_name\":\"capture.png\"}}";
        byte[] initiateBodyBytes = System.Text.Encoding.UTF8.GetBytes(initiateBody);

        Debug.Log($"[FalAiClient] Initiating upload: POST {initiateUrl}");
        UnityWebRequest initiateReq = new UnityWebRequest(initiateUrl, "POST");
        initiateReq.uploadHandler = new UploadHandlerRaw(initiateBodyBytes);
        initiateReq.downloadHandler = new DownloadHandlerBuffer();
        initiateReq.SetRequestHeader("Authorization", $"Key {apiKey}");
        initiateReq.SetRequestHeader("Content-Type", "application/json");
        initiateReq.timeout = 30;

        yield return initiateReq.SendWebRequest();

        string initiateResponse = initiateReq.downloadHandler.text;
        long initiateCode = initiateReq.responseCode;
        bool initiateOk = initiateReq.result == UnityWebRequest.Result.Success;
        initiateReq.Dispose();

        Debug.Log($"[FalAiClient] Initiate → HTTP {initiateCode}: {initiateResponse}");

        string uploadUrl = null;
        string fileUrl = null;

        if (initiateOk)
        {
            try
            {
                var ir = JsonUtility.FromJson<FalUploadInitiateResponse>(initiateResponse);
                uploadUrl = ir?.upload_url;
                fileUrl = ir?.file_url;
            }
            catch { }

            // Manual extraction fallback
            if (string.IsNullOrEmpty(uploadUrl))
            {
                uploadUrl = ExtractJsonString(initiateResponse, "upload_url");
                fileUrl = ExtractJsonString(initiateResponse, "file_url");
            }
        }

        if (!string.IsNullOrEmpty(uploadUrl))
        {
            // ── Step 2: PUT raw bytes to the pre-signed GCS URL ──
            Debug.Log($"[FalAiClient] Uploading bytes via PUT to pre-signed URL...");
            UnityWebRequest putReq = new UnityWebRequest(uploadUrl, "PUT");
            putReq.uploadHandler = new UploadHandlerRaw(pngBytes);
            putReq.downloadHandler = new DownloadHandlerBuffer();
            putReq.SetRequestHeader("Content-Type", "image/png");
            putReq.timeout = (int)requestTimeout;

            yield return putReq.SendWebRequest();

            long putCode = putReq.responseCode;
            bool putOk = putReq.result == UnityWebRequest.Result.Success;
            string putResponse = putReq.downloadHandler.text;
            putReq.Dispose();

            Debug.Log($"[FalAiClient] PUT → HTTP {putCode}: {putResponse}");

            if (putOk && !string.IsNullOrEmpty(fileUrl))
            {
                imageUrl = fileUrl;
                Debug.Log($"[FalAiClient] Upload successful! CDN URL: {imageUrl}");
            }
            else
            {
                Debug.LogWarning($"[FalAiClient] PUT failed (HTTP {putCode}), falling back to base64...");
            }
        }
        else
        {
            Debug.LogWarning($"[FalAiClient] Initiate failed (HTTP {initiateCode}), falling back to base64...");
        }

        if (string.IsNullOrEmpty(imageUrl))
        {
            // Last resort: base64 data URI (may fail segmentation on some images)
            Debug.LogWarning("[FalAiClient] Using base64 fallback (may fail segmentation)...");
            string base64 = Convert.ToBase64String(pngBytes);
            imageUrl = $"data:image/png;base64,{base64}";
        }

        OnProgress?.Invoke(0.1f);
        StartCoroutine(SendImageCoroutine(imageUrl, objectPrompt));
    }

    /// <summary>Helper to extract a string value from a JSON string without full deserialization.</summary>
    private static string ExtractJsonString(string json, string key)
    {
        string search = $"\"{key}\":\"";
        int start = json.IndexOf(search);
        if (start < 0) return null;
        start += search.Length;
        int end = json.IndexOf("\"", start);
        if (end < 0) return null;
        return json.Substring(start, end - start);
    }

    /// <summary>
    /// Sends an image URL directly to FAL AI (no upload needed).
    /// Use this when you already have a publicly accessible image URL.
    /// </summary>
    public void SendImageUrlToFalAi(string imageUrl, string objectPrompt = "all objects")
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            OnError?.Invoke("Image URL is empty");
            return;
        }

        Debug.Log($"[FalAiClient] Sending image URL directly: {imageUrl}");
        StartCoroutine(SendImageCoroutine(imageUrl, objectPrompt));
    }

    /// <summary>
    /// Sends a base64-encoded image to FAL AI.
    /// </summary>
    public void SendBase64ImageToFalAi(string base64Image, string objectPrompt = "object")
    {
        if (string.IsNullOrEmpty(base64Image))
        {
            OnError?.Invoke("Base64 image is empty");
            return;
        }

        string dataUrl = $"data:image/png;base64,{base64Image}";
        StartCoroutine(SendImageCoroutine(dataUrl, objectPrompt));
    }

    /// <summary>
    /// Sends an image from file path to FAL AI.
    /// </summary>
    public void SendImageFromFile(string imagePath, string objectPrompt = "object")
    {
        try
        {
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            Texture2D texture = new Texture2D(1, 1);
            texture.LoadImage(imageBytes);
            SendImageToFalAi(texture, objectPrompt);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to load image: {ex.Message}");
        }
    }

    private IEnumerator SendImageCoroutine(string imageDataUrl, string objectPrompt)
    {
        // Build JSON manually - image_url + low detection threshold for best results
        string jsonPayload = $"{{\"image_url\":\"{imageDataUrl}\",\"prompt\":\"{objectPrompt}\",\"detection_threshold\":0.1}}";

        Debug.Log($"[FalAiClient] JSON payload: {jsonPayload}");
        Debug.Log($"[FalAiClient] URL: {FAL_API_BASE + SAM3D_ENDPOINT}");

        // Build request
        string url = FAL_API_BASE + SAM3D_ENDPOINT;
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);

        UnityWebRequest www = new UnityWebRequest(url, "POST");
        www.uploadHandler = new UploadHandlerRaw(bodyRaw);
        www.downloadHandler = new DownloadHandlerBuffer();

        // Set headers
        www.SetRequestHeader("Content-Type", "application/json");
        www.SetRequestHeader("Authorization", $"Key {apiKey}");

        www.timeout = (int)requestTimeout;

        OnProgress?.Invoke(0.1f);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            string errorMsg = $"API request failed: {www.error}\nResponse: {www.downloadHandler.text}";
            Debug.LogError($"[FalAiClient] {errorMsg}");
            OnError?.Invoke(errorMsg);
            www.Dispose();
            yield break;
        }

        // Parse response (moved outside try-catch to allow yield)
        string responseJson = www.downloadHandler.text;
        Debug.Log($"[FalAiClient] Response received successfully (length={responseJson.Length})");

        ParseAndHandleResponse(responseJson);

        www.Dispose();
    }

    private void ParseAndHandleResponse(string responseJson)
    {
        try
        {
            // Extract request ID if provided
            var statusResponse = JsonUtility.FromJson<FalAiStatusResponse>(responseJson);
            if (statusResponse != null && !string.IsNullOrEmpty(statusResponse.request_id))
            {
                requestId = statusResponse.request_id;
                Debug.Log($"[FalAiClient] Request ID: {requestId}");

                // If status is COMPLETED, use result directly
                if (statusResponse.status == "COMPLETED" && statusResponse.result != null)
                {
                    OnProgress?.Invoke(1.0f);
                    OnResponseReceived?.Invoke(statusResponse.result);
                    return;
                }

                // Otherwise, poll for status
                StartCoroutine(PollForResultCoroutine());
            }
            else
            {
                // Parse as direct response with model_glb field
                var directResponse = JsonUtility.FromJson<FalAiResponse>(responseJson);
                if (directResponse != null && directResponse.model_glb != null && !string.IsNullOrEmpty(directResponse.model_glb.url))
                {
                    Debug.Log($"[FalAiClient] GLB URL: {directResponse.model_glb.url}");
                    OnProgress?.Invoke(1.0f);
                    OnResponseReceived?.Invoke(directResponse);
                }
                else
                {
                    OnError?.Invoke("Invalid response format from API");
                }
            }
        }
        catch (Exception ex)
        {
            string errorMsg = $"Failed to parse response: {ex.Message}";
            Debug.LogError($"[FalAiClient] {errorMsg}");
            OnError?.Invoke(errorMsg);
        }
    }

    private IEnumerator PollForResultCoroutine()
    {
        pollingAttempts = 0;

        while (pollingAttempts < maxPollingAttempts)
        {
            pollingAttempts++;
            float progress = 0.1f + (pollingAttempts / (float)maxPollingAttempts) * 0.8f;
            OnProgress?.Invoke(progress);

            yield return new WaitForSeconds(pollingInterval);

            // Create polling request
            string url = FAL_API_BASE + SAM3D_ENDPOINT;
            UnityWebRequest www = UnityWebRequest.Get(url);
            www.SetRequestHeader("Authorization", $"Key {apiKey}");
            www.SetRequestHeader(REQUEST_ID_HEADER, requestId);
            www.timeout = (int)requestTimeout;

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[FalAiClient] Polling request failed: {www.error}");
                www.Dispose();
                continue;
            }

            try
            {
                string responseJson = www.downloadHandler.text;
                var statusResponse = JsonUtility.FromJson<FalAiStatusResponse>(responseJson);

                if (statusResponse.status == "COMPLETED" && statusResponse.result != null)
                {
                    Debug.Log("[FalAiClient] Result ready!");
                    OnProgress?.Invoke(1.0f);
                    OnResponseReceived?.Invoke(statusResponse.result);
                    www.Dispose();
                    yield break;
                }
                else if (statusResponse.status == "FAILED")
                {
                    string errorMsg = statusResponse.error ?? "Unknown error";
                    Debug.LogError($"[FalAiClient] Request failed: {errorMsg}");
                    OnError?.Invoke(errorMsg);
                    www.Dispose();
                    yield break;
                }

                Debug.Log($"[FalAiClient] Status: {statusResponse.status}, Progress: {statusResponse.progress}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FalAiClient] Failed to parse status response: {ex.Message}");
            }

            www.Dispose();
        }

        OnError?.Invoke("Request timeout: API took too long to process");
    }

    /// <summary>
    /// Downloads a file from a URL and saves it to persistent storage.
    /// Returns the file path.
    /// </summary>
    public IEnumerator DownloadFileCoroutine(string downloadUrl, string filename, System.Action<string> onComplete, System.Action<string> onError)
    {
        if (string.IsNullOrEmpty(downloadUrl))
        {
            onError?.Invoke("Download URL is empty");
            yield break;
        }

        string filePath = Path.Combine(Application.persistentDataPath, filename);

        UnityWebRequest www = UnityWebRequest.Get(downloadUrl);
        www.timeout = (int)requestTimeout;

        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke($"Download failed: {www.error}");
            www.Dispose();
            yield break;
        }

        try
        {
            byte[] data = www.downloadHandler.data;
            File.WriteAllBytes(filePath, data);
            Debug.Log($"[FalAiClient] File downloaded: {filePath} ({data.Length} bytes)");
            onComplete?.Invoke(filePath);
        }
        catch (Exception ex)
        {
            onError?.Invoke($"Failed to save file: {ex.Message}");
        }

        www.Dispose();
    }

    /// <summary>
    /// Sets the API key. Call this at runtime if not set in inspector.
    /// </summary>
    public void SetApiKey(string key)
    {
        apiKey = key;
        Debug.Log("[FalAiClient] API key set");
    }

    /// <summary>
    /// Validates that the API key is set.
    /// </summary>
    public bool IsApiKeySet()
    {
        return !string.IsNullOrEmpty(apiKey) && apiKey != "YOUR_FAL_AI_API_KEY";
    }
}
