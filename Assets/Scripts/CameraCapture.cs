using System;
using System.IO;
using UnityEngine;
using Meta.XR;

/// <summary>
/// Captures frames from the Quest 3 passthrough camera.
///
/// Capture priority:
///   1. PassthroughCameraAccess.GetTexture()  — raw camera feed, needs HzOS v85+
///   2. ScreenCapture.CaptureScreenshotAsTexture() — rendered frame fallback, works on any firmware
///   3. Test image URL                         — editor / CI testing
///
/// On HzOS v84 the raw camera API is unavailable (XR_METAX1_passthrough_camera_data extension
/// not present). The screen-capture fallback kicks in automatically so the FAL AI pipeline
/// keeps working. Enable 'Save Debug Images' to verify the captured frame via ADB.
/// </summary>
public class CameraCapture : MonoBehaviour
{
    [Header("Test Mode")]
    [Tooltip("Use a fixed test image URL instead of live capture (editor / CI)")]
    [SerializeField] private bool useTestImageUrl = false;

    [Tooltip("Test image URL (only used when Use Test Image Url is checked)")]
    [SerializeField] private string testImageUrl = "https://images.pexels.com/photos/170811/pexels-photo-170811.jpeg?auto=compress&cs=tinysrgb&w=800";

    [Header("References")]
    [Tooltip("Drag the [BuildingBlock] Passthrough Camera Access GameObject here from Hierarchy")]
    [SerializeField] private GameObject passthroughCameraObject;

    [Header("Fallback (HzOS v84 / no PassthroughCameraAccess)")]
    [Tooltip("When PassthroughCameraAccess.IsPlaying is false, fall back to ScreenCapture instead of failing. " +
             "The screen-capture frame includes Unity objects but may not include the passthrough layer " +
             "depending on compositor configuration. Enable Save Debug Images to verify.")]
    [SerializeField] private bool screenCaptureFallback = true;

    [Header("Debug")]
    [Tooltip("Save each captured frame as debug_capture.png in persistentDataPath")]
    [SerializeField] private bool saveDebugImages = false;

    public event Action<Texture2D> OnScreenshotCaptured;
    public event Action<string> OnCaptureError;

    private PassthroughCameraAccess passthroughCamera;
    private Texture2D captureTexture;
    private string persistentPath;

    private void Start()
    {
        persistentPath = Application.persistentDataPath;

        if (useTestImageUrl)
        {
            Debug.Log("[CameraCapture] Test image URL mode enabled");
            return;
        }

        // Try to find PassthroughCameraAccess (may not be usable on HzOS v84)
        if (passthroughCameraObject != null)
            passthroughCamera = passthroughCameraObject.GetComponentInChildren<PassthroughCameraAccess>(true);

        if (passthroughCamera == null)
            passthroughCamera = FindObjectOfType<PassthroughCameraAccess>(true);

        if (passthroughCamera == null)
        {
            string fallbackNote = screenCaptureFallback ? " — screen-capture fallback is ON." : " — no fallback available!";
            Debug.LogWarning("[CameraCapture] PassthroughCameraAccess not found in scene." + fallbackNote);
        }
        else
        {
            Debug.Log($"[CameraCapture] Found PassthroughCameraAccess on '{passthroughCamera.gameObject.name}'");
        }
    }

    public bool IsUsingTestUrl() => useTestImageUrl;
    public string GetTestImageUrl() => testImageUrl;

    /// <summary>
    /// Captures the current view as a Texture2D.
    /// Tries PassthroughCameraAccess first; falls back to ScreenCapture on HzOS v84.
    /// </summary>
    public Texture2D CaptureScreenshot()
    {
        if (useTestImageUrl)
        {
            OnCaptureError?.Invoke("Use Test Image Url is on — call GetTestImageUrl() instead");
            return null;
        }

        // ── Path 1: raw passthrough camera (HzOS v85+) ────────────────────────
        bool passthroughReady = passthroughCamera != null && passthroughCamera.IsPlaying;
        if (passthroughReady)
        {
            return CaptureViaPassthrough();
        }

        // ── Path 2: screen capture fallback (any firmware) ────────────────────
        if (screenCaptureFallback)
        {
            if (passthroughCamera != null)
                Debug.LogWarning("[CameraCapture] PassthroughCameraAccess.IsPlaying = false (HzOS v84?). " +
                                 "Using ScreenCapture fallback.");
            return CaptureViaScreenCapture();
        }

        // ── No path available ─────────────────────────────────────────────────
        OnCaptureError?.Invoke(
            "Passthrough camera is not ready (HzOS < v85). " +
            "Enable 'Screen Capture Fallback' in CameraCapture Inspector, or update headset firmware.");
        return null;
    }

    // ── Capture implementations ────────────────────────────────────────────────

    private Texture2D CaptureViaPassthrough()
    {
        try
        {
            Texture sourceTexture = passthroughCamera.GetTexture();
            if (sourceTexture == null)
            {
                OnCaptureError?.Invoke("Passthrough camera returned null texture");
                return null;
            }

            int width  = sourceTexture.width;
            int height = sourceTexture.height;

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            // Passthrough textures are often upside-down — flip V during blit
            Graphics.Blit(sourceTexture, rt, new Vector2(1f, -1f), new Vector2(0f, 1f));

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            if (captureTexture != null) Destroy(captureTexture);
            captureTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            captureTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            captureTexture.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            if (saveDebugImages) SaveDebugImage(captureTexture, "passthrough");
            Debug.Log($"[CameraCapture] Passthrough capture OK: {width}x{height}");
            OnScreenshotCaptured?.Invoke(captureTexture);
            return captureTexture;
        }
        catch (Exception ex)
        {
            string msg = $"Passthrough capture failed: {ex.Message}";
            Debug.LogError($"[CameraCapture] {msg}");
            OnCaptureError?.Invoke(msg);
            return null;
        }
    }

    private Texture2D CaptureViaScreenCapture()
    {
        try
        {
            if (captureTexture != null) Destroy(captureTexture);

            // CaptureScreenshotAsTexture() captures Unity's rendered framebuffer.
            // On Quest 3 this includes all Unity objects; the passthrough layer from
            // the compositor may or may not be included depending on your OVRPassthroughLayer
            // configuration. Enable saveDebugImages to verify via ADB pull.
            captureTexture = ScreenCapture.CaptureScreenshotAsTexture();

            if (captureTexture == null)
            {
                OnCaptureError?.Invoke("ScreenCapture.CaptureScreenshotAsTexture() returned null");
                return null;
            }

            if (saveDebugImages) SaveDebugImage(captureTexture, "screencap");
            Debug.Log($"[CameraCapture] Screen capture OK: {captureTexture.width}x{captureTexture.height}");
            OnScreenshotCaptured?.Invoke(captureTexture);
            return captureTexture;
        }
        catch (Exception ex)
        {
            string msg = $"Screen capture failed: {ex.Message}";
            Debug.LogError($"[CameraCapture] {msg}");
            OnCaptureError?.Invoke(msg);
            return null;
        }
    }

    // ── Debug helpers ──────────────────────────────────────────────────────────

    private void SaveDebugImage(Texture2D texture, string tag)
    {
        try
        {
            byte[] png  = texture.EncodeToPNG();
            string path = Path.Combine(persistentPath, $"debug_capture_{tag}.png");
            File.WriteAllBytes(path, png);
            Debug.Log($"[CameraCapture] Debug image saved: {path}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CameraCapture] Could not save debug image: {ex.Message}");
        }
    }

    public string GetPersistentPath() => persistentPath;

    private void OnDestroy()
    {
        if (captureTexture != null) Destroy(captureTexture);
    }
}
