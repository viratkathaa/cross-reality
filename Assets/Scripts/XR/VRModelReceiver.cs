using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Polls Firebase /desktop_to_vr every <pollInterval> seconds.
/// When a new entry appears (written by upload_to_vr.py or the Blender addon),
/// downloads the GLB and spawns it in MR alongside any already-loaded models.
///
/// Scene setup
/// ───────────
///  • Add this component to any persistent GameObject (e.g. SAM3DManager).
///  • Assign a SEPARATE GLBModelLoader instance so desktop models are independent
///    from capture-flow models (they won't get cleared when A is pressed).
///  • CaptureUI is auto-found for notifications.
/// </summary>
public class VRModelReceiver : MonoBehaviour
{
    [Header("Firebase Config")]
    [SerializeField] private string databaseUrl    = "https://crossreality-ec12f-default-rtdb.asia-southeast1.firebasedatabase.app";
    [SerializeField] private string databaseSecret = "YOUR_FIREBASE_SECRET_HERE";
    [SerializeField] private string collectionPath = "desktop_to_vr";

    [Header("References")]
    [Tooltip("Assign a SEPARATE GLBModelLoader (not the one used by SAM3DCaptureManager) " +
             "so desktop models are never cleared by the capture flow. " +
             "Leave blank to auto-create one at runtime.")]
    [SerializeField] private GLBModelLoader glbModelLoader;

    [Tooltip("Auto-found if not assigned.")]
    [SerializeField] private CaptureUI captureUI;

    [Header("Spawn Settings")]
    [Tooltip("How far in front of the user desktop models spawn.")]
    [SerializeField] private float spawnDistance = 1.0f;

    [Tooltip("Horizontal gap between successive desktop models.")]
    [SerializeField] private float spawnSpacing = 0.4f;

    [Tooltip("How many seconds between Firebase polls.")]
    [SerializeField] private float pollInterval = 3f;

    // ── State ──────────────────────────────────────────────────────────────────
    private readonly HashSet<string> _seenKeys = new HashSet<string>();
    private const string PREFS_KEY = "VRModelReceiver_SeenKeys";

    private Transform _headTransform;
    private int       _spawnIndex = 0; // increments for each desktop model to spread them out

    // ── Unity lifecycle ────────────────────────────────────────────────────────
    private void Start()
    {
        LoadSeenKeys();

        // Find head transform
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null) _headTransform = rig.centerEyeAnchor;

        // Auto-find CaptureUI
        if (captureUI == null)
            captureUI = FindObjectOfType<CaptureUI>();

        // Create a dedicated GLBModelLoader so desktop models are independent
        if (glbModelLoader == null)
        {
            var loaderGO = new GameObject("VRReceiver_Loader");
            DontDestroyOnLoad(loaderGO);
            glbModelLoader = loaderGO.AddComponent<GLBModelLoader>();
            Debug.Log("[VRModelReceiver] Created dedicated GLBModelLoader for desktop models.");
        }

        StartCoroutine(PollLoop());
        Debug.Log($"[VRModelReceiver] Polling '{collectionPath}' every {pollInterval}s…");
    }

    // ── Poll loop ──────────────────────────────────────────────────────────────
    private IEnumerator PollLoop()
    {
        while (true)
        {
            yield return StartCoroutine(FetchAndProcess());
            yield return new WaitForSeconds(pollInterval);
        }
    }

    private IEnumerator FetchAndProcess()
    {
        string url = $"{databaseUrl.TrimEnd('/')}/{collectionPath}.json";
        if (!string.IsNullOrEmpty(databaseSecret))
            url += $"?auth={databaseSecret}";

        using var req = UnityWebRequest.Get(url);
        req.timeout = 10;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[VRModelReceiver] Poll failed: {req.error}");
            yield break;
        }

        string json = req.downloadHandler.text;
        if (string.IsNullOrEmpty(json) || json == "null") yield break;

        var entries = ParseCollection(json);
        foreach (var e in entries)
        {
            if (_seenKeys.Contains(e.key)) continue;
            if (e.status != "ready")        continue;

            Debug.Log($"[VRModelReceiver] New model from desktop: '{e.name}' ({e.key})");
            _seenKeys.Add(e.key);
            SaveSeenKeys();

            // Notify user immediately
            if (captureUI != null)
                captureUI.SetStatusText($"↓  Receiving '{e.name}' from desktop…");

            StartCoroutine(SpawnModel(e.name, e.glbUrl));
        }
    }

    // ── Spawn ──────────────────────────────────────────────────────────────────
    private IEnumerator SpawnModel(string name, string glbUrl)
    {
        bool done  = false;
        bool fault = false;
        string faultMsg = "";

        void OnLoaded(GameObject go)
        {
            done = true;
            if (go != null) go.tag = "SAM3DModel";
        }

        void OnError(string msg) { fault = true; faultMsg = msg; done = true; }

        glbModelLoader.OnModelLoaded += OnLoaded;
        glbModelLoader.OnLoadError   += OnError;

        glbModelLoader.LoadGLBFromUrl(glbUrl, NextSpawnPosition());

        yield return new WaitUntil(() => done);

        glbModelLoader.OnModelLoaded -= OnLoaded;
        glbModelLoader.OnLoadError   -= OnError;

        if (!fault)
        {
            _spawnIndex++;
            if (captureUI != null)
                captureUI.ShowSuccess($"'{name}' received from desktop!");
        }
        else
        {
            Debug.LogError($"[VRModelReceiver] Failed to load '{name}': {faultMsg}");
            if (captureUI != null)
                captureUI.ShowError($"Could not load '{name}'");
        }
    }

    /// <summary>
    /// Spreads desktop models to the right of centre so they don't overlap
    /// with the capture-flow model spawned straight ahead.
    /// </summary>
    private Vector3 NextSpawnPosition()
    {
        if (_headTransform == null)
            return new Vector3(_spawnIndex * spawnSpacing, 1f, spawnDistance);

        Vector3 forward = _headTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();

        Vector3 right   = _headTransform.right;

        // Centre-right fan: index 0 is straight ahead, 1 is one step right, etc.
        Vector3 target = _headTransform.position
                       + forward * spawnDistance
                       + right * (_spawnIndex * spawnSpacing);

        // Snap to floor surface if one is within reach
        Vector3 rayOrigin = target + Vector3.up * 1f;
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 4f))
            return hit.point + Vector3.up * 0.05f;

        return target;
    }

    // ── Minimal Firebase JSON parser ───────────────────────────────────────────
    // Handles: { "-KEY": { "name":"...", "glb_url":"...", "status":"..." }, ... }
    // No external JSON library required.

    private struct Entry
    {
        public string key, name, glbUrl, status;
    }

    private static List<Entry> ParseCollection(string json)
    {
        var list = new List<Entry>();
        int pos  = 0;

        while (pos < json.Length)
        {
            // Find next quoted key
            int ks = json.IndexOf('"', pos);
            if (ks < 0) break;
            int ke = json.IndexOf('"', ks + 1);
            if (ke < 0) break;
            string key = json.Substring(ks + 1, ke - ks - 1);
            pos = ke + 1;

            // Skip whitespace and colon
            while (pos < json.Length && json[pos] != '{') pos++;
            if (pos >= json.Length) break;

            // Find matching closing brace (handles simple 1-level nesting)
            int depth = 0, objStart = pos, objEnd = pos;
            for (int i = pos; i < json.Length; i++)
            {
                if      (json[i] == '{') depth++;
                else if (json[i] == '}') { if (--depth == 0) { objEnd = i; break; } }
            }

            string body = json.Substring(objStart, objEnd - objStart + 1);
            pos = objEnd + 1;

            // Only process Firebase push-ID keys (start with "-")
            if (!key.StartsWith("-")) continue;

            string name   = JsonField(body, "name");
            string glbUrl = JsonField(body, "glb_url");
            string status = JsonField(body, "status");

            if (!string.IsNullOrEmpty(glbUrl))
                list.Add(new Entry { key = key, name = name, glbUrl = glbUrl, status = status });
        }

        return list;
    }

    private static string JsonField(string json, string field)
    {
        string needle = $"\"{field}\"";
        int idx = json.IndexOf(needle);
        if (idx < 0) return "";

        idx += needle.Length;
        while (idx < json.Length && json[idx] != '"') idx++; // skip : and whitespace
        if (idx >= json.Length) return "";

        idx++; // skip opening "
        int end = idx;
        while (end < json.Length && json[end] != '"') end++;
        return json.Substring(idx, end - idx);
    }

    // ── PlayerPrefs persistence ────────────────────────────────────────────────
    private void LoadSeenKeys()
    {
        string stored = PlayerPrefs.GetString(PREFS_KEY, "");
        if (string.IsNullOrEmpty(stored)) return;
        foreach (var k in stored.Split(','))
            if (!string.IsNullOrEmpty(k)) _seenKeys.Add(k);
        Debug.Log($"[VRModelReceiver] {_seenKeys.Count} previously seen key(s) loaded.");
    }

    private void SaveSeenKeys()
    {
        PlayerPrefs.SetString(PREFS_KEY, string.Join(",", _seenKeys));
        PlayerPrefs.Save();
    }
}
