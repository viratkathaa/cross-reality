using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Grab;
using Oculus.Interaction.GrabAPI;

/// <summary>
/// Downloads GLB files from FAL AI and loads them into the scene using GLTFast.
/// </summary>
public class GLBModelLoader : MonoBehaviour
{
    [SerializeField] private float modelScale = 0.3f;
    [SerializeField] private bool addPhysics = true;
    [SerializeField] private bool addColliders = true;

    public event Action<GameObject> OnModelLoaded;
    public event Action<string> OnLoadError;
    public event Action<float> OnProgress;

    private GameObject currentModel;
    private FalAiClient falAiClient;

    private void Start()
    {
        falAiClient = GetComponent<FalAiClient>();
        if (falAiClient == null)
            Debug.LogError("[GLBModelLoader] FalAiClient component not found on same GameObject");
    }

    /// <summary>
    /// Downloads GLB from URL and loads it into the scene at spawnPosition.
    /// </summary>
    /// <summary>
    /// Downloads GLB from URL and loads it at spawnPosition.
    /// Pass capturedTexture to use the original camera frame as a fallback
    /// albedo if the GLB contains no embedded texture.
    /// </summary>
    public void LoadGLBFromUrl(string glbUrl, Vector3 spawnPosition, Texture2D capturedTexture = null)
    {
        if (string.IsNullOrEmpty(glbUrl))
        {
            OnLoadError?.Invoke("GLB URL is empty");
            return;
        }

        StartCoroutine(DownloadAndLoadGLB(glbUrl, spawnPosition, capturedTexture));
    }

    private IEnumerator DownloadAndLoadGLB(string glbUrl, Vector3 spawnPosition, Texture2D capturedTexture)
    {
        OnProgress?.Invoke(0.3f);
        Debug.Log($"[GLBModelLoader] Downloading GLB from: {glbUrl}");

        UnityWebRequest www = UnityWebRequest.Get(glbUrl);
        www.timeout = 120;
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            OnLoadError?.Invoke($"Failed to download GLB: {www.error}");
            www.Dispose();
            yield break;
        }

        byte[] glbBytes = www.downloadHandler.data;
        Debug.Log($"[GLBModelLoader] Downloaded {glbBytes.Length} bytes");
        www.Dispose();

        OnProgress?.Invoke(0.6f);

        yield return StartCoroutine(LoadGLBBytes(glbBytes, spawnPosition, capturedTexture));
    }

    private IEnumerator LoadGLBBytes(byte[] glbBytes, Vector3 spawnPosition, Texture2D capturedTexture)
    {
        var gltfImport = new GltfImport();

        // Load the GLB from bytes
        var loadTask = gltfImport.LoadGltfBinary(glbBytes, null);
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (!loadTask.Result)
        {
            OnLoadError?.Invoke("GLTFast failed to parse GLB file");
            yield break;
        }

        // Pull textures directly from the parsed GLB data before any shader
        // creation is attempted. This is the reliable path — GltfImport.GetTexture()
        // returns the raw Texture2D regardless of which shader is active or stripped.
        Texture2D glbTexture = null;
        if (gltfImport.TextureCount > 0)
        {
            glbTexture = gltfImport.GetTexture(0);
            Debug.Log($"[GLBModelLoader] GLB has {gltfImport.TextureCount} embedded texture(s) — using index 0: {glbTexture?.name ?? "null"}");
        }
        else
        {
            Debug.Log("[GLBModelLoader] GLB has no embedded textures — falling back to captured image");
        }

        // Prefer the GLB's own texture; fall back to the passthrough capture
        Texture2D resolvedTexture = glbTexture != null ? glbTexture : capturedTexture;

        OnProgress?.Invoke(0.8f);

        // Create container GameObject
        GameObject modelContainer = new GameObject("SAM3D_Model");
        modelContainer.transform.position = spawnPosition;
        modelContainer.transform.localScale = Vector3.one * modelScale;

        // Instantiate into scene
        var instantiateTask = gltfImport.InstantiateMainSceneAsync(modelContainer.transform);
        yield return new WaitUntil(() => instantiateTask.IsCompleted);

        if (!instantiateTask.Result)
        {
            OnLoadError?.Invoke("GLTFast failed to instantiate model");
            Destroy(modelContainer);
            yield break;
        }

        // ── Collider (required by HandGrabInteractable) ──────────────────────────
        if (addColliders)
        {
            var meshRenderers = modelContainer.GetComponentsInChildren<MeshRenderer>();
            BoxCollider col = modelContainer.AddComponent<BoxCollider>();
            if (meshRenderers.Length > 0)
            {
                Bounds combinedBounds = meshRenderers[0].bounds;
                foreach (var r in meshRenderers)
                    combinedBounds.Encapsulate(r.bounds);
                col.center = modelContainer.transform.InverseTransformPoint(combinedBounds.center);
                col.size   = combinedBounds.size * (1f / modelScale);
            }
        }

        // ── Rigidbody ─────────────────────────────────────────────────────────
        Rigidbody rb = null;
        if (addPhysics)
        {
            rb = modelContainer.AddComponent<Rigidbody>();
            rb.useGravity  = true; // fall onto room surfaces (EffectMesh colliders)
            rb.isKinematic = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // prevent tunnelling through thin mesh colliders
        }

        // Fix materials — GLTFast shader graphs get stripped on Android builds.
        // Replace with URP/Lit which is always included, preserving color + texture.
        FixMaterialsForURP(modelContainer, resolvedTexture);

        // ── Meta Interaction SDK grabbing (hands + controllers) ──────────────
        // One Grabbable drives movement; both interactable types share it so the
        // model responds to whichever input is active (RealHands or controllers).
        if (rb != null)
        {
            var grabbable = modelContainer.AddComponent<Grabbable>();

            // Hand tracking — pinch and palm grabs via RealHands building block
            var hgi = modelContainer.AddComponent<HandGrabInteractable>();
            hgi.InjectAllHandGrabInteractable(
                GrabTypeFlags.All,
                rb,
                GrabbingRule.DefaultPinchRule,
                GrabbingRule.DefaultPalmRule
            );
            hgi.InjectOptionalPointableElement(grabbable);

            // Controller grabbing — works alongside hand tracking
            var gi = modelContainer.AddComponent<GrabInteractable>();
            gi.InjectAllGrabInteractable(rb);
            gi.InjectOptionalPointableElement(grabbable);

            // Distance hand grab — pinch from across the room
            var dhgi = modelContainer.AddComponent<DistanceHandGrabInteractable>();
            dhgi.InjectAllDistanceHandGrabInteractable(
                GrabTypeFlags.Pinch,
                rb,
                GrabbingRule.DefaultPinchRule,
                GrabbingRule.DefaultPalmRule
            );
            dhgi.InjectOptionalPointableElement(grabbable);

            Debug.Log("[GLBModelLoader] Model is grabbable — hands (pinch/palm/distance) + controllers");
        }
        else
        {
            // Physics disabled — fall back to the lightweight custom grabber
            modelContainer.AddComponent<HandGrabbable>();
        }

        currentModel = modelContainer;
        OnProgress?.Invoke(1.0f);

        Debug.Log($"[GLBModelLoader] Model loaded at {spawnPosition}");
        OnModelLoaded?.Invoke(modelContainer);
    }

    /// <summary>
    /// Replaces every material with URP/Lit so the model renders correctly when
    /// GLTFast's shader graphs are stripped from Android builds.
    /// Texture priority:
    ///   1. Embedded GLB texture (tries all known property name variants)
    ///   2. capturedTexture — the passthrough frame used to generate the model;
    ///      SAM-3D UVs are front-projected so the image maps correctly.
    /// </summary>
    private static void FixMaterialsForURP(GameObject root, Texture2D capturedTexture = null)
    {
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit == null)
        {
            Debug.LogWarning("[GLBModelLoader] Could not find 'Universal Render Pipeline/Lit' shader.");
            return;
        }

        // GLTFast shader graph uses no-underscore names; URP Lit uses _BaseMap / _BaseColor.
        // Try all variants so we work regardless of which shader was active.
        string[] texCandidates   = { "baseColorTexture", "_BaseColorTexture", "_BaseMap", "_MainTex" };
        string[] colorCandidates = { "baseColorFactor",  "_BaseColorFactor",  "_BaseColor", "_Color" };

        int replaced = 0;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            var src = renderer.sharedMaterials;
            var dst = new Material[src.Length];

            for (int i = 0; i < src.Length; i++)
            {
                var orig = src[i];
                var mat  = new Material(urpLit);
                mat.name = orig != null ? orig.name + "_URP" : "URP_Mat";

                // Declare outside so texture fallback can run even when orig is null
                Texture embeddedTex = null;

                if (orig != null)
                {
                    // ── Colour ────────────────────────────────────────────────
                    foreach (var prop in colorCandidates)
                    {
                        if (orig.HasProperty(prop))
                        {
                            mat.SetColor("_BaseColor", orig.GetColor(prop));
                            break;
                        }
                    }

                    // ── Texture (embedded in GLB) ─────────────────────────────
                    foreach (var prop in texCandidates)
                    {
                        if (orig.HasProperty(prop))
                        {
                            embeddedTex = orig.GetTexture(prop);
                            if (embeddedTex != null)
                            {
                                Debug.Log($"[GLBModelLoader] Found embedded texture '{embeddedTex.name}' " +
                                          $"via property '{prop}'");
                                break;
                            }
                        }
                    }

                    // ── PBR (best-effort) ─────────────────────────────────────
                    if (orig.HasProperty("_Metallic"))
                        mat.SetFloat("_Metallic", orig.GetFloat("_Metallic"));
                    if (orig.HasProperty("_Smoothness"))
                        mat.SetFloat("_Smoothness", orig.GetFloat("_Smoothness"));
                    else if (orig.HasProperty("_Glossiness"))
                        mat.SetFloat("_Smoothness", orig.GetFloat("_Glossiness"));
                }

                // ── Apply texture — OUTSIDE null check so fallback always runs ──
                if (embeddedTex != null)
                {
                    mat.SetTexture("_BaseMap", embeddedTex);
                }
                else if (capturedTexture != null)
                {
                    // GLTFast returned null material (shader stripped) — use the
                    // original passthrough frame as albedo. SAM-3D UVs are
                    // front-projected from the input image so it maps correctly.
                    mat.SetTexture("_BaseMap", capturedTexture);
                    Debug.Log("[GLBModelLoader] orig was null — applying captured image as albedo");
                }

                dst[i] = mat;
                replaced++;
            }

            renderer.sharedMaterials = dst;
        }

        Debug.Log($"[GLBModelLoader] Fixed {replaced} material(s) for URP.");
    }

    public void ClearCurrentModel()
    {
        if (currentModel != null)
        {
            Destroy(currentModel);
            currentModel = null;
        }
    }

    public GameObject GetCurrentModel() => currentModel;
    public void SetModelScale(float scale) => modelScale = scale;
}
