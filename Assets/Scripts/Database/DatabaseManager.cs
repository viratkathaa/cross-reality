using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Thin Firebase Realtime Database client that uses the REST API only —
/// no Firebase SDK required.  All network calls are UnityWebRequest coroutines
/// so they play nice with Unity's main thread.
///
/// Setup (Inspector):
///   databaseUrl  →  https://YOUR-PROJECT.firebaseio.com
///   databaseSecret (optional) → your Database Secret for write auth
///                               Leave blank if rules allow public writes during dev.
/// </summary>
public class DatabaseManager : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────
    [Header("Firebase Config")]
    [Tooltip("e.g. https://your-project-default-rtdb.firebaseio.com")]
    [SerializeField] private string databaseUrl = "https://crossreality-ec12f-default-rtdb.asia-southeast1.firebasedatabase.app";

    [Tooltip("Firebase Database Secret (legacy auth). Leave blank if rules are open during dev.")]
    [SerializeField] private string databaseSecret = "YOUR_FIREBASE_SECRET_HERE";

    [Header("Collection")]
    [Tooltip("Firebase path where model records are written, e.g. 'models'")]
    [SerializeField] private string collectionPath = "models";

    // ── Events ─────────────────────────────────────────────────────────────────
    /// <summary>Fired on success with the Firebase-generated key (e.g. "-NxABC123").</summary>
    public event Action<string> OnSaveSuccess;
    /// <summary>Fired on failure with an error description.</summary>
    public event Action<string> OnSaveError;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a new model entry to Firebase.
    /// Returns immediately; result is delivered via OnSaveSuccess / OnSaveError.
    /// </summary>
    public void SaveModel(string objectName, string glbUrl, string thumbnailUrl = "")
    {
        if (!IsConfigured())
        {
            string msg = "[DatabaseManager] databaseUrl not set. Check Inspector.";
            Debug.LogError(msg);
            OnSaveError?.Invoke(msg);
            return;
        }

        StartCoroutine(PostModel(objectName, glbUrl, thumbnailUrl));
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    private bool IsConfigured()
    {
        return !string.IsNullOrEmpty(databaseUrl) &&
               databaseUrl.StartsWith("https://") &&
               !databaseUrl.Contains("YOUR-PROJECT");
    }

    private IEnumerator PostModel(string objectName, string glbUrl, string thumbnailUrl)
    {
        // Build payload — Firebase auto-generates a key when we POST (not PUT)
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string json = BuildJson(objectName, glbUrl, thumbnailUrl, unixMs);

        // Endpoint: POST /models.json
        string endpoint = $"{databaseUrl.TrimEnd('/')}/{collectionPath}.json";
        if (!string.IsNullOrEmpty(databaseSecret))
            endpoint += $"?auth={databaseSecret}";

        Debug.Log($"[DatabaseManager] POST → {endpoint}");
        Debug.Log($"[DatabaseManager] Payload: {json}");

        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        using var req = new UnityWebRequest(endpoint, "POST");
        req.uploadHandler   = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 30;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            string err = $"Firebase POST failed: {req.error} (HTTP {req.responseCode})";
            Debug.LogError($"[DatabaseManager] {err}");
            OnSaveError?.Invoke(err);
            yield break;
        }

        // Firebase returns {"name":"-NxKEY..."} on success
        string responseBody = req.downloadHandler.text;
        Debug.Log($"[DatabaseManager] Firebase response: {responseBody}");

        string key = ParseFirebaseKey(responseBody);
        Debug.Log($"[DatabaseManager] Model saved — key: {key}");
        OnSaveSuccess?.Invoke(key);
    }

    /// <summary>
    /// Hand-rolled JSON builder — avoids JsonUtility quirks and keeps the
    /// file dependency-free.
    /// </summary>
    private static string BuildJson(string name, string glbUrl, string thumbnailUrl, long timestampMs)
    {
        // Escape quotes in strings (basic — object names shouldn't contain quotes)
        name         = name.Replace("\"", "\\\"");
        glbUrl       = glbUrl.Replace("\"", "\\\"");
        thumbnailUrl = thumbnailUrl.Replace("\"", "\\\"");

        return "{"
             + $"\"name\":\"{name}\","
             + $"\"glb_url\":\"{glbUrl}\","
             + $"\"thumbnail_url\":\"{thumbnailUrl}\","
             + $"\"timestamp_ms\":{timestampMs},"
             + "\"status\":\"ready\""
             + "}";
    }

    /// <summary>Extracts the Firebase push key from {"name":"-NxABC"}</summary>
    private static string ParseFirebaseKey(string json)
    {
        // {"name":"-NxABC123xyz"} — quick substring parse, no JSON library needed
        const string marker = "\"name\":\"";
        int start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return json; // fallback: return raw body

        start += marker.Length;
        int end = json.IndexOf('"', start);
        return end > start ? json.Substring(start, end - start) : json;
    }

    // ── Helpers for external scripts ──────────────────────────────────────────

    public bool IsDatabaseConfigured() => IsConfigured();

    /// <summary>Update the database URL at runtime (e.g. loaded from a config file).</summary>
    public void Configure(string url, string secret = "")
    {
        databaseUrl    = url;
        databaseSecret = secret;
    }
}
