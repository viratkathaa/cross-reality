using System.Collections.Generic;
using UnityEngine;
using Meta.XR.BuildingBlocks.AIBlocks;

/// <summary>
/// Positions semi-transparent trigger volumes over every laptop / monitor / TV
/// detected by ObjectDetectionAgent. Up to MAX_ZONES screens can be tracked at once.
///
/// All visuals are created automatically at runtime — no manual Inspector wiring needed.
/// When a spawned SAM3DModel (tagged "SAM3DModel") enters any zone, the GLB URL is
/// saved to Firebase so the laptop receiver can open it in Blender.
///
/// Key improvements over v1:
///  • Multi-zone pool — one glowing box per detected screen simultaneously
///  • Auto-generated cube visuals (no zoneRenderer Inspector assignment)
///  • maxRaycastDepth prevents the ray from overshooting the screen and hitting a far wall
///  • "keyboard" added to device labels (consistently detected near laptops)
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class TransferZone : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("Auto-found if not assigned.")]
    [SerializeField] private DatabaseManager databaseManager;

    [Tooltip("Auto-found if not assigned.")]
    [SerializeField] private ObjectDetectionAgent objectDetectionAgent;

    [Header("Target Device Labels")]
    [Tooltip("Object detection labels that trigger zone placement (case-insensitive).")]
    [SerializeField] private string[] deviceLabels =
        { "laptop", "monitor", "tv", "television", "screen", "computer", "display", "keyboard" };

    [Header("Zone Appearance")]
    [SerializeField] private Color idleColor    = new Color(0.0f, 0.8f, 1.0f, 0.40f);
    [SerializeField] private Color activeColor  = new Color(0.0f, 1.0f, 0.4f, 0.65f);
    [SerializeField] private Color successColor = new Color(1.0f, 1.0f, 0.0f, 0.65f);
    [SerializeField] private Color errorColor   = new Color(1.0f, 0.2f, 0.2f, 0.55f);

    [Header("Positioning")]
    [Tooltip("Raycast stops at this distance. Prevents hitting far walls behind the screen. " +
             "Set to roughly 1.5× the farthest your screen ever sits from you.")]
    [SerializeField] private float maxRaycastDepth = 3.0f;

    [Tooltip("Zone depth along the ray when no surface is hit within maxRaycastDepth.")]
    [SerializeField] private float fallbackDepth = 1.8f;

    [Tooltip("Depth (Z thickness) of each zone box in metres.")]
    [SerializeField] private float zoneDepth = 0.12f;

    [Header("Timing")]
    [Tooltip("Zone hides if its screen is not detected for this many seconds.")]
    [SerializeField] private float deviceTimeoutSeconds = 2.5f;
    [SerializeField] private float flashDuration = 2f;

    // ── State ──────────────────────────────────────────────────────────────────
    private string _pendingGlbUrl;
    private string _pendingModelName;
    private bool   _isTransferring;

    private HashSet<string> _deviceSet;
    private BoxCollider     _unusedCollider; // RequireComponent one — disabled

    // ── Zone slot pool ─────────────────────────────────────────────────────────
    private const int MAX_ZONES = 5;

    private class ZoneSlot
    {
        public GameObject  go;
        public MeshRenderer rend;
        public Material    mat;
        public float       lastSeenAt = -100f;
        public float       flashEndAt = 0f;
        public bool        inUse;
    }

    private readonly List<ZoneSlot> _slots = new List<ZoneSlot>();

    // ── Unity lifecycle ────────────────────────────────────────────────────────
    private void Awake()
    {
        _unusedCollider         = GetComponent<BoxCollider>();
        _unusedCollider.enabled = false; // each slot has its own collider

        _deviceSet = new HashSet<string>(deviceLabels, System.StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < MAX_ZONES; i++)
            _slots.Add(BuildSlot(i));
    }

    private void Start()
    {
        if (databaseManager == null)
            databaseManager = FindObjectOfType<DatabaseManager>();
        if (databaseManager == null)
            Debug.LogError("[TransferZone] DatabaseManager not found!");

        if (objectDetectionAgent == null)
            objectDetectionAgent = FindAnyObjectByType<ObjectDetectionAgent>();

        if (objectDetectionAgent != null)
            objectDetectionAgent.OnDetectionResponseReceived.AddListener(OnObjectsDetected);
        else
            Debug.LogWarning("[TransferZone] ObjectDetectionAgent not found — zones will not auto-position.");

        if (databaseManager != null)
        {
            databaseManager.OnSaveSuccess += HandleSaveSuccess;
            databaseManager.OnSaveError   += HandleSaveError;
        }

        Debug.Log($"[TransferZone] Ready — watching for: {string.Join(", ", deviceLabels)}");
    }

    private void OnDestroy()
    {
        if (objectDetectionAgent != null)
            objectDetectionAgent.OnDetectionResponseReceived.RemoveListener(OnObjectsDetected);
        if (databaseManager != null)
        {
            databaseManager.OnSaveSuccess -= HandleSaveSuccess;
            databaseManager.OnSaveError   -= HandleSaveError;
        }
    }

    private void Update()
    {
        float now = Time.time;
        foreach (var slot in _slots)
        {
            if (!slot.inUse) continue;

            bool timeout = (now - slot.lastSeenAt) >= deviceTimeoutSeconds;
            if (timeout)
            {
                slot.go.SetActive(false);
                slot.inUse = false;
                continue;
            }

            // Reset to idle colour after a flash
            if (now > slot.flashEndAt && !_isTransferring)
                SetSlotColor(slot, idleColor);
        }
    }

    // ── Slot pool creation ─────────────────────────────────────────────────────
    private ZoneSlot BuildSlot(int index)
    {
        // Cube primitive — visible from all angles, no back-face culling
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"TransferZone_Slot{index}";
        go.transform.SetParent(transform);

        // Re-use the default BoxCollider as a trigger
        var col = go.GetComponent<BoxCollider>();
        col.isTrigger = true;

        // Transparent URP/Lit material
        var mat  = MakeTransparentMaterial(idleColor);
        var rend = go.GetComponent<MeshRenderer>();
        rend.material             = mat;
        rend.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows       = false;

        // Trigger receiver forwards OnTriggerEnter back to this manager
        go.AddComponent<ZoneTriggerReceiver>().Initialize(this);

        go.SetActive(false);

        return new ZoneSlot { go = go, rend = rend, mat = mat };
    }

    private static Material MakeTransparentMaterial(Color color)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        // URP transparency keywords + blend settings
        mat.SetFloat("_Surface",  1f); // Transparent
        mat.SetFloat("_Blend",    0f); // Alpha blend
        mat.SetFloat("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite",   0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = 3000; // Transparent queue
        mat.SetColor("_BaseColor", color);
        return mat;
    }

    // ── Detection callback ─────────────────────────────────────────────────────
    private void OnObjectsDetected(List<BoxData> boxes)
    {
        if (boxes == null || boxes.Count == 0) return;

        // Collect ALL device detections this frame (not just best one)
        var hits = new List<(float cx, float cy, float w, float h)>();
        foreach (var box in boxes)
        {
            if (_deviceSet.Contains(StripScore(box.label)))
                hits.Add((box.position.x, box.position.y, box.scale.x, box.scale.y));
        }

        if (hits.Count == 0) return;

        // Assign each detection to a slot (up to MAX_ZONES)
        int count = Mathf.Min(hits.Count, MAX_ZONES);
        for (int i = 0; i < count; i++)
        {
            var slot = _slots[i];
            slot.inUse      = true;
            slot.lastSeenAt = Time.time;
            slot.go.SetActive(true);
            PositionSlot(slot, hits[i].cx, hits[i].cy, hits[i].w, hits[i].h);
        }
    }

    private void PositionSlot(ZoneSlot slot, float cx, float cy, float w, float h)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // Detection uses top-left origin; Unity viewport uses bottom-left → flip Y
        Ray ray = cam.ViewportPointToRay(new Vector3(cx, 1f - cy, 0f));

        Vector3    pos;
        Quaternion rot;

        // Limit depth to maxRaycastDepth — avoids hitting walls behind the screen
        if (Physics.Raycast(ray, out RaycastHit hit, maxRaycastDepth))
        {
            pos = hit.point + hit.normal * 0.06f;
            rot = Quaternion.LookRotation(-hit.normal, Vector3.up);
            Debug.Log($"[TransferZone] Slot anchored to surface at {hit.point} (dist {hit.distance:F2}m)");
        }
        else
        {
            pos = ray.GetPoint(fallbackDepth);
            rot = Quaternion.LookRotation(ray.direction, Vector3.up);
        }

        slot.go.transform.SetPositionAndRotation(pos, rot);

        // Scale to match detected bounding box footprint in world space
        float estW = Mathf.Clamp(w * 2f, 0.25f, 1.5f);
        float estH = Mathf.Clamp(h * 2f, 0.20f, 1.0f);
        slot.go.transform.localScale = new Vector3(estW, estH, zoneDepth);
    }

    // ── Public API ─────────────────────────────────────────────────────────────
    public void SetPendingModel(string glbUrl, string modelName)
    {
        _pendingGlbUrl    = glbUrl;
        _pendingModelName = modelName;
        Debug.Log($"[TransferZone] Armed — '{modelName}'. Throw model into a glowing blue zone.");
    }

    public void ClearPendingModel()
    {
        _pendingGlbUrl    = null;
        _pendingModelName = null;
    }

    public bool HasPendingModel() => !string.IsNullOrEmpty(_pendingGlbUrl);

    // ── Trigger (forwarded from ZoneTriggerReceiver) ───────────────────────────
    public void OnZoneTriggered(Collider other)
    {
        if (!other.CompareTag("SAM3DModel")) return;

        if (_isTransferring)
        {
            Debug.LogWarning("[TransferZone] Transfer already in progress.");
            return;
        }

        if (string.IsNullOrEmpty(_pendingGlbUrl))
        {
            Debug.LogWarning("[TransferZone] No model pending. Generate one first (press A).");
            FlashAll(errorColor);
            return;
        }

        if (databaseManager == null || !databaseManager.IsDatabaseConfigured())
        {
            Debug.LogError("[TransferZone] DatabaseManager not configured.");
            FlashAll(errorColor);
            return;
        }

        Debug.Log($"[TransferZone] '{_pendingModelName}' entered zone — uploading to Firebase…");
        _isTransferring = true;
        SetAllColor(activeColor);
        databaseManager.SaveModel(_pendingModelName, _pendingGlbUrl);
    }

    // ── Database callbacks ─────────────────────────────────────────────────────
    private void HandleSaveSuccess(string key)
    {
        _isTransferring = false;
        Debug.Log($"[TransferZone] Upload OK (key: {key}). Blender opening on laptop.");
        FlashAll(successColor);
        ClearPendingModel();
    }

    private void HandleSaveError(string error)
    {
        _isTransferring = false;
        Debug.LogError($"[TransferZone] Firebase upload failed: {error}");
        FlashAll(errorColor);
    }

    // ── Visual helpers ─────────────────────────────────────────────────────────
    private void SetSlotColor(ZoneSlot slot, Color color)
    {
        if (slot.mat.HasProperty("_BaseColor"))
            slot.mat.SetColor("_BaseColor", color);
    }

    private void SetAllColor(Color color)
    {
        foreach (var s in _slots)
            if (s.inUse) SetSlotColor(s, color);
    }

    private void FlashAll(Color color)
    {
        float end = Time.time + flashDuration;
        foreach (var s in _slots)
        {
            s.go.SetActive(true);
            SetSlotColor(s, color);
            s.flashEndAt  = end;
            s.lastSeenAt  = Time.time;
        }
    }

    // ── String helpers ─────────────────────────────────────────────────────────
    private static string StripScore(string label)
    {
        int i = label.LastIndexOf(' ');
        return i > 0 ? label.Substring(0, i).Trim() : label.Trim();
    }
}
