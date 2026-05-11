# SAM-3D Capture System Setup Guide

## Overview
This guide walks you through setting up the SAM-3D 3D object capture system for Meta Quest 3. The system captures objects using your Quest's camera, sends them to FAL AI for 3D reconstruction, and lets you interact with the models using hand tracking.

## What You Need
- Meta Quest 3 with hand tracking enabled
- FAL AI API key (get credits at https://fal.ai)
- Unity 2022.2+ with Android build support

## Step-by-Step Setup

### 1. Get Your FAL AI API Key

1. Go to https://fal.ai/dashboard
2. Sign up or log in
3. Generate an API key
4. Copy it (you'll need this in Step 4)

### 2. Open Your Project

1. Open the Cross Reality Project in Unity
2. Open `Assets/Scenes/SampleScene.unity`
3. Verify you see:
   - `[BuildingBlock] Camera Rig` - XR camera setup
   - `[BuildingBlock] Passthrough` - For camera feed
   - Directional Light
   - Global Volume

### 3. Auto-Setup the Scene

**Option A: Automatic (Recommended)**

1. Go to **Tools > SAM-3D > Setup Scene**
2. This creates all GameObjects and wires them up automatically
3. Skip to Step 4

**Option B: Manual Setup**

If automatic setup doesn't work, follow these steps:

#### Create the Manager GameObject

1. Right-click in Hierarchy, create an empty GameObject
2. Name it: `[Manager] SAM3D Capture`
3. Add these components (Add Component button in Inspector):
   - `CameraCapture`
   - `FalAiClient`
   - `GLBModelLoader`
   - `SAM3DCaptureManager`

#### Create the UI Canvas

1. Right-click in Hierarchy, go to **UI > Canvas**
2. Name it: `[UI] Capture Canvas`
3. Select the Canvas, in Inspector set:
   - Render Mode: `Screen Space - Overlay`

#### Add UI Elements to Canvas

**Status Text:**
1. Right-click on Canvas, go to **TextMeshPro > Text**
2. Name it: `StatusText`
3. Set text to: "Ready to capture"
4. Position at top: X=0, Y=200
5. Size: 800x100

**Capture Button:**
1. Right-click on Canvas, go to **UI > Button - TextMeshPro**
2. Name it: `CaptureButton`
3. Change button text to: `CAPTURE`
4. Position at center: X=0, Y=0
5. Size: 400x100
6. Color: Light blue (RGB: 0.2, 0.6, 1.0)

**Loading Spinner:**
1. Right-click on Canvas, go to **UI > Image**
2. Name it: `LoadingSpinner`
3. Position at bottom: X=0, Y=-150
4. Size: 100x100
5. Color: Light blue (same as button)
6. **Important**: Uncheck "Active" in Inspector

#### Add CaptureUI Component

1. Select the Canvas object
2. Add Component: `CaptureUI`
3. Drag these UI elements into the CaptureUI fields:
   - Capture Button: (drag CaptureButton here)
   - Status Text: (drag StatusText here)
   - Loading Spinner: (drag LoadingSpinner here)
   - Loading Canvas Group: (create one, see below)

#### Create CanvasGroup for Loading Panel

1. Select Canvas
2. Add Component: `CanvasGroup`
3. In CaptureUI, drag Canvas to "Loading Canvas Group" field

#### Wire Up Manager References

1. Select `[Manager] SAM3D Capture`
2. In SAM3DCaptureManager, drag these components:
   - Camera Capture: (SAM3DCaptureManager > CameraCapture)
   - Fal Ai Client: (SAM3DCaptureManager > FalAiClient)
   - Glb Model Loader: (SAM3DCaptureManager > GLBModelLoader)
   - Capture UI: (drag the Canvas with CaptureUI component)

### 4. Configure Your API Key

1. Select `[Manager] SAM3D Capture` in Hierarchy
2. In Inspector, find `SAM3DCaptureManager` component
3. In the "Fal Ai Api Key" field, paste your FAL AI API key
4. **Important**: Do NOT commit this key to version control!
   - Add `SAM3DCaptureManager.cs` to `.gitignore` or
   - Use environment variables for deployment

### 5. Configure Camera Capture Settings (Optional)

In the Inspector, you can adjust:

**CameraCapture component:**
- Capture Width: 1280 (for quality)
- Capture Height: 960 (for quality)

**FalAiClient component:**
- API Key: (already set above)
- Request Timeout: 60 seconds
- Polling Interval: 2 seconds
- Max Polling Attempts: 60

**GLBModelLoader component:**
- Model Scale: 0.5 (adjust for model size)
- Add Physics: ✓ (checked)
- Add Colliders: ✓ (checked)

**SAM3DCaptureManager component:**
- Spawn Distance: 1.5 (how far in front of camera)
- Capture Prompt: "object" (change to "chair", "plant", etc. for specific objects)

### 6. Test in Editor (Optional)

1. Press Play in Unity
2. Open Console (Window > General > Console)
3. Errors should appear here
4. Press Escape to stop play mode

### 7. Build for Quest 3

#### Setup Build Target

1. Go to **File > Build Settings**
2. Under Platform, click **Android**
3. Click **Switch Platform** (may take a minute)

#### Player Settings

1. Go to **Edit > Project Settings > Player**
2. Under Android section:
   - Company Name: Your name/company
   - Product Name: "Cross Reality"
   - Minimum API Level: API level 28+
   - Target API Level: API level 34+

#### Build and Run

1. Connect your Quest 3 via USB
2. Go to **File > Build and Run**
3. It will build and deploy to your Quest
4. The app will launch automatically

### 8. Using the App in VR

**First Time:**
1. Put on your Quest 3
2. Allow permissions (camera, hand tracking)
3. Wait for the app to load

**Capturing Objects:**
1. Point at an object (table, plant, person, etc.)
2. Press the **CAPTURE** button (shown on screen)
3. Wait for "Processing..." message
4. The AI processes for 10-30 seconds (depending on complexity)
5. A 3D model appears in front of you

**Interacting with Models:**
1. **Grab**: Make a pinch gesture (thumb + index finger close together)
2. **Move**: Keep pinching and move your hand
3. **Rotate**: While pinching, rotate your hand (hand rotation follows)
4. **Drop**: Release the pinch gesture

**Capture Again:**
1. Press the button again to capture a new object
2. The old model stays in place
3. You can have multiple models in the scene

## Troubleshooting

### "API Key not set"
- Double-check the API key is pasted correctly in Inspector
- Make sure there are no extra spaces
- Try a fresh copy from https://fal.ai/dashboard

### "Failed to capture screenshot"
- Ensure Camera.main exists in scene
- Check that CameraCapture has a reference to the camera

### "API request failed"
- Check your internet connection
- Verify FAL AI is not in maintenance
- Check your API key has active credits

### "Failed to parse response"
- The API format may have changed
- Check the FAL AI API documentation
- Look at console errors for details

### "Model doesn't appear"
- Check if the API returned a valid GLB URL
- Verify internet connection to download the model
- Check console for download errors

### "Can't grab model"
- Ensure Quest 3 hand tracking is enabled
- Check that both hands are visible to the headset
- Try a pinch gesture (thumb and index together)
- Make sure you're pinching on the model

### "App crashes on startup"
- Check that all components are properly attached
- Ensure API key is set (won't crash, but won't work)
- Look at logcat output: `adb logcat | grep Unity`

## Advanced: Custom Prompts

The "Capture Prompt" field controls what the AI looks for:

- `"object"` - Detects any object
- `"chair"` - Focuses on chairs
- `"plant"` - Detects plants
- `"person"` - For people (only upper body works well)
- `"furniture"` - General furniture
- `"toy"` - Small toys

Try different prompts for better results!

## Advanced: Input Binding

To use a controller button instead of the on-screen button:

1. Select `[Manager] SAM3D Capture`
2. In SAM3DCaptureManager, find "Capture Input Action"
3. Create a new InputActionReference or use existing mappings
4. Bind to controller button (e.g., X button on right controller)

## Performance Optimization

If the app feels slow:

1. Reduce capture resolution:
   - CameraCapture: 640x480 instead of 1280x960
   - Faster capture, lower quality 3D models

2. Reduce model complexity:
   - In FalAiClient, set Detection Threshold higher (0.7 instead of 0.5)
   - This reduces detail in the 3D model

3. Enable frame rate reduction:
   - Project Settings > Quality
   - Target Frame Rate: 60 instead of 90

## Next Steps

Once everything is working:

1. **Add more interactivity**:
   - Color changing
   - Resizing
   - Saving models

2. **Multi-user support**:
   - Use Photon or Mirror for networking
   - Share captured models with other players

3. **Better segmentation**:
   - Try different prompts
   - Capture from multiple angles
   - Let AI auto-detect multiple objects

## Support

If you run into issues:

1. Check the console logs (Window > General > Console)
2. Verify all components are wired up in Inspector
3. Re-run the auto-setup script
4. Check FAL AI API documentation: https://fal.ai/models/fal-ai/sam-3/3d-objects/api

Good luck! 🚀
