"""Import-guarded access to the heavy dependencies, so a missing or broken install reports cleanly."""
from __future__ import annotations

import importlib


def load_torch():
    """Return (torch_module, None) or (None, reason)."""
    try:
        return importlib.import_module("torch"), None
    except Exception as exc:  # broken wheels raise more than ImportError
        return None, f"{type(exc).__name__}: {exc}"


def probe() -> dict:
    torch, err = load_torch()
    if torch is None:
        return {"ok": False, "reason": "torch_missing", "detail": err}
    try:
        if not torch.cuda.is_available():
            return {"ok": False, "reason": "no_cuda", "detail": "torch reports no CUDA device"}
        props = torch.cuda.get_device_properties(0)
        return {
            "ok": True,
            "device": props.name,
            "vram_bytes": int(props.total_memory),
            "torch": str(torch.__version__),
            "cuda": str(torch.version.cuda),
        }
    except Exception as exc:
        return {"ok": False, "reason": "cuda_error", "detail": f"{type(exc).__name__}: {exc}"}


def free_vram_bytes():
    torch, _ = load_torch()
    if torch is None:
        return None
    try:
        free, _total = torch.cuda.mem_get_info()
        return int(free)
    except Exception:
        return None
