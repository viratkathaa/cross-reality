using System.Collections.Generic;
using UnityEngine;
using Meta.XR.BuildingBlocks.AIBlocks;
using FalAi.Models;

/// <summary>
/// Main orchestrator for the SAM-3D capture workflow.
/// Coordinates camera capture -> FAL AI API -> GLB loading -> hand interaction.
/// Uses OVR controller input (A button or right trigger) to capture.
/// Auto-detects the target object via Meta ObjectDetectionAgent when available.
/// </summary>
public class SAM3DCaptureManager : MonoBehaviour
{
    [Header("Component References")]
    [SerializeField] private CameraCapture cameraCapture;
    [SerializeField] private FalAiClient falAiClient;
    [SerializeField] private GLBModelLoader glbModelLoader;
    [SerializeField] private CaptureUI captureUI;

    [Header("Object Detection")]
    [Tooltip("Meta on-device object detection building block. Auto-found if not assigned.")]
    [SerializeField] private ObjectDetectionAgent objectDetectionAgent;

    [Header("Spawn Settings")]
    [SerializeField] private float spawnDistance = 1.5f;
    [SerializeField] private string capturePrompt = "object"; // fallback only — overridden by auto-detection

    [Header("API Settings")]
    [SerializeField] private string falAiApiKey = "YOUR_FAL_AI_API_KEY";

    [Header("Input Settings")]
    [Tooltip("Which OVR button triggers capture")]
    [SerializeField] private OVRInput.Button captureButton = OVRInput.Button.One; // A button

    [Tooltip("Press this button to upload the last generated model to Firebase / Blender.\n" +
             "Default: B button (Button.Two) on right controller.")]
    [SerializeField] private OVRInput.Button transferButton = OVRInput.Button.Two; // B button

    [Header("Cross-Device Transfer")]
    [Tooltip("The virtual laptop-screen zone. Auto-found if not assigned.")]
    [SerializeField] private TransferZone transferZone;

    [Tooltip("DatabaseManager for Firebase uploads. Auto-found if not assigned.")]
    [SerializeField] private DatabaseManager databaseManager;

    [Tooltip("When ON: uploads the GLB to Firebase immediately after the model loads (no collision required).\n" +
             "When OFF: use the B button or TransferZone collision to trigger upload.")]
    [SerializeField] private bool autoUploadOnLoad = false;

    private bool isProcessing = false;
    private Transform headTransform;
    private float lastCaptureTime = -10f;
    private const float CAPTURE_COOLDOWN = 2f; // Prevent double triggers

    // Labels that are transfer targets, not capture subjects — never use as SAM-3D prompt
    private static readonly HashSet<string> s_deviceLabels = new HashSet<string>(
        System.StringComparer.OrdinalIgnoreCase)
    {
        "laptop", "monitor", "tv", "television", "screen", "computer", "display"
    };

    // Auto-detection state — filled by Meta ObjectDetectionAgent every frame
    private string _autoDetectedLabel = null;
    private float _errorShownAt = -10f;
    private const float ERROR_HOLD_SECONDS = 4f; // keep error text visible before detection overwrites it

    // Last captured passthrough frame — passed to GLBModelLoader as texture fallback
    private Texture2D _lastCapturedImage;

    // Cross-device transfer — stored when FAL AI returns the GLB URL
    private string _lastGlbUrl      = null;
    private string _lastObjectName  = null;

    private void Start()
    {
        // Find components on same object if not assigned
        if (cameraCapture == null)
            cameraCapture = GetComponent<CameraCapture>();
        if (falAiClient == null)
            falAiClient = GetComponent<FalAiClient>();
        if (glbModelLoader == null)
            glbModelLoader = GetComponent<GLBModelLoader>();
        if (captureUI == null)
            captureUI = FindObjectOfType<CaptureUI>();
        if (transferZone == null)
            transferZone = FindObjectOfType<TransferZone>();
        if (databaseManager == null)
            databaseManager = FindObjectOfType<DatabaseManager>();

        // Find headset transform from OVRCameraRig
        OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            headTransform = cameraRig.centerEyeAnchor;
            Debug.Log("[SAM3DCaptureManager] Found OVRCameraRig");
        }
        else
        {
            Debug.LogError("[SAM3DCaptureManager] OVRCameraRig not found in scene!");
        }

        // Setup event handlers
        if (falAiClient != null)
        {
            falAiClient.OnProgress += HandleProgress;
            falAiClient.OnError += HandleError;
            falAiClient.OnResponseReceived += HandleFalAiResponse;
        }

        if (glbModelLoader != null)
        {
            glbModelLoader.OnModelLoaded += HandleModelLoaded;
            glbModelLoader.OnLoadError += HandleLoadError;
        }

        // Set API key - check both this component and FalAiClient
        if (!string.IsNullOrEmpty(falAiApiKey) && !falAiApiKey.Contains("YOUR_FAL"))
        {
            falAiClient?.SetApiKey(falAiApiKey);
            Debug.Log("[SAM3DCaptureManager] API key set from Manager");
        }
        else if (falAiClient != null && falAiClient.IsApiKeySet())
        {
            Debug.Log("[SAM3DCaptureManager] API key already set on FalAiClient");
        }
        else
        {
            Debug.LogWarning("[SAM3DCaptureManager] FAL AI API key not set! Set it on either SAM3DCaptureManager or FalAiClient in Inspector.");
            if (captureUI != null)
                captureUI.SetStatusText("Set API key in Inspector");
        }

        // Hook into Meta on-device object detection building block
        if (objectDetectionAgent == null)
            objectDetectionAgent = FindAnyObjectByType<ObjectDetectionAgent>();

        if (objectDetectionAgent != null)
        {
            objectDetectionAgent.OnDetectionResponseReceived.AddListener(OnObjectsDetected);
            Debug.Log("[SAM3DCaptureManager] Connected to ObjectDetectionAgent — prompt will be set automatically");
        }
        else
        {
            Debug.LogWarning("[SAM3DCaptureManager] ObjectDetectionAgent not found — using Inspector capturePrompt as fallback");
        }

        Debug.Log("[SAM3DCaptureManager] Initialized successfully");
    }

    private void OnObjectsDetected(List<BoxData> boxes)
    {
        if (boxes == null || boxes.Count == 0) return;

        // Find highest-confidence non-device object in this frame
        string bestLabel = null;
        float  bestScore = -1f;

        foreach (var box in boxes)
        {
            string label = StripScore(box.label);
            float  score = ParseScore(box.label);

            // Never set a device (laptop/monitor/TV) as the SAM-3D capture prompt
            if (s_deviceLabels.Contains(label)) continue;

            if (score > bestScore) { bestScore = score; bestLabel = label; }
        }

        if (bestLabel == null) return;

        _autoDetectedLabel = bestLabel;

        // Live UI feedback while idle — but don't overwrite a fresh error message
        bool errorStillShowing = (Time.time - _errorShownAt) < ERROR_HOLD_SECONDS;
        if (!isProcessing && !errorStillShowing && captureUI != null)
            captureUI.SetStatusText($"Seeing: {_autoDetectedLabel} — press A to capture");
    }

    private static string StripScore(string label)
    {
        int i = label.LastIndexOf(' ');
        return i > 0 ? label.Substring(0, i).Trim() : label.Trim();
    }

    private static float ParseScore(string label)
    {
        int i = label.LastIndexOf(' ');
        if (i > 0 && float.TryParse(
                label.Substring(i + 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float s))
            return s;
        return 0f;
    }

    private void OnDestroy()
    {
        if (objectDetectionAgent != null)
            objectDetectionAgent.OnDetectionResponseReceived.RemoveListener(OnObjectsDetected);
    }

    private void Update()
    {
        // A button — capture & generate model
        if (!isProcessing && OVRInput.GetDown(captureButton))
            StartCapture();

        // B button — upload last generated model to Firebase → Blender
        if (OVRInput.GetDown(transferButton))
            TransferLastModel();
    }

    /// <summary>
    /// Uploads the last generated GLB URL to Firebase.
    /// Called by the B button. The laptop receiver will download and open it in Blender.
    /// </summary>
    public void TransferLastModel()
    {
        if (string.IsNullOrEmpty(_lastGlbUrl))
        {
            Debug.LogWarning("[SAM3DCaptureManager] No model to transfer — press A first to generate one.");
            if (captureUI != null)
                captureUI.ShowError("Generate a model first (press A)");
            return;
        }

        if (databaseManager == null || !databaseManager.IsDatabaseConfigured())
        {
            Debug.LogError("[SAM3DCaptureManager] DatabaseManager not configured.");
            if (captureUI != null)
                captureUI.ShowError("Database not configured");
            return;
        }

        Debug.Log($"[SAM3DCaptureManager] B pressed — transferring '{_lastObjectName}' to laptop…");
        if (captureUI != null)
            captureUI.SetStatusText($"Sending '{_lastObjectName}' to laptop…");

        databaseManager.SaveModel(_lastObjectName, _lastGlbUrl);
    }

    /// <summary>
    /// Main capture workflow. Can also be called from UI button.
    /// </summary>
    public void StartCapture()
    {
        // Guard against double triggers (UI button + controller at same time)
        // Set isProcessing FIRST before any async work to block re-entry
        if (isProcessing || Time.time - lastCaptureTime < CAPTURE_COOLDOWN)
        {
            Debug.LogWarning("[SAM3DCaptureManager] Already processing or cooldown active...");
            return;
        }
        isProcessing = true;       // claim the slot immediately
        lastCaptureTime = Time.time;

        if (falAiClient == null || !falAiClient.IsApiKeySet())
        {
            isProcessing = false;
            if (captureUI != null)
                captureUI.ShowError("API key not configured");
            return;
        }

        // Resolve prompt: prefer what the on-device model sees right now
        string prompt = !string.IsNullOrEmpty(_autoDetectedLabel) ? _autoDetectedLabel : capturePrompt;
        Debug.Log($"[SAM3DCaptureManager] Prompt: '{prompt}' " +
                  $"(source: {(!string.IsNullOrEmpty(_autoDetectedLabel) ? "auto-detected" : "Inspector fallback")})");

        if (captureUI != null)
        {
            captureUI.ShowLoading();
            captureUI.SetStatusText($"Capturing '{prompt}'...");
        }

        // Check if using test URL mode (editor) or real capture (device)
        if (cameraCapture != null && cameraCapture.IsUsingTestUrl())
        {
            // Test mode: send a known good image URL directly
            string testUrl = cameraCapture.GetTestImageUrl();
            Debug.Log($"[SAM3DCaptureManager] Using test image URL: {testUrl}");

            if (captureUI != null)
                captureUI.SetStatusText($"Sending test image — prompt: '{prompt}'...");

            falAiClient.SendImageUrlToFalAi(testUrl, prompt);
        }
        else
        {
            // Real capture mode (on device)
            if (cameraCapture == null)
            {
                HandleError("CameraCapture component not assigned");
                return;
            }

            Debug.Log("[SAM3DCaptureManager] Step 1/4: Capturing screenshot...");

            Texture2D capturedImage = cameraCapture.CaptureScreenshot();

            if (capturedImage == null)
            {
                HandleError("Failed to capture screenshot");
                return;
            }

            // Store for use as texture fallback on the generated model
            _lastCapturedImage = capturedImage;

            Debug.Log($"[SAM3DCaptureManager] Step 2/4: Sending to FAL AI with prompt '{prompt}'...");
            if (captureUI != null)
                captureUI.SetStatusText($"Sending to AI — object: '{prompt}'...");

            falAiClient.SendImageToFalAi(capturedImage, prompt);
        }
    }

    private void HandleFalAiResponse(FalAiResponse response)
    {
        if (response == null || response.model_glb == null || string.IsNullOrEmpty(response.model_glb.url))
        {
            HandleError("No GLB model in API response");
            return;
        }

        Debug.Log($"[SAM3DCaptureManager] Step 3/4: Loading 3D model from {response.model_glb.url}");
        if (captureUI != null)
            captureUI.SetStatusText("Loading 3D model...");

        // Store for cross-device transfer
        _lastGlbUrl     = response.model_glb.url;
        _lastObjectName = !string.IsNullOrEmpty(_autoDetectedLabel) ? _autoDetectedLabel : capturePrompt;

        // Clear previous model before spawning new one
        glbModelLoader.ClearCurrentModel();

        Vector3 spawnPos = GetSpawnPosition();
        glbModelLoader.LoadGLBFromUrl(response.model_glb.url, spawnPos, _lastCapturedImage);
    }

    private void HandleModelLoaded(GameObject model)
    {
        isProcessing = false;
        Debug.Log("[SAM3DCaptureManager] Step 4/4: Model ready!");
        if (captureUI != null)
            captureUI.ShowSuccess("Model loaded! Grab it!");

        // Tag the model so TransferZone can identify it on collision
        if (model != null)
            model.tag = "SAM3DModel";

        // ── Auto-upload mode (for testing without TransferZone colliders) ────────
        if (autoUploadOnLoad && !string.IsNullOrEmpty(_lastGlbUrl))
        {
            if (databaseManager != null && databaseManager.IsDatabaseConfigured())
            {
                Debug.Log($"[SAM3DCaptureManager] Auto-upload ON — pushing '{_lastObjectName}' to Firebase now.");
                databaseManager.SaveModel(_lastObjectName, _lastGlbUrl);
                if (captureUI != null)
                    captureUI.SetStatusText($"Uploading '{_lastObjectName}' to laptop…");
            }
            else
            {
                Debug.LogWarning("[SAM3DCaptureManager] autoUploadOnLoad = true but DatabaseManager not found/configured.");
            }
        }

        // ── TransferZone mode (physical throw) ────────────────────────────────
        if (transferZone != null && !string.IsNullOrEmpty(_lastGlbUrl))
        {
            transferZone.SetPendingModel(_lastGlbUrl, _lastObjectName);
            Debug.Log($"[SAM3DCaptureManager] TransferZone armed — '{_lastObjectName}'");
        }
    }

    private void HandleError(string errorMessage)
    {
        isProcessing = false;
        _errorShownAt = Time.time; // prevent detection text from overwriting this for ERROR_HOLD_SECONDS
        Debug.LogError($"[SAM3DCaptureManager] Error: {errorMessage}");
        if (captureUI != null)
            captureUI.ShowError(errorMessage);
    }

    private void HandleLoadError(string errorMessage)
    {
        HandleError($"Load failed: {errorMessage}");
    }

    private void HandleProgress(float progress)
    {
        if (captureUI != null)
            captureUI.SetProgress(progress);
    }

    /// <summary>
    /// Spawn position: in front of the headset, snapped to the nearest surface below.
    /// Raycasts downward from the forward point; falls back to the raw forward position
    /// if no EffectMesh collider is hit (e.g. room not yet loaded).
    /// </summary>
    private Vector3 GetSpawnPosition()
    {
        if (headTransform == null)
            return Vector3.forward * spawnDistance;

        // Horizontal forward point at head height
        Vector3 forward = headTransform.position + headTransform.forward * spawnDistance;

        // Cast down from 1 m above the forward point to find the room surface
        Vector3 rayOrigin = forward + Vector3.up * 1f;
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 4f))
        {
            // Place object just above the surface so it rests on it
            return hit.point + Vector3.up * 0.05f;
        }

        // Fallback: no surface found — spawn at forward position (gravity will take it)
        return forward;
    }

    public void ClearModel()
    {
        glbModelLoader?.ClearCurrentModel();
        if (captureUI != null)
            captureUI.ShowSuccess("Ready to capture");
    }

    public void SetApiKey(string key)
    {
        falAiApiKey = key;
        falAiClient?.SetApiKey(key);
    }

    public void SetCapturePrompt(string prompt)
    {
        capturePrompt = prompt;
    }
}
