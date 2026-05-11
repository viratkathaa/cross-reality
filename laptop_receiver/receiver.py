"""
Cross-Reality Model Receiver
=============================
Polls Firebase Realtime Database for new model entries written by the VR headset.
When a new entry with status="ready" appears it:
  1. Downloads the GLB file
  2. Saves it to a local folder
  3. Opens it in Blender (if installed)

Usage
-----
  pip install -r requirements.txt
  python receiver.py

Configuration
-------------
Set these three environment variables **or** edit the CONSTANTS block below:
  FIREBASE_URL     → e.g. https://your-project-default-rtdb.firebaseio.com
  FIREBASE_SECRET  → your database secret (leave blank if rules are open)
  BLENDER_PATH     → full path to the Blender executable

The script keeps a local file called `.seen_keys` next to itself so it never
re-opens the same model twice across restarts.
"""

import os
import sys
import json
import time
import hashlib
import subprocess
import tempfile
import urllib.request
import urllib.error
from pathlib import Path

# ── Configuration ──────────────────────────────────────────────────────────────

FIREBASE_URL    = os.environ.get("FIREBASE_URL",    "https://crossreality-ec12f-default-rtdb.asia-southeast1.firebasedatabase.app")
FIREBASE_SECRET = os.environ.get("FIREBASE_SECRET", "YOUR_FIREBASE_SECRET_HERE")
COLLECTION      = os.environ.get("FIREBASE_COLLECTION", "models")
POLL_INTERVAL   = float(os.environ.get("POLL_INTERVAL", "2.0"))  # seconds between polls

# Blender executable — common platform defaults
_blender_defaults = {
    "win32":  r"C:\Program Files\Blender Foundation\Blender 5.0\blender.exe",
    "darwin": "/Applications/Blender.app/Contents/MacOS/Blender",
    "linux":  "/usr/bin/blender",
}
BLENDER_PATH = os.environ.get(
    "BLENDER_PATH",
    _blender_defaults.get(sys.platform, "blender")
)

# Where GLB files are saved locally
DOWNLOAD_DIR = Path(os.environ.get("DOWNLOAD_DIR", Path(__file__).parent / "downloaded_models"))

# ── Helpers ────────────────────────────────────────────────────────────────────

SEEN_KEYS_FILE = Path(__file__).parent / ".seen_keys"


def load_seen_keys() -> set:
    if SEEN_KEYS_FILE.exists():
        return set(SEEN_KEYS_FILE.read_text().splitlines())
    return set()


def save_seen_key(key: str) -> None:
    with SEEN_KEYS_FILE.open("a") as f:
        f.write(key + "\n")


def firebase_get(path: str) -> dict | None:
    """GET <databaseUrl>/<path>.json and return parsed JSON or None on error."""
    url = f"{FIREBASE_URL.rstrip('/')}/{path}.json"
    if FIREBASE_SECRET:
        url += f"?auth={FIREBASE_SECRET}"
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            data = json.loads(resp.read().decode())
            return data
    except urllib.error.HTTPError as e:
        print(f"[receiver] HTTP {e.code} fetching Firebase: {e.reason}")
    except Exception as e:
        print(f"[receiver] Error fetching Firebase: {e}")
    return None


def download_glb(glb_url: str, dest_path: Path) -> bool:
    """Download a GLB file from URL to dest_path. Returns True on success."""
    print(f"[receiver] Downloading GLB → {dest_path.name}")
    try:
        urllib.request.urlretrieve(glb_url, dest_path)
        print(f"[receiver] Saved {dest_path.stat().st_size // 1024} KB to {dest_path}")
        return True
    except Exception as e:
        print(f"[receiver] Download failed: {e}")
        return False


def open_in_blender(glb_path: Path) -> None:
    """Launch Blender and import the GLB file."""
    blender_exe = Path(BLENDER_PATH)
    if not blender_exe.exists():
        print(f"[receiver] Blender not found at '{blender_exe}' — skipping auto-open.")
        print(f"           Set BLENDER_PATH env var or edit the CONSTANTS block in receiver.py")
        return

    # Inline Python script passed to Blender via --python-expr
    # Clears the default scene, imports the GLB, then centres the view.
    py_expr = (
        "import bpy;"
        "bpy.ops.object.select_all(action='SELECT');"
        "bpy.ops.object.delete();"
        f"bpy.ops.import_scene.gltf(filepath=r'{glb_path}');"
    )

    cmd = [str(blender_exe), "--python-expr", py_expr]
    print(f"[receiver] Launching Blender: {' '.join(cmd)}")
    try:
        # Launch detached so the receiver keeps polling
        subprocess.Popen(cmd, creationflags=subprocess.CREATE_NEW_CONSOLE if sys.platform == "win32" else 0)
        print("[receiver] Blender launched.")
    except Exception as e:
        print(f"[receiver] Could not launch Blender: {e}")


def process_entry(key: str, entry: dict) -> None:
    """Handle a single new model entry."""
    name    = entry.get("name", "unknown")
    glb_url = entry.get("glb_url", "")
    status  = entry.get("status", "")

    print(f"\n[receiver] ── New model ──────────────────────")
    print(f"           Key    : {key}")
    print(f"           Name   : {name}")
    print(f"           Status : {status}")
    print(f"           GLB    : {glb_url[:80]}...")

    if status != "ready":
        print(f"[receiver] Status is '{status}', not 'ready' — skipping.")
        return

    if not glb_url:
        print("[receiver] No GLB URL — skipping.")
        return

    DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)

    # Unique filename: name + hash of URL (avoids collisions on same object name)
    url_hash  = hashlib.md5(glb_url.encode()).hexdigest()[:8]
    safe_name = "".join(c if c.isalnum() or c in "-_" else "_" for c in name)
    dest_path = DOWNLOAD_DIR / f"{safe_name}_{url_hash}.glb"

    if download_glb(glb_url, dest_path):
        open_in_blender(dest_path)


# ── Main loop ──────────────────────────────────────────────────────────────────

def main() -> None:
    print("=" * 60)
    print("  Cross-Reality Model Receiver")
    print("=" * 60)
    print(f"  Firebase URL : {FIREBASE_URL}")
    print(f"  Collection   : {COLLECTION}")
    print(f"  Poll interval: {POLL_INTERVAL}s")
    print(f"  Blender path : {BLENDER_PATH}")
    print(f"  Download dir : {DOWNLOAD_DIR}")
    print("=" * 60)

    if not FIREBASE_URL.startswith("https://") or "YOUR-PROJECT" in FIREBASE_URL:
        print("\n[ERROR] FIREBASE_URL is not configured!")
        print("  Edit receiver.py or set the FIREBASE_URL environment variable.")
        sys.exit(1)

    seen_keys = load_seen_keys()
    print(f"[receiver] Loaded {len(seen_keys)} previously seen key(s).")
    print("[receiver] Listening for new models… (Ctrl+C to stop)\n")

    while True:
        try:
            data = firebase_get(COLLECTION)

            if data and isinstance(data, dict):
                new_keys = [k for k in data if k not in seen_keys]
                if new_keys:
                    print(f"[receiver] {len(new_keys)} new entry/entries found.")
                for key in new_keys:
                    entry = data[key]
                    if isinstance(entry, dict):
                        process_entry(key, entry)
                    seen_keys.add(key)
                    save_seen_key(key)
            # else: null / empty collection — nothing to do

        except KeyboardInterrupt:
            print("\n[receiver] Stopped.")
            break
        except Exception as e:
            print(f"[receiver] Unexpected error: {e}")

        time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
