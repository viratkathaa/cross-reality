"""
blender_send_to_vr.py — Blender addon for Cross Reality Transfer.

Exports the selected objects (or full scene) as a GLB, uploads it to 0x0.st,
and writes the record to Firebase Realtime Database.

Install via Edit > Preferences > Add-ons > Install, then enable "Cross Reality Transfer".
Panel location: View3D > Sidebar (N) > VR tab
"""

bl_info = {
    "name": "Cross Reality Transfer",
    "author": "Cross Reality Project",
    "version": (1, 0, 0),
    "blender": (4, 0, 0),
    "location": "View3D > Sidebar > VR",
    "description": "Export selected objects as GLB and send to VR via Firebase",
    "category": "Import-Export",
}

import os
import json
import time
import uuid
import tempfile
import ssl
import urllib.request
import urllib.error

_SSL_CTX = ssl._create_unverified_context()

import bpy
from bpy.props import StringProperty
from bpy.types import PropertyGroup, Operator, Panel

# ─── Configuration ────────────────────────────────────────────────────────────
FIREBASE_URL = "https://crossreality-ec12f-default-rtdb.asia-southeast1.firebasedatabase.app"
FIREBASE_SECRET = os.environ.get("FIREBASE_SECRET", "YOUR_FIREBASE_SECRET_HERE")
FIREBASE_COLLECTION = "desktop_to_vr"
STORAGE_BUCKET = "crossreality-ec12f.firebasestorage.app"
STORAGE_BASE = f"https://firebasestorage.googleapis.com/v0/b/{STORAGE_BUCKET}/o"
USER_AGENT = "CrossReality-BlenderAddon/1.0"
# ──────────────────────────────────────────────────────────────────────────────


def _upload_file(file_path: str) -> str:
    """
    Upload a GLB to Firebase Storage via the REST media upload endpoint.
    Returns the public download URL.
    """
    import urllib.parse

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
        result = json.loads(resp.read().decode("utf-8"))

    # Public download URL (works when Storage rules allow read: if true)
    return f"{STORAGE_BASE}/{encoded_name}?alt=media"


def _write_to_firebase(name: str, glb_url: str) -> str:
    """
    POST a record to Firebase Realtime Database under /desktop_to_vr.json.
    Returns the Firebase-assigned record name/ID.
    Raises RuntimeError or urllib.error.URLError on failure.
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

    with urllib.request.urlopen(req, timeout=30, context=_SSL_CTX) as resp:
        if resp.status not in (200, 201):
            raise RuntimeError(f"Firebase returned HTTP {resp.status}")
        result = json.loads(resp.read().decode("utf-8"))

    return result.get("name", "<unknown>")


# ─── Property Group ────────────────────────────────────────────────────────────

class SendToVRProperties(PropertyGroup):
    model_name: StringProperty(
        name="Model Name",
        description="Name to register this model under in Firebase",
        default="my_model",
    )  # type: ignore
    status: StringProperty(
        name="Status",
        description="Result of the last send operation",
        default="",
    )  # type: ignore


# ─── Operator ─────────────────────────────────────────────────────────────────

class VR_OT_SendToVR(Operator):
    bl_idname = "vr.send_to_vr"
    bl_label = "Send to VR"
    bl_description = "Export selected objects as GLB and upload to VR via Firebase"
    bl_options = {"REGISTER"}

    def execute(self, context):
        props = context.scene.send_to_vr_props
        model_name = props.model_name.strip()

        if not model_name:
            props.status = "✗ Model name cannot be empty."
            self.report({"ERROR"}, "Model name cannot be empty.")
            return {"CANCELLED"}

        # ── Determine what to export ───────────────────────────────────────────
        selected_objects = context.selected_objects
        export_selection_only = len(selected_objects) > 0

        # ── Build temporary GLB path ───────────────────────────────────────────
        safe_name = "".join(c if c.isalnum() or c in "-_" else "_" for c in model_name)
        tmp_path = os.path.join(tempfile.gettempdir(), f"{safe_name}_{int(time.time())}.glb")

        # ── Export GLB ────────────────────────────────────────────────────────
        self.report({"INFO"}, f"Exporting GLB to {tmp_path} ...")
        try:
            bpy.ops.export_scene.gltf(
                filepath=tmp_path,
                export_format="GLB",
                use_selection=export_selection_only,
            )
        except Exception as exc:
            msg = f"✗ GLB export failed: {exc}"
            props.status = msg
            self.report({"ERROR"}, msg)
            return {"CANCELLED"}

        if not os.path.isfile(tmp_path):
            msg = "✗ GLB export produced no file."
            props.status = msg
            self.report({"ERROR"}, msg)
            return {"CANCELLED"}

        file_size_kb = os.path.getsize(tmp_path) / 1024
        self.report({"INFO"}, f"GLB exported ({file_size_kb:.1f} KB). Uploading to Firebase Storage ...")

        # ── Upload to 0x0.st ──────────────────────────────────────────────────
        try:
            glb_url = _upload_file(tmp_path)
        except (urllib.error.URLError, urllib.error.HTTPError, RuntimeError, OSError) as exc:
            msg = f"✗ Upload failed: {exc}"
            props.status = msg
            self.report({"ERROR"}, msg)
            _try_remove(tmp_path)
            return {"CANCELLED"}

        self.report({"INFO"}, f"Uploaded: {glb_url}. Writing to Firebase ...")

        # ── Write to Firebase ─────────────────────────────────────────────────
        try:
            record_id = _write_to_firebase(model_name, glb_url)
        except (urllib.error.URLError, urllib.error.HTTPError, RuntimeError, json.JSONDecodeError) as exc:
            msg = f"✗ Firebase write failed: {exc}"
            props.status = msg
            self.report({"ERROR"}, msg)
            _try_remove(tmp_path)
            return {"CANCELLED"}

        # ── Clean up temp file ────────────────────────────────────────────────
        _try_remove(tmp_path)

        # ── Success ───────────────────────────────────────────────────────────
        props.status = f"✓ Sent! ID: {record_id}"
        self.report({"INFO"}, f"Model '{model_name}' sent to VR. Record: {record_id}")
        return {"FINISHED"}


def _try_remove(path: str) -> None:
    """Remove a file, silently ignoring errors."""
    try:
        os.remove(path)
    except OSError:
        pass


# ─── Panel ────────────────────────────────────────────────────────────────────

class VR_PT_SendPanel(Panel):
    bl_label = "Cross Reality Transfer"
    bl_idname = "VR_PT_send_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "VR"

    def draw(self, context):
        layout = self.layout
        props = context.scene.send_to_vr_props

        layout.label(text="Cross Reality Transfer", icon="WORLD")
        layout.separator()

        layout.prop(props, "model_name")
        layout.separator()

        row = layout.row()
        row.scale_y = 1.4
        row.operator(
            VR_OT_SendToVR.bl_idname,
            text="Send to VR",
            icon="EXPORT",
        )

        if props.status:
            layout.separator()
            box = layout.box()
            # Choose icon based on success/failure prefix
            if props.status.startswith("✓"):
                icon = "CHECKMARK"
            elif props.status.startswith("✗"):
                icon = "ERROR"
            else:
                icon = "INFO"
            box.label(text=props.status, icon=icon)


# ─── Registration ─────────────────────────────────────────────────────────────

_classes = (
    SendToVRProperties,
    VR_OT_SendToVR,
    VR_PT_SendPanel,
)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.send_to_vr_props = bpy.props.PointerProperty(type=SendToVRProperties)


def unregister():
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    del bpy.types.Scene.send_to_vr_props


if __name__ == "__main__":
    register()
