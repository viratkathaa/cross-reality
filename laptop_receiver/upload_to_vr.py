"""
upload_to_vr.py — CLI tool to upload a GLB file to 0x0.st and register it in Firebase.

Usage:
    python upload_to_vr.py model.glb "model name"
"""

import sys
import os
import ssl
import json
import time
import uuid
import urllib.request
import urllib.parse
import urllib.error

_SSL_CTX = ssl._create_unverified_context()

# ─── Configuration ────────────────────────────────────────────────────────────
FIREBASE_URL = "https://crossreality-ec12f-default-rtdb.asia-southeast1.firebasedatabase.app"
FIREBASE_SECRET = os.environ.get("FIREBASE_SECRET", "YOUR_FIREBASE_SECRET_HERE")
FIREBASE_COLLECTION = "desktop_to_vr"
STORAGE_BUCKET = "crossreality-ec12f.firebasestorage.app"
STORAGE_BASE = f"https://firebasestorage.googleapis.com/v0/b/{STORAGE_BUCKET}/o"
USER_AGENT = "CrossReality-Uploader/1.0"
# ──────────────────────────────────────────────────────────────────────────────


def upload_file(file_path: str) -> str:
    """
    Upload a GLB to Firebase Storage via the REST media upload endpoint.
    Returns the public download URL.
    """
    file_name = os.path.basename(file_path)
    encoded_name = urllib.parse.quote(file_name, safe="")

    with open(file_path, "rb") as fh:
        file_bytes = fh.read()

    upload_url = f"{STORAGE_BASE}?name={encoded_name}&uploadType=media"

    req = urllib.request.Request(
        upload_url,
        data=file_bytes,
        method="POST",
        headers={
            "Content-Type": "application/octet-stream",
            "Content-Length": str(len(file_bytes)),
            "User-Agent": USER_AGENT,
        },
    )

    with urllib.request.urlopen(req, timeout=180, context=_SSL_CTX) as resp:
        if resp.status != 200:
            raise RuntimeError(f"Firebase Storage returned HTTP {resp.status}")

    return f"{STORAGE_BASE}/{encoded_name}?alt=media"


def write_to_firebase(name: str, glb_url: str) -> None:
    """
    POST a record to Firebase Realtime Database under /desktop_to_vr.json.
    """
    payload = {
        "name": name,
        "glb_url": glb_url,
        "timestamp_ms": int(time.time() * 1000),
        "status": "ready",
        "source": "desktop",
    }

    endpoint = (
        f"{FIREBASE_URL}/{FIREBASE_COLLECTION}.json"
        f"?auth={FIREBASE_SECRET}"
    )

    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        endpoint,
        data=data,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "Content-Length": str(len(data)),
            "User-Agent": USER_AGENT,
        },
    )

    with urllib.request.urlopen(req, timeout=30) as resp:
        if resp.status not in (200, 201):
            raise RuntimeError(f"Firebase returned HTTP {resp.status}")
        result = json.loads(resp.read().decode("utf-8"))

    return result


def main():
    # ── Argument validation ────────────────────────────────────────────────────
    if len(sys.argv) != 3:
        print("Usage: python upload_to_vr.py <model.glb> \"model name\"")
        print("Example: python upload_to_vr.py robot.glb \"Red Robot\"")
        sys.exit(1)

    glb_path = sys.argv[1]
    model_name = sys.argv[2]

    if not os.path.isfile(glb_path):
        print(f"[ERROR] File not found: {glb_path}")
        sys.exit(1)

    if not glb_path.lower().endswith(".glb"):
        print(f"[WARNING] File does not have a .glb extension: {glb_path}")

    file_size_kb = os.path.getsize(glb_path) / 1024
    print(f"[1/3] Preparing upload...")
    print(f"      File : {glb_path}")
    print(f"      Name : {model_name}")
    print(f"      Size : {file_size_kb:.1f} KB")

    # ── Upload to 0x0.st ───────────────────────────────────────────────────────
    print(f"\n[2/3] Uploading to Firebase Storage ...")
    try:
        glb_url = upload_file(glb_path)
    except urllib.error.HTTPError as exc:
        print(f"[ERROR] HTTP error during upload: {exc.code} {exc.reason}")
        sys.exit(1)
    except urllib.error.URLError as exc:
        print(f"[ERROR] Network error during upload: {exc.reason}")
        sys.exit(1)
    except OSError as exc:
        print(f"[ERROR] Could not read file: {exc}")
        sys.exit(1)
    except RuntimeError as exc:
        print(f"[ERROR] Upload failed: {exc}")
        sys.exit(1)

    print(f"      URL  : {glb_url}")

    # ── Write to Firebase ──────────────────────────────────────────────────────
    print(f"\n[3/3] Writing record to Firebase ({FIREBASE_COLLECTION}) ...")
    try:
        result = write_to_firebase(model_name, glb_url)
    except urllib.error.HTTPError as exc:
        print(f"[ERROR] HTTP error writing to Firebase: {exc.code} {exc.reason}")
        sys.exit(1)
    except urllib.error.URLError as exc:
        print(f"[ERROR] Network error writing to Firebase: {exc.reason}")
        sys.exit(1)
    except (json.JSONDecodeError, RuntimeError) as exc:
        print(f"[ERROR] Firebase write failed: {exc}")
        sys.exit(1)

    record_id = result.get("name", "<unknown>")
    print(f"      Record ID : {record_id}")

    print("\n[DONE] Model successfully sent to VR!")
    print(f"       Name : {model_name}")
    print(f"       URL  : {glb_url}")


if __name__ == "__main__":
    main()
