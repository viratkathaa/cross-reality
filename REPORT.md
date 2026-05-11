# Cross-Reality Project — Status Report

> **Platform:** Meta Quest 3 · **Engine:** Unity 6000.3.8f1 URP · **SDK:** Meta XR 201.0.0

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [System Architecture](#2-system-architecture)
3. [Complete Bidirectional Loop](#3-complete-bidirectional-loop)
4. [Unity Scripts (Quest 3)](#4-unity-scripts-quest-3)
5. [Desktop Scripts (Python)](#5-desktop-scripts-python)
6. [Controller Reference](#6-controller-reference)
7. [Firebase Structure](#7-firebase-structure)
8. [Dependencies & Packages](#8-dependencies--packages)
9. [Confirmed Working](#9-confirmed-working)
10. [Known Limitations](#10-known-limitations)

---

## 1. Project Overview

A **bidirectional Mixed Reality ↔ Desktop pipeline** running on Meta Quest 3.

The user looks at a real-world object through the passthrough camera, presses a button, and an AI-generated 3D model of that object appears in their mixed-reality space — grabbable, physics-enabled, and interactable. They can then send it to a laptop where it opens in Blender. After editing, the updated model can be sent back into VR. The desktop can also independently push any `.glb` file into the headset.

---

## 2. System Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                      META QUEST 3 (MR)                           │
│                                                                  │
│  ObjectDetectionAgent ──► SAM3DCaptureManager                   │
│    (on-device CV model)          │                               │
│    detects: cup, chair, mouse…   │                               │
│                                  ▼                               │
│  CameraCapture ──────────► FalAiClient ──────► FAL AI Cloud     │
│  (passthrough / screen           │               SAM-3D model    │
│   capture fallback)              │               generation      │
│                                  ▼                               │
│               GLB URL ──► GLBModelLoader                        │
│                               │                                  │
│                               ▼                                  │
│          Grabbable 3D Model (GLTFast + Interaction SDK)          │
│          BoxCollider · Rigidbody · HandGrabInteractable          │
│                               │                                  │
│                    ┌──────────┴───────────┐                      │
│                    │                      │                      │
│              Press B button         TransferZone                 │
│                    │              (collision-based,              │
│                    ▼               optional)                     │
│             DatabaseManager                                      │
│           (Firebase REST POST)                                   │
│                    │                                             │
│  VRModelReceiver ◄─── polls /desktop_to_vr every 3s             │
│  (spawns desktop models                                          │
│   to the right of capture models)                               │
└──────────────────────────────────────────────────────────────────┘
                     │                       ▲
           /models (legacy)          /desktop_to_vr
           /vr_to_desktop                    │
                     │                       │
┌──────────────────────────────────────────────────────────────────┐
│               FIREBASE REALTIME DATABASE                         │
│      https://crossreality-ec12f.asia-southeast1.firebasedatabase │
│                                                                  │
│   /models          →  VR-to-Desktop  (legacy collection)        │
│   /desktop_to_vr   →  Desktop-to-VR  (new collection)           │
└──────────────────────────────────────────────────────────────────┘
                     │                       │
              receiver.py              upload_to_vr.py
              (polls + opens           blender_send_to_vr.py
               Blender)                (Blender N-panel addon)
                     │                       │
┌──────────────────────────────────────────────────────────────────┐
│                     LAPTOP (Windows)                             │
│                                                                  │
│  receiver.py           — auto-opens GLBs in Blender             │
│  upload_to_vr.py       — CLI uploader, any .glb → VR            │
│  blender_send_to_vr.py — Blender addon, "Send to VR" button     │
└──────────────────────────────────────────────────────────────────┘
```

---

## 3. Complete Bidirectional Loop

```
Real Object
    │
    │  A button → capture passthrough frame
    │  ObjectDetectionAgent sets prompt automatically
    ▼
FAL AI SAM-3D  (cloud)
    │
    │  Returns textured GLB URL
    ▼
VR Model spawns in MR space
    │  Physics-enabled, grabbable with hands or controllers
    │
    │  B button
    ▼
Firebase  /models
    │
    │  receiver.py polls every 2s
    ▼
Blender opens on laptop
    │
    │  User edits / sculpts / recolours
    │  N-panel → VR tab → "Send to VR"
    ▼
0x0.st  (file host)  →  Firebase  /desktop_to_vr
    │
    │  VRModelReceiver.cs polls every 3s
    ▼
Updated model spawns in VR  ←─────────────────────┐
    │                                              │
    │  Also: desktop user can upload               │
    │  any .glb independently                      │
    └──────────────────────────────────────────────┘
         python upload_to_vr.py model.glb "name"
```

---

## 4. Unity Scripts (Quest 3)

### `SAM3DCaptureManager.cs`
Main orchestrator for the entire capture workflow.

- Listens to `ObjectDetectionAgent.OnDetectionResponseReceived` and auto-selects the highest-confidence non-device label as the FAL AI prompt
- **A button** → calls `StartCapture()` → camera capture → FAL AI → GLB load
- **B button** → calls `TransferLastModel()` → uploads last GLB URL to Firebase
- `autoUploadOnLoad` toggle (Inspector) — uploads immediately on model load (for testing)
- Filters out device labels (laptop, monitor, tv…) so they are never used as capture prompts
- Tags every spawned model as `"SAM3DModel"` for TransferZone collision detection

---

### `CameraCapture.cs`
Captures the passthrough camera frame to send to FAL AI.

- **Primary path:** `PassthroughCameraAccess.GetTexture()` — raw camera feed, requires HzOS v85+
- **Fallback path:** `ScreenCapture.CaptureScreenshotAsTexture()` — works on any firmware (HzOS v84 compatible)
- `screenCaptureFallback` toggle in Inspector (default: on)
- `saveDebugImages` saves `debug_capture_passthrough.png` / `debug_capture_screencap.png` to persistent data path for ADB inspection

---

### `FalAiClient.cs`
HTTP client for the FAL AI SAM-3D API.

- Uploads image as base64 or sends image URL
- Sends prompt (object label) alongside image
- Events: `OnResponseReceived(FalAiResponse)`, `OnProgress(float)`, `OnError(string)`
- Configurable API key via Inspector or `SetApiKey(string)`

---

### `GLBModelLoader.cs`
Downloads and instantiates GLB models in the scene.

- Downloads GLB bytes via `UnityWebRequest`
- Parses with **GLTFast 6.10.1**
- Adds `BoxCollider` sized to mesh bounds
- Adds `Rigidbody` (gravity on, `ContinuousDynamic` for thin-surface collision)
- Adds `Grabbable` + `HandGrabInteractable` (pinch/palm) + `GrabInteractable` (controller)
- Fixes materials to `Universal Render Pipeline/Lit` (GLTFast shader graphs are stripped on Android builds)
- Falls back to captured passthrough frame as albedo if GLB has no embedded texture

---

### `DatabaseManager.cs`
Firebase Realtime Database REST client (no Firebase SDK).

- `SaveModel(name, glbUrl)` → `POST /models.json?auth=<secret>`
- Payload: `{ name, glb_url, thumbnail_url, timestamp_ms, status:"ready" }`
- Events: `OnSaveSuccess(string key)`, `OnSaveError(string error)`
- `IsDatabaseConfigured()` validates URL format before any network call

---

### `VRModelReceiver.cs`
Polls Firebase `/desktop_to_vr` and spawns desktop-sent models in VR.

- Polls every 3 seconds via `UnityWebRequest`
- Creates its own dedicated `GLBModelLoader` instance so desktop models are never cleared by the capture flow
- Spawns models fanned out to the right (spawnSpacing configurable) so they don't overlap capture models
- Persists seen keys in `PlayerPrefs` — models don't re-spawn after app restart
- Shows `↓ Receiving 'name' from desktop…` then success/error notification via CaptureUI
- Includes a hand-rolled Firebase JSON parser (no external JSON library needed)

---

### `TransferZone.cs`
Positions trigger volumes over detected screens for collision-based upload.

- Pool of 5 independent zone slots — one per simultaneously detected screen
- Each slot is an auto-created semi-transparent cube (`URP/Lit`, transparent queue)
- Subscribes to `ObjectDetectionAgent` — filters for: `laptop, monitor, tv, television, screen, computer, display, keyboard`
- Raycasts from camera through detected bounding box centre; `maxRaycastDepth = 3.0m` prevents hitting far walls
- Falls back to configurable `fallbackDepth` when no surface hit
- Colour states: idle (cyan), active (green), success (yellow), error (red)
- Zones timeout after `deviceTimeoutSeconds = 2.5s` of not being detected

---

### `ZoneTriggerReceiver.cs`
Tiny helper placed on each TransferZone slot cube.

- Forwards `OnTriggerEnter` to the parent `TransferZone` manager
- Required because `MonoBehaviour.OnTriggerEnter` must be on the same GameObject as the collider

---

### `CaptureUI.cs`
Spatial MR UI panel — does not follow the head.

- Switches Canvas to **World Space** at runtime (compatible with **OVR Overlay Canvas** upgrade)
- Spawns 1.2m ahead, 25cm below eye level on first run
- **Lazy-follow:** slides back into comfortable view after 2.5s of being out of sight
- Background colour changes: normal (dark blue), error (red, auto-resets 4s), success (green, auto-resets 3s)
- Physical size: 40cm × 14cm
- Spinner animates during FAL AI processing

---

### `HandGrabbable.cs`
Lightweight fallback grabber when the Interaction SDK is unavailable.

---

### `FalAiResponse.cs`
Data model for the FAL AI JSON response (`model_glb.url` field).

---

### `SceneSetupHelper.cs`
Editor-only utility for initial scene configuration.

---

## 5. Desktop Scripts (Python)

### `receiver.py`
Polls Firebase and auto-opens new models in Blender.

```bash
python receiver.py
```

- Polls `/models` every 2 seconds
- Downloads GLB to `downloaded_models/` with unique filename (`name_urlhash.glb`)
- Launches Blender via `subprocess.Popen` with `--python-expr`:
  - Clears default scene
  - Imports GLB via `bpy.ops.import_scene.gltf()`
- `.seen_keys` file prevents re-opening across restarts
- Configured for Blender at `C:\Program Files\Blender Foundation\Blender 5.0\blender.exe`

---

### `upload_to_vr.py`
CLI tool — send any `.glb` file to the Quest 3.

```bash
python upload_to_vr.py model.glb "model name"
```

- Uploads file to **0x0.st** via multipart POST (pure stdlib, no `requests`)
- Writes `{ name, glb_url, timestamp_ms, status:"ready", source:"desktop" }` to Firebase `/desktop_to_vr`
- Quest 3 receives and spawns the model within ~3 seconds

---

### `blender_send_to_vr.py`
Blender 4.x / 5.x addon — "Send to VR" button inside Blender.

**Install:** Edit → Preferences → Add-ons → Install → select `blender_send_to_vr.py` → Enable

**Usage:** 3D Viewport → **N panel → VR tab** → enter model name → click **Send to VR**

- Exports selected objects (or full scene if nothing selected) as GLB to temp directory
- Uploads to 0x0.st, writes to Firebase
- Shows `✓ Sent!` or `✗ error` status in the panel

---

## 6. Controller Reference

| Button | Action |
|---|---|
| **A (right controller)** | Capture → FAL AI → spawn 3D model in MR |
| **B (right controller)** | Upload last model to Firebase → Blender opens on laptop |
| **Hand pinch / palm grab** | Pick up any spawned model (hand tracking) |
| **Controller grip** | Pick up any spawned model (controller) |

---

## 7. Firebase Structure

```
crossreality-ec12f-default-rtdb.asia-southeast1.firebasedatabase.app
│
├── /models/{pushKey}               ← VR → Desktop (legacy)
│   ├── name          : "cup"
│   ├── glb_url       : "https://fal.run/files/..."
│   ├── thumbnail_url : ""
│   ├── timestamp_ms  : 1713456789000
│   └── status        : "ready"
│
└── /desktop_to_vr/{pushKey}        ← Desktop → VR (new)
    ├── name          : "edited_cup"
    ├── glb_url       : "https://0x0.st/..."
    ├── timestamp_ms  : 1713456800000
    ├── status        : "ready"
    └── source        : "desktop"
```

---

## 8. Dependencies & Packages

### Unity Packages (`Packages/manifest.json`)

| Package | Version | Purpose |
|---|---|---|
| `com.meta.xr.sdk.core` | 201.0.0 | OVRCameraRig, OVRInput, PassthroughCameraAccess |
| `com.meta.xr.mrutilitykit` | 201.0.0 | MRUK scene understanding, EffectMesh |
| `com.meta.xr.sdk.interaction` | 201.0.0 | Grabbable, HandGrabInteractable |
| `com.meta.xr.sdk.interaction.ovr` | 201.0.0 | OVR Interaction integration |
| `com.meta.xr.sdk.haptics` | 201.0.0 | Haptic feedback |
| `com.meta.xr.sdk.platform` | 201.0.0 | Platform services |
| `com.unity.cloud.gltfast` | 6.10.1 | GLB/GLTF loading at runtime |
| `com.unity.render-pipelines.universal` | 17.3.0 | URP rendering |
| `com.unity.xr.meta-openxr` | 2.2.0 | Meta OpenXR backend |
| `com.unity.xr.openxr` | 1.16.1 | OpenXR standard |
| `com.unity.ai.inference` | 2.3.0 | On-device ML inference for object detection |
| `com.unity.inputsystem` | 1.18.0 | Input system |
| `com.unity.mobile.android-logcat` | 1.4.7 | ADB log viewer |

> ⚠️ `com.meta.xr.sdk.voice` and `com.meta.xr.sdk.audio` are intentionally excluded — the voice SDK's `ConduitManifestGenerationManager` crashes Unity 6 builds.

### Python (`laptop_receiver/requirements.txt`)
Pure stdlib is used throughout — no pip installs required beyond what ships with Python 3.9+.

---

## 9. Confirmed Working

| Feature | Evidence |
|---|---|
| Object detection | Logcat: `Seeing: person`, `keyboard`, `chair`, `mouse` |
| FAL AI model generation | `downloaded_models/` contains 8 GLBs |
| Firebase round-trip | `.seen_keys` has confirmed push keys |
| Blender auto-open | `receiver.py` confirmed working by user |
| VR model grabbing | Confirmed by user in testing |
| B-button upload | User confirmed Firebase → Blender pipeline end-to-end |
| Spatial UI | OVR Overlay Canvas upgrade applied |

**Models generated so far:** `person` (×3), `mouse` (×5)

---

## 10. Known Limitations

| Issue | Detail | Fix |
|---|---|---|
| **HzOS v84** | `PassthroughCameraAccess` requires v85. Raw camera feed unavailable. | Update lab headset firmware via Settings → System → Software Update (or join Public Test Channel) |
| **TransferZone not visible** | Zone cubes spawn and are positioned correctly but may not render due to URP transparent material setup. | B button used as primary transfer method instead |
| **Firebase secret in plaintext** | Secret key is hardcoded in scripts and Unity Inspector. | Acceptable for BTP demo. Use Firebase Auth tokens for production. |
| **0x0.st file expiry** | Files uploaded via `upload_to_vr.py` expire after ~30 days. | Switch to Firebase Storage REST API for permanent hosting. |
| **Single GLBModelLoader** | SAM3DCaptureManager's `ClearCurrentModel()` only clears capture-flow models; VRModelReceiver uses its own loader. | Working correctly — desktop and capture models are independent. |
