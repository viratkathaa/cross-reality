# Cross-Reality Object Digitization

> A bidirectional Mixed Reality ↔ Desktop pipeline on Meta Quest 3.
> Point at a real-world object, get a grabbable 3D model in seconds, edit it in Blender, and send it back to MR — all without opening a file dialog.

**BTP Project · Meta Quest 3 · Unity 6 · FAL AI SAM-3D**

---

## What It Does

1. **Look** at a real object through your Quest 3 passthrough.
2. **Press A** — an AI-generated 3D model appears in your hand within ~30 seconds.
3. **Grab, throw, place** the model with your hands. It obeys real-room physics.
4. **Press B** — it opens in Blender on your laptop.
5. **Edit it** in Blender, click "Send to VR" — it reappears in your headset.

The desktop can also independently push any `.glb` file into the headset.

---

## Quick Links

| Document | Purpose |
|---|---|
| [`KNOWLEDGE_TRANSFER.md`](KNOWLEDGE_TRANSFER.md) | **Start here.** Full onboarding for new contributors. |
| [`REPORT.md`](REPORT.md) | Living project status report. |
| [`SAM3D_SETUP_GUIDE.md`](SAM3D_SETUP_GUIDE.md) | First-time FAL AI account setup. |

---

## Tech Stack

- **Headset:** Meta Quest 3 (Horizon OS v85+)
- **Engine:** Unity 6000.3.8f1, URP, Android ARM64
- **Meta XR SDK:** 201.0.0 (individual packages — see KT doc for why)
- **3D Synthesis:** FAL AI SAM-3D (hosted)
- **Backend:** Firebase Realtime Database + Cloud Storage (free tier)
- **Desktop:** Python 3.9+ (pure stdlib), Blender 4.0+

---

## Setup

See [`KNOWLEDGE_TRANSFER.md`](KNOWLEDGE_TRANSFER.md) §6. The short version:

1. Get a FAL AI API key from https://fal.ai
2. Create a free Firebase project, enable Realtime Database + Storage
3. Replace `YOUR_FAL_AI_API_KEY` and `YOUR_FIREBASE_SECRET_HERE` placeholders in the codebase
4. Open in Unity 6000.3.8f1, build to Quest 3
5. Run `python laptop_receiver/receiver.py` on your laptop

---

## License

[Add your license here — MIT / Apache 2.0 / proprietary, as appropriate.]

---

## Acknowledgements

Built on top of:
- Meta XR SDK and Interaction SDK
- FAL.AI hosted SAM-3D
- Unity GLTFast
- Google Firebase

See [`KNOWLEDGE_TRANSFER.md`](KNOWLEDGE_TRANSFER.md) §15 for the full credits list.
