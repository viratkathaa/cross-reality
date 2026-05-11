# Knowledge Transfer Document
## Cross-Reality Object Digitization

> This document is the single source of truth for anyone picking up this project.
> Read it end-to-end before touching the code.

---

## 1. What This Project Is

A **bidirectional Mixed Reality ↔ Desktop pipeline** running on Meta Quest 3.

The user looks at a real-world object through the headset's passthrough camera, presses **A**, and an AI-generated 3D model of that object appears in their mixed-reality space — grabbable with hands or controllers, obeying real-room physics. Pressing **B** transfers the model to a laptop where it auto-opens in Blender. After editing, a Blender add-on uploads the revised model back to the headset, which respawns it in MR within seconds.

The system also supports an independent **Desktop → VR** push: any `.glb` file on the laptop can be sent into the headset via a CLI script or the Blender add-on, without going through the capture flow first.

---

## 2. High-Level Architecture

```
┌────────────────────────────────────────────────────────┐
│                 META QUEST 3 (Unity)                   │
│                                                        │
│   Passthrough  ──►  ObjectDetectionAgent (on-device)   │
│       │                       │                        │
│       ▼                       ▼                        │
│   CameraCapture  ──►  FalAiClient  ──►  FAL AI SAM-3D  │
│                              │              (cloud)    │
│                              ▼                         │
│                       GLBModelLoader                   │
│                              │                         │
│                              ▼                         │
│              Grabbable model in MR                     │
│                      ▲     │                           │
│                      │     │ B button                  │
│                      │     ▼                           │
│       VRModelReceiver◄── Firebase Realtime DB ──►───┐  │
│       (polls /desktop_to_vr) │   /models           │   │
└──────────────────────────────┼─────────────────────┼───┘
                               │                     │
                               ▼                     ▼
                  ┌─────────────────────┐   ┌───────────────────┐
                  │   Firebase Storage  │   │  receiver.py       │
                  │   (binary GLB host) │   │  auto-opens GLB    │
                  └─────────────────────┘   │  in Blender        │
                          ▲                 └───────────────────┘
                          │
                  ┌───────┴──────────────┐
                  │ blender_send_to_vr   │
                  │ (Blender N-panel)    │
                  └──────────────────────┘
```

---

## 3. Repository Layout

```
Cross Reality Project/
├── Assets/                          ← Unity project root
│   ├── CR project.unity             ← MAIN SCENE
│   ├── Scripts/
│   │   ├── Manager/SAM3DCaptureManager.cs   ← Orchestrator (A/B buttons)
│   │   ├── CameraCapture.cs                 ← Passthrough/screenshot capture
│   │   ├── FalAiClient.cs                   ← FAL AI HTTP client
│   │   ├── GLBModelLoader.cs                ← Downloads + parses GLBs
│   │   ├── Database/DatabaseManager.cs      ← Firebase RT-DB writes
│   │   ├── XR/VRModelReceiver.cs            ← Polls /desktop_to_vr
│   │   ├── XR/TransferZone.cs               ← (legacy) collision-based upload
│   │   ├── XR/ZoneTriggerReceiver.cs        ← TransferZone helper
│   │   ├── UI/CaptureUI.cs                  ← Spatial MR status panel
│   │   ├── Interaction/HandGrabbable.cs     ← Fallback grabber
│   │   ├── Models/FalAiResponse.cs          ← FAL AI JSON DTO
│   │   └── Editor/SceneSetupHelper.cs       ← One-click scene config
│   └── Materials/, Settings/, etc.
│
├── Packages/manifest.json           ← Unity package dependencies
├── ProjectSettings/                 ← Unity project config
│
├── laptop_receiver/                 ← Desktop side
│   ├── receiver.py                  ← Polls /models → opens GLB in Blender
│   ├── upload_to_vr.py              ← CLI: any .glb → Quest 3
│   ├── blender_send_to_vr.py        ← Blender add-on
│   └── requirements.txt             ← Pure stdlib (no installs needed)
│
├── REPORT.md                        ← Living project status report
├── KNOWLEDGE_TRANSFER.md            ← This file
└── SAM3D_SETUP_GUIDE.md             ← First-time FAL AI setup
```

---

## 4. Tech Stack & Versions

| Component | Version | Notes |
|---|---|---|
| Unity | 6000.3.8f1 (Unity 6) | URP + IL2CPP + Android ARM64 |
| Meta XR SDK | 201.0.0 | **Individual packages only** (see §10) |
| MR Utility Kit | 201.0.0 | Room mesh / EffectMesh |
| Interaction SDK | 201.0.0 | HandGrab + DistanceHandGrab |
| GLTFast | 6.10.1 | Runtime GLB parser |
| URP | 17.3.0 | Materials manually replaced at runtime |
| Meta OpenXR | 2.2.0 | XR backend (Oculus XR plugin removed) |
| FAL AI | SAM-3D endpoint | Hosted cloud inference |
| Firebase | RT-DB + Storage (Spark free tier) | Region: asia-southeast1 |
| Blender | 4.0+ / 5.0 | Verified on 5.0 |
| Python | 3.9+ | Pure stdlib (urllib, json, ssl) |

---

## 5. Hardware Requirements

- **Meta Quest 3** with Horizon OS **v85+** for `PassthroughCameraAccess` (raw camera feed).
  - On v84 and earlier, the system falls back to `ScreenCapture.CaptureScreenshotAsTexture()` — still works, just no raw passthrough access.
- Windows laptop (or Mac/Linux with minor path tweaks) running Python 3.9+ and Blender 4.0+.
- Both devices must reach the public internet to talk to Firebase + FAL AI.

---

## 6. First-Time Setup (Fresh Clone)

### 6.1 Get API credentials

1. **FAL AI key** — sign up at https://fal.ai/dashboard and create an API key.
2. **Firebase** — create a free Spark-plan project at https://console.firebase.google.com
   - Enable **Realtime Database** in the `asia-southeast1` region (or change `databaseUrl` in code).
   - Enable **Storage** with rules `allow read, write: if true;` (demo only — production should use Auth).
   - Note the Database URL and Database Secret (legacy auth token).
   - Note the Storage bucket name (looks like `your-project.firebasestorage.app`).

### 6.2 Configure secrets

Search the codebase for the placeholder `YOUR_FIREBASE_SECRET_HERE` and `YOUR_FAL_AI_API_KEY` and replace with real values. The Inspector values in `CR project.unity` will need updating too — open the scene in Unity and edit:
- `SAM3DManager` → `Fal Ai Api Key`
- `SAM3DManager / FalAiClient` → `Api Key`
- `SAM3DManager / DatabaseManager` → `Database Secret`
- `SAM3DManager / VRModelReceiver` → `Database Secret`

For the Python scripts, set environment variables (preferred):

```bash
# Windows (PowerShell)
$env:FIREBASE_SECRET = "your-secret-here"
$env:FIREBASE_URL    = "https://your-project-default-rtdb.firebaseio.com"
```

Or edit the placeholder constants directly in `upload_to_vr.py` and `blender_send_to_vr.py`.

### 6.3 Unity setup

1. Open the project in **Unity 6000.3.8f1**.
2. Let it import everything (~15 min the first time).
3. Open `Assets/CR project.unity`.
4. Connect Quest 3 via USB, enable developer mode, accept the trust prompt on the headset.
5. **File → Build Profiles → Android → Run Device:** select Quest 3.
6. **Build And Run** — first build takes ~10 min, subsequent ~2 min.

### 6.4 Laptop setup

```bash
cd laptop_receiver
python receiver.py          # leave this running — auto-opens new models in Blender
```

In Blender:
- `Edit → Preferences → Add-ons → Install` → pick `laptop_receiver/blender_send_to_vr.py`
- Enable "Cross Reality Transfer"
- `3D Viewport → N panel → VR tab` → Send to VR

---

## 7. The Bidirectional Loop, Step by Step

### A button (capture)
1. `SAM3DCaptureManager.Update()` detects `OVRInput.GetDown(captureButton)`
2. `StartCapture()` →
3. `CameraCapture.CaptureScreenshot()` → returns `Texture2D`
4. `FalAiClient.SendImageToFalAi(tex, prompt)` → POSTs base64 image + auto-detected label as prompt
5. FAL AI returns a `model_glb.url`
6. `GLBModelLoader.LoadGLBFromUrl(url, spawnPos, capturedTex)` →
   - `UnityWebRequest.Get(url)` → bytes
   - `GltfImport.LoadGltfBinary()` → parsed mesh
   - `gltfImport.InstantiateMainSceneAsync(container)` → in scene
   - URP material fix (shader graphs stripped on Android)
   - `BoxCollider` + `Rigidbody` (gravity on, ContinuousDynamic)
   - `Grabbable` + `HandGrabInteractable` + `GrabInteractable` + `DistanceHandGrabInteractable`
7. Stores `_lastGlbUrl` + `_lastObjectName` for the B-button transfer

### B button (transfer to laptop)
1. `SAM3DCaptureManager.TransferLastModel()` →
2. `DatabaseManager.SaveModel(name, glbUrl)` → POSTs `{name, glb_url, status, timestamp_ms}` to `/models.json`
3. `receiver.py` polling loop sees the new key
4. Downloads GLB to `downloaded_models/`
5. Launches Blender via `subprocess.Popen` with `--python-expr` to clear scene + import GLB

### Send to VR (Blender → headset)
1. User selects objects in Blender → N-panel → "Send to VR"
2. `bpy.ops.export_scene.gltf()` → temp `.glb`
3. PUT to Firebase Storage REST API → returns public download URL
4. POST `{name, glb_url, status, timestamp_ms, source:"desktop"}` to `/desktop_to_vr.json`
5. `VRModelReceiver` (3 s poll) sees the new key
6. Uses its **own dedicated** `GLBModelLoader` (separate from capture-flow loader) so the new model doesn't clobber capture models
7. Spawns model fanned to the right of the user with floor-snapping raycast

---

## 8. Key Design Decisions (and why)

### Why is there a separate `GLBModelLoader` for `VRModelReceiver`?
Because the capture flow calls `glbModelLoader.ClearCurrentModel()` whenever a new SAM-3D model is loaded. If desktop-sent models shared that loader, pressing A would destroy them. Two loaders = two independent lifetimes.

### Why is the FAL AI prompt auto-detected?
Asking the user to type or pick the object's label defeats the design goal of a single-gesture capture. The `ObjectDetectionAgent` building block runs continuously on-device and the highest-confidence non-device label is used implicitly. A blacklist (laptop, monitor, tv, keyboard…) prevents transfer-target devices from being used as capture subjects.

### Why URP material replacement at runtime?
GLTFast ships shader graphs which are **stripped** during IL2CPP+ARM64 builds for Android. The mesh would render bright pink (missing shader) without fixing this. We rebuild every material with `Universal Render Pipeline/Lit` and copy across `_BaseMap`/`_BaseColor`/etc. If no embedded texture exists, the captured passthrough frame is used as albedo (SAM-3D UVs are front-projected so this maps correctly).

### Why Firebase Storage and not a CDN / S3?
- Spark free tier (5 GB storage, 1 GB/day download) is enough for a BTP demo.
- One ecosystem — same project hosts the database + the files.
- Earlier iterations used `0x0.st` (failed on Windows SSL for large files) and `litterbox.catbox.moe` (worked but third-party). Storage is the cleanest solution.

### Why is `com.meta.xr.sdk.all` excluded?
Because it transitively includes `com.meta.xr.sdk.voice@201.0.0`, whose `ConduitManifestGenerationManager` crashes Unity 6 builds with `An item with the same key has already been added. Key: Assembly-CSharp`. There is no `#if !WIT_DISABLE_CONDUIT` guard around the offending code. **Fix:** depend on the individual sub-packages instead.

### Why is the database secret in plaintext?
**Demo only.** Firebase Auth tokens (or a server-side proxy) are the correct production solution. This is acceptable for BTP because:
- The Spark plan caps cost at zero.
- Storage rules accept all read/writes (also demo-only).
- No PII is stored.

---

## 9. Controller Reference

| Button | Action |
|---|---|
| **A** (right) | Capture passthrough → FAL AI → spawn model |
| **B** (right) | Upload last captured model to Firebase → opens in Blender |
| **Pinch / palm** | Grab any spawned model with hands |
| **Distance pinch** | Grab any spawned model from across the room |
| **Controller grip** | Grab any spawned model with controllers |

---

## 10. Common Pitfalls (Read Before Debugging)

| Problem | Cause | Fix |
|---|---|---|
| Unity 6 build fails with `ConduitManifestGenerationManager` error | `com.meta.xr.sdk.all` or `com.meta.xr.sdk.voice` is in manifest | Remove them; depend on individual Meta sub-packages |
| `OpenXR is not available` / object detection silent | OpenXR not enabled in Project Settings | Project Settings → XR Plug-in Management → Android tab → enable Meta OpenXR + Meta Quest feature set |
| Models spawn bright pink | GLTFast shader graphs stripped on Android | Already handled — see `FixMaterialsForURP()` in `GLBModelLoader.cs` |
| `[Errno 0] Error` uploading from Blender | Old code uploaded to `0x0.st` over SSL on Windows | Already fixed — uses Firebase Storage now |
| TransferZone cubes invisible | URP transparent material edge case | B-button is the primary transfer method; zones are a legacy fallback |
| Capture A button doesn't work | API key not set | Inspector → SAM3DManager → Fal Ai Api Key |
| Models from desktop spawn too far away | `spawnDistance` default too large | VRModelReceiver Inspector → Spawn Distance (default 1.0 m) |
| Firebase write fails with HTTP 401 | Database Secret wrong or rules block writes | Console → Realtime Database → Rules → check `.write` rule |

---

## 11. Files That Must Be in `.gitignore`

- `Library/` — Unity package cache (~5 GB, regenerated on import)
- `Temp/`, `Logs/`, `Build/`, `Builds/`, `obj/`, `MemoryCaptures/`
- `*.csproj`, `*.sln` — generated by Unity from `.asmdef` files
- `Assets/_Recovery/` — Unity scene auto-backup
- `downloaded_models/` — Python receiver download cache
- `.seen_keys`, `.seen_keys_vr` — receiver state files
- `__pycache__/`, `*.pyc`
- `.vs/`, `.idea/`, `.vscode/`

A ready-made `.gitignore` is in the repo root.

---

## 12. Where the Secrets Live (Make Sure They're Set)

| Where | Variable | What to set |
|---|---|---|
| `Assets/CR project.unity` (Inspector) | `SAM3DCaptureManager.falAiApiKey` | FAL AI key |
| `Assets/CR project.unity` (Inspector) | `FalAiClient.apiKey` | FAL AI key (same value) |
| `Assets/CR project.unity` (Inspector) | `DatabaseManager.databaseSecret` | Firebase DB secret |
| `Assets/CR project.unity` (Inspector) | `VRModelReceiver.databaseSecret` | Firebase DB secret |
| `laptop_receiver/*.py` | `FIREBASE_SECRET` env var | Firebase DB secret |
| `laptop_receiver/blender_send_to_vr.py` | `STORAGE_BUCKET` const | Firebase Storage bucket name |
| `laptop_receiver/upload_to_vr.py` | `STORAGE_BUCKET` const | Firebase Storage bucket name |

---

## 13. Known Limitations

1. **Cloud dependency** — capture-to-grab time is bounded by FAL AI inference (~20–30 s) and network conditions.
2. **Scale is non-metric** — SAM-3D output has no absolute scale. `modelScale` Inspector field is a manual workaround.
3. **Single-user** — no shared MR session yet.
4. **Public Storage rules** — anyone with the bucket name can read/write. Replace with Firebase Auth tokens for production.
5. **Object detection coverage** — limited to the building block's class set. Unrecognised objects fall back to the Inspector `capturePrompt` (default: "object").
6. **HzOS v84** — raw passthrough camera access unavailable; screenshot fallback used instead.

---

## 14. Suggested Next Steps

In rough priority order:

1. **Replace database secret + storage public rules with Firebase Auth tokens.** This is the only production blocker.
2. **Persistent spatial anchors** so digitised objects survive between sessions.
3. **Multi-user shared scenes** — same room, multiple headsets seeing the same models.
4. **On-device 3D generation** when a small enough model lands (current cloud round-trip dominates latency).
5. **Object scale calibration** — use depth or stereo cues to size models to the real object.
6. **Texture quality pass** — SAM-3D occasionally returns front-projected textures; back faces look stretched. A second-pass texture inpainting would help.

---

## 15. Credits & Contacts

- Project: BTP, [Your Institute]
- Owner: [Your name, email]
- Supervisor: [Supervisor name, email]
- Maintainer (post-handover): [whoever picks this up]

---

*Last updated: see git history.*
