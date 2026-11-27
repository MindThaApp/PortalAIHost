"""stdio JSON-lines server. Writes only new files; deletes only its own .part temp files."""
from __future__ import annotations

import json
import os
import sys
import threading

from . import runtime
from .engines import EngineUnavailable, load_engine

MEMORY_GUARD_BYTES = 1 * 1024 * 1024 * 1024  # refuse a segment when less VRAM than this is free

_cancel = threading.Event()
_out_lock = threading.Lock()


def emit(event: dict) -> None:
    with _out_lock:
        sys.stdout.write(json.dumps(event, separators=(",", ":")) + "\n")
        sys.stdout.flush()


def _frames(folder: str) -> list:
    return sorted(n for n in os.listdir(folder) if n.lower().endswith(".png"))


def upscale_segment(cmd: dict) -> None:
    src, dst = cmd["input_dir"], cmd["output_dir"]
    seg = cmd.get("segment", 0)
    scale = int(cmd.get("scale", 2))
    if not os.path.isdir(src):
        emit({"event": "error", "code": "bad_input", "message": "input_dir missing"})
        return
    os.makedirs(dst, exist_ok=True)
    names = _frames(src)
    todo = [n for n in names if not os.path.exists(os.path.join(dst, n))]  # resume: keep finished frames
    free = runtime.free_vram_bytes()
    if todo and free is not None and free < MEMORY_GUARD_BYTES:
        emit({"event": "error", "code": "insufficient_memory", "message": f"only {free} bytes of VRAM free"})
        return
    engine = None
    if todo:
        try:
            engine = load_engine(str(cmd.get("model", "")), str(cmd.get("models_dir", "")))
        except EngineUnavailable as exc:
            emit({"event": "error", "code": "model_not_installed", "message": str(exc)})
            return
    total, done = len(names), len(names) - len(todo)
    emit({"event": "progress", "segment": seg, "frames_done": done, "frames_total": total})
    for name in todo:
        if _cancel.is_set():
            emit({"event": "cancelled", "segment": seg})
            return
        with open(os.path.join(src, name), "rb") as f:
            data = f.read()
        out = engine.upscale_window([data], scale)[0]
        final = os.path.join(dst, name)
        part = final + ".part"
        with open(part, "xb") as f:  # exclusive create: never overwrites
            f.write(out)
        os.replace(part, final)  # the final name was checked not to exist
        done += 1
        emit({"event": "progress", "segment": seg, "frames_done": done, "frames_total": total})
    emit({"event": "segment_done", "segment": seg, "output_dir": dst, "frames": total})


def cleanup_parts(folder: str) -> None:
    """Delete only half-written *.png.part files this worker created."""
    if not folder or not os.path.isdir(folder):
        return
    for n in os.listdir(folder):
        if n.endswith(".png.part"):
            try:
                os.remove(os.path.join(folder, n))
            except OSError:
                pass


def main() -> int:
    emit({"event": "ready", "protocol": 1})
    worker = None
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            cmd = json.loads(line)
        except ValueError:
            emit({"event": "error", "code": "bad_command", "message": "not json"})
            continue
        op = cmd.get("op")
        if op == "probe":
            emit({"event": "probe", **runtime.probe()})
        elif op == "upscale":
            if worker is not None and worker.is_alive():
                emit({"event": "error", "code": "busy", "message": "a segment is running"})
                continue
            _cancel.clear()

            def run(c=cmd):
                try:
                    upscale_segment(c)
                except Exception as exc:
                    emit({"event": "error", "code": "worker_failed", "message": f"{type(exc).__name__}: {exc}"})
                finally:
                    cleanup_parts(str(c.get("output_dir", "")))

            worker = threading.Thread(target=run, daemon=True)
            worker.start()
        elif op == "cancel":
            _cancel.set()
        elif op == "quit":
            _cancel.set()
            break
        else:
            emit({"event": "error", "code": "bad_command", "message": f"unknown op {op!r}"})
    if worker is not None:
        worker.join(timeout=30)
    return 0
