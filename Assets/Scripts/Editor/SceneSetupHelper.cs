#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// Sets up the SAM-3D capture scene for Meta Quest 3.
/// Creates a World Space UI canvas attached to the OVRCameraRig.
/// Menu: Tools > SAM-3D > Setup Scene
/// </summary>
public class SceneSetupHelper : MonoBehaviour
{
    [MenuItem("Tools/SAM-3D/Setup Scene")]
    public static void SetupScene()
    {
        Debug.Log("[SceneSetupHelper] Starting VR scene setup...");

        // Step 1: Find existing OVRCameraRig
        OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null)
        {
            Debug.LogError("[SceneSetupHelper] No OVRCameraRig found in scene! " +
                "Add one first using Meta Building Blocks.");
            EditorUtility.DisplayDialog("Error",
                "No OVRCameraRig found in scene!\n\n" +
                "Add one using Meta Building Blocks first, then run this setup again.",
                "OK");
            return;
        }

        Transform centerEye = cameraRig.centerEyeAnchor;
        Debug.Log("[SceneSetupHelper] Found OVRCameraRig with CenterEyeAnchor");

        // Step 2: Create Manager GameObject
        GameObject managerObj = new GameObject("[SAM3D] Manager");
        managerObj.AddComponent<CameraCapture>();
        managerObj.AddComponent<FalAiClient>();
        managerObj.AddComponent<GLBModelLoader>();
        managerObj.AddComponent<SAM3DCaptureManager>();

        Debug.Log("[SceneSetupHelper] Created Manager with all components");

        // Step 3: Create World Space Canvas (attached to CenterEyeAnchor so it follows head)
        GameObject canvasObj = new GameObject("[SAM3D] UI Canvas");
        canvasObj.transform.SetParent(centerEye, false);

        // Position canvas 2 meters in front of the headset, slightly below eye level
        canvasObj.transform.localPosition = new Vector3(0f, -0.3f, 2f);
        canvasObj.transform.localRotation = Quaternion.identity;
        canvasObj.transform.localScale = Vector3.one * 0.002f; // Scale down for world space

        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        // Size the canvas rect
        RectTransform canvasRect = canvasObj.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(800, 400);

        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        // Step 4: Create background panel
        GameObject panelObj = new GameObject("Panel");
        panelObj.transform.SetParent(canvasObj.transform, false);

        Image panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0.05f, 0.05f, 0.15f, 0.85f);

        RectTransform panelRect = panelObj.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        CanvasGroup canvasGroup = panelObj.AddComponent<CanvasGroup>();

        // Step 5: Create Status Text
        GameObject statusObj = new GameObject("StatusText");
        statusObj.transform.SetParent(panelObj.transform, false);

        TextMeshProUGUI statusText = statusObj.AddComponent<TextMeshProUGUI>();
        statusText.text = "Press A to Capture";
        statusText.fontSize = 48;
        statusText.alignment = TextAlignmentOptions.Center;
        statusText.color = Color.white;

        RectTransform statusRect = statusObj.GetComponent<RectTransform>();
        statusRect.anchorMin = new Vector2(0, 0.55f);
        statusRect.anchorMax = new Vector2(1, 0.95f);
        statusRect.offsetMin = new Vector2(20, 0);
        statusRect.offsetMax = new Vector2(-20, 0);

        // Step 6: Create Capture Button
        GameObject buttonObj = new GameObject("CaptureButton");
        buttonObj.transform.SetParent(panelObj.transform, false);

        Image buttonImage = buttonObj.AddComponent<Image>();
        buttonImage.color = new Color(0.1f, 0.5f, 0.9f, 1f);

        Button button = buttonObj.AddComponent<Button>();
        var colors = button.colors;
        colors.highlightedColor = new Color(0.2f, 0.6f, 1f, 1f);
        colors.pressedColor = new Color(0.05f, 0.3f, 0.7f, 1f);
        button.colors = colors;

        RectTransform buttonRect = buttonObj.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.2f, 0.1f);
        buttonRect.anchorMax = new Vector2(0.8f, 0.45f);
        buttonRect.offsetMin = Vector2.zero;
        buttonRect.offsetMax = Vector2.zero;

        // Button label
        GameObject labelObj = new GameObject("Label");
        labelObj.transform.SetParent(buttonObj.transform, false);

        TextMeshProUGUI labelText = labelObj.AddComponent<TextMeshProUGUI>();
        labelText.text = "CAPTURE";
        labelText.fontSize = 56;
        labelText.alignment = TextAlignmentOptions.Center;
        labelText.color = Color.white;

        RectTransform labelRect = labelObj.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        // Step 7: Create Loading Spinner (hidden by default)
        GameObject spinnerObj = new GameObject("LoadingSpinner");
        spinnerObj.transform.SetParent(panelObj.transform, false);

        Image spinnerImage = spinnerObj.AddComponent<Image>();
        spinnerImage.color = new Color(0.2f, 0.7f, 1f, 1f);
        spinnerObj.SetActive(false);

        RectTransform spinnerRect = spinnerObj.GetComponent<RectTransform>();
        spinnerRect.anchorMin = new Vector2(0.4f, 0.15f);
        spinnerRect.anchorMax = new Vector2(0.6f, 0.4f);
        spinnerRect.offsetMin = Vector2.zero;
        spinnerRect.offsetMax = Vector2.zero;

        // Step 8: Add CaptureUI to panel and wire it up
        CaptureUI captureUI = panelObj.AddComponent<CaptureUI>();

        SerializedObject uiSO = new SerializedObject(captureUI);
        uiSO.FindProperty("captureButton").objectReferenceValue = button;
        uiSO.FindProperty("statusText").objectReferenceValue = statusText;
        uiSO.FindProperty("loadingSpinner").objectReferenceValue = spinnerImage;
        uiSO.FindProperty("loadingCanvasGroup").objectReferenceValue = canvasGroup;
        uiSO.ApplyModifiedProperties();

        // Step 9: Wire up Manager references
        SAM3DCaptureManager manager = managerObj.GetComponent<SAM3DCaptureManager>();
        SerializedObject managerSO = new SerializedObject(manager);
        managerSO.FindProperty("cameraCapture").objectReferenceValue = managerObj.GetComponent<CameraCapture>();
        managerSO.FindProperty("falAiClient").objectReferenceValue = managerObj.GetComponent<FalAiClient>();
        managerSO.FindProperty("glbModelLoader").objectReferenceValue = managerObj.GetComponent<GLBModelLoader>();
        managerSO.FindProperty("captureUI").objectReferenceValue = captureUI;
        managerSO.ApplyModifiedProperties();

        Debug.Log("[SceneSetupHelper] ✅ Scene setup complete!");
        Debug.Log("[SceneSetupHelper] ⚠️ IMPORTANT: Set your FAL AI API key on [SAM3D] Manager in Inspector!");

        EditorUtility.DisplayDialog("Setup Complete!",
            "SAM-3D scene is configured!\n\n" +
            "NEXT STEP: Select [SAM3D] Manager in Hierarchy\n" +
            "and set your FAL AI API Key in the Inspector.\n\n" +
            "Input: Press A button on right controller to capture.\n" +
            "Or press the on-screen CAPTURE button.",
            "Got it!");

        Selection.activeGameObject = managerObj;
        EditorGUIUtility.PingObject(managerObj);
    }

    [MenuItem("Tools/SAM-3D/Remove Setup")]
    public static void RemoveSetup()
    {
        // Clean up previously created objects
        var manager = GameObject.Find("[SAM3D] Manager");
        if (manager != null) DestroyImmediate(manager);

        var canvas = GameObject.Find("[SAM3D] UI Canvas");
        if (canvas != null) DestroyImmediate(canvas);

        Debug.Log("[SceneSetupHelper] Previous setup removed");
    }
}
#endif
