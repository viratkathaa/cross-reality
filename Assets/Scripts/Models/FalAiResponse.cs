using System;
using UnityEngine;

/// <summary>
/// Response from FAL AI file upload endpoint.
/// </summary>
[System.Serializable]
public class FalUploadResponse
{
    public string url;
    public string content_type;
    public string file_name;
    public int file_size;
}

/// <summary>
/// Response from FAL AI upload/initiate - contains pre-signed upload URL and final CDN URL.
/// </summary>
[System.Serializable]
public class FalUploadInitiateResponse
{
    public string upload_url;   // PUT bytes here (pre-signed GCS URL)
    public string file_url;     // Use this URL with SAM-3D
}

namespace FalAi.Models
{
    /// <summary>
    /// Represents the response from FAL AI SAM-3D API.
    /// Actual response format:
    /// { "model_glb": { "url": "..." }, "gaussian_splat": { "url": "..." }, "metadata": [...] }
    /// </summary>
    [System.Serializable]
    public class FalAiResponse
    {
        [SerializeField] public FileInfo model_glb;
        [SerializeField] public FileInfo gaussian_splat;
        [SerializeField] public FileInfo artifacts_zip;
    }

    /// <summary>
    /// File info object returned by FAL AI.
    /// </summary>
    [System.Serializable]
    public class FileInfo
    {
        [SerializeField] public string url;
        [SerializeField] public string content_type;
        [SerializeField] public string file_name;
        [SerializeField] public int file_size;
    }

    /// <summary>
    /// Request payload for FAL AI API.
    /// Only include image_url and prompt. Keep it minimal to match API expectations.
    /// </summary>
    [System.Serializable]
    public class FalAiRequest
    {
        [SerializeField] public string image_url;
        [SerializeField] public string prompt;
        [SerializeField] public float detection_threshold;

        public FalAiRequest(string imageUrl, string objectPrompt = "car")
        {
            image_url = imageUrl;
            prompt = objectPrompt;
            detection_threshold = 0.1f;
        }
    }

    /// <summary>
    /// Wrapper for FAL API polling responses (for checking async job status).
    /// </summary>
    [System.Serializable]
    public class FalAiStatusResponse
    {
        [SerializeField] public string request_id;
        [SerializeField] public string status;        // "PENDING", "IN_PROGRESS", "COMPLETED", "FAILED"
        [SerializeField] public FalAiResponse result;
        [SerializeField] public string error;
        [SerializeField] public float progress;       // Progress percentage if available
    }
}
