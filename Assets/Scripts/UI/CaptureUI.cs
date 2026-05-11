using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Spatial MR UI panel — floats in world space rather than following the headset.
///
/// Behaviour
/// ─────────
///  • On start the panel is placed 1.2 m ahead, slightly below eye level, facing the user.
///  • It stays fixed in the world while you look around (not glued to your face).
///  • If it drifts out of view for more than <repositionDelay> seconds it smoothly
///    slides back into comfortable viewing position.
///  • Canvas is forced to World Space in code — no need to change the scene prefab manually.
///
/// Scene setup
/// ───────────
///  • The Canvas + CaptureUI component can remain as-is in the scene.
///    This script overrides the render mode at runtime automatically.
///  • The canvas should have a background Image (dark, semi-transparent) as a child,
///    with StatusText (TextMeshProUGUI) on top. A Capture Button is optional.
/// </summary>
public class CaptureUI : MonoBehaviour
{
    [Header("UI Elements")]
    [SerializeField] private Button             captureButton;
    [SerializeField] private Image              loadingSpinner;
    [SerializeField] private TextMeshProUGUI    statusText;
    [SerializeField] private CanvasGroup        loadingCanvasGroup;
    [SerializeField] private Image              backgroundPanel;

    [Header("Spatial Panel Settings")]
    [Tooltip("Distance in front of the user's head where the panel spawns.")]
    [SerializeField] private float spawnDistance     = 1.2f;

    [Tooltip("How far below eye level the panel sits (positive = lower).")]
    [SerializeField] private float verticalOffset    = 0.25f;

    [Tooltip("Physical width of the panel in metres.")]
    [SerializeField] private float panelWidth        = 0.40f;

    [Tooltip("Physical height of the panel in metres.")]
    [SerializeField] private float panelHeight       = 0.14f;

    [Tooltip("After this many seconds out of view the panel slides back in front of you.")]
    [SerializeField] private float repositionDelay   = 2.5f;

    [Tooltip("How fast the panel moves back into view (lerp speed).")]
    [SerializeField] private float repositionSpeed   = 2.0f;

    [Header("Appearance")]
    [SerializeField] private Color normalBg  = new Color(0.05f, 0.05f, 0.10f, 0.82f);
    [SerializeField] private Color errorBg   = new Color(0.35f, 0.05f, 0.05f, 0.85f);
    [SerializeField] private Color successBg = new Color(0.04f, 0.25f, 0.10f, 0.85f);

    [Header("Spinner")]
    [SerializeField] private float spinnerRotationSpeed = 360f;

    // ── State ──────────────────────────────────────────────────────────────────
    private bool    isLoading;
    private Canvas  canvas;
    private Camera  headCam;
    private float   outOfViewTimer;
    private bool    isRepositioning;

    // ── Unity lifecycle ────────────────────────────────────────────────────────
    private void Start()
    {
        headCam = Camera.main;

        SetupWorldSpaceCanvas();
        PlacePanelInFrontOfUser(instant: true);

        if (captureButton != null)
            captureButton.onClick.AddListener(OnCaptureButtonClicked);

        if (loadingSpinner != null)
            HideLoading();

        SetStatusText("Ready — press A to capture");
        ApplyBgColor(normalBg);
    }

    private void Update()
    {
        AnimateSpinner();
        LazyFollow();
    }

    // ── World-space canvas setup ───────────────────────────────────────────────
    private void SetupWorldSpaceCanvas()
    {
        canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            canvas = GetComponent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("[CaptureUI] No Canvas found on this GameObject or its parent.");
            return;
        }

        // OVROverlayCanvas manages RenderMode itself — only fall back to manual
        // WorldSpace setup if the overlay component is not present.
        // Use reflection so this compiles even when the OVR type is unavailable.
        var overlayType = System.Type.GetType("OVROverlayCanvas") ??
                          System.Type.GetType("OVROverlayCanvas, Oculus.VR");
        bool hasOverlay = overlayType != null &&
                          (canvas.GetComponent(overlayType) != null ||
                           canvas.GetComponentInParent(overlayType) != null);

        if (!hasOverlay)
        {
            canvas.renderMode  = RenderMode.WorldSpace;
            canvas.worldCamera = headCam;

            var rt = canvas.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.sizeDelta  = new Vector2(panelWidth / 0.001f, panelHeight / 0.001f);
                rt.localScale = Vector3.one * 0.001f;
            }
        }

        // Find background panel
        if (backgroundPanel == null)
        {
            var bg = canvas.GetComponentInChildren<Image>();
            if (bg != null) backgroundPanel = bg;
        }

        ApplyBgColor(normalBg);
    }

    // ── Positioning ────────────────────────────────────────────────────────────
    private void PlacePanelInFrontOfUser(bool instant = false)
    {
        if (headCam == null) return;

        // Forward projected onto the horizontal plane so the panel doesn't tilt with head pitch
        Vector3 flatForward = headCam.transform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = Vector3.forward;
        flatForward.Normalize();

        Vector3 targetPos = headCam.transform.position
                          + flatForward * spawnDistance
                          - Vector3.up * verticalOffset;

        // Face the user (billboard around Y axis only for stability)
        Quaternion targetRot = Quaternion.LookRotation(targetPos - headCam.transform.position);

        if (instant || canvas == null)
        {
            canvas.transform.position = targetPos;
            canvas.transform.rotation = targetRot;
        }
        else
        {
            canvas.transform.position = Vector3.Lerp(
                canvas.transform.position, targetPos, Time.deltaTime * repositionSpeed);
            canvas.transform.rotation = Quaternion.Slerp(
                canvas.transform.rotation, targetRot, Time.deltaTime * repositionSpeed);
        }
    }

    private void LazyFollow()
    {
        if (headCam == null || canvas == null) return;

        // Dot product: how directly is the user looking at the panel?
        Vector3 toPanel = (canvas.transform.position - headCam.transform.position).normalized;
        float   dot     = Vector3.Dot(headCam.transform.forward, toPanel);

        bool inView = dot > 0.2f; // roughly within ~78° of centre gaze

        if (!inView)
        {
            outOfViewTimer += Time.deltaTime;
            if (outOfViewTimer >= repositionDelay)
            {
                // Slide back into comfortable position
                PlacePanelInFrontOfUser(instant: false);
            }
        }
        else
        {
            outOfViewTimer  = 0f;
        }
    }

    // ── Public API (called by SAM3DCaptureManager) ────────────────────────────
    public void ShowLoading()
    {
        isLoading = true;

        if (loadingCanvasGroup != null)
        {
            loadingCanvasGroup.alpha        = 1f;
            loadingCanvasGroup.blocksRaycasts = false;
        }
        if (loadingSpinner != null)
            loadingSpinner.gameObject.SetActive(true);
        if (captureButton != null)
            captureButton.gameObject.SetActive(false);

        ApplyBgColor(normalBg);
    }

    public void HideLoading()
    {
        isLoading = false;

        if (loadingCanvasGroup != null)
        {
            loadingCanvasGroup.alpha        = 0.3f;
            loadingCanvasGroup.blocksRaycasts = false;
        }
        if (loadingSpinner != null)
            loadingSpinner.gameObject.SetActive(false);
        if (captureButton != null)
        {
            captureButton.gameObject.SetActive(true);
            captureButton.interactable = true;
        }
    }

    public void SetStatusText(string message)
    {
        if (statusText != null)
            statusText.text = message;
        Debug.Log($"[CaptureUI] Status: {message}");
    }

    public void ShowError(string errorMessage)
    {
        HideLoading();
        SetStatusText($"✕  {errorMessage}");
        ApplyBgColor(errorBg);
        // Auto-reset background after 4 seconds
        Invoke(nameof(ResetBg), 4f);
    }

    public void ShowSuccess(string message = "Ready to capture")
    {
        HideLoading();
        SetStatusText($"✓  {message}");
        ApplyBgColor(successBg);
        Invoke(nameof(ResetBg), 3f);
    }

    public void SetProgress(float progress)
    {
        int pct = Mathf.RoundToInt(progress * 100);
        SetStatusText($"Processing…  {pct}%");
    }

    // ── Internal helpers ───────────────────────────────────────────────────────
    private void OnCaptureButtonClicked()
    {
        if (captureButton != null) captureButton.interactable = false;
        ShowLoading();
        SetStatusText("Capturing…");
    }

    private void AnimateSpinner()
    {
        if (isLoading && loadingSpinner != null)
            loadingSpinner.rectTransform.Rotate(0f, 0f, -spinnerRotationSpeed * Time.deltaTime);
    }

    private void ApplyBgColor(Color color)
    {
        if (backgroundPanel != null)
            backgroundPanel.color = color;
    }

    private void ResetBg() => ApplyBgColor(normalBg);
}
