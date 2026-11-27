"""Model backends. Real inference is wired in after the live spike; the interface is fixed now."""
from __future__ import annotations

from typing import Iterable, Protocol


class FrameUpscaler(Protocol):
    def upscale_window(self, frames: list, scale: int) -> list:
        """Upscale a window of PNG-encoded frames (bytes), returning PNG bytes in the same order."""


class EngineUnavailable(Exception):
    pass


def window_size(engine: str) -> int:
    # SeedVR2 works on batches of 4n+1 frames with blended overlap.
    return 5 if engine == "seedvr2" else 8


def load_engine(model_id: str, models_dir: str) -> FrameUpscaler:
    """Load model weights from models_dir. Placeholder until the pinned weights exist."""
    raise EngineUnavailable(f"model '{model_id}' is not installed in {models_dir}")


def windows(items: list, size: int, overlap: int = 1) -> Iterable:
    step = max(1, size - overlap)
    i = 0
    while i < len(items):
        yield items[i : i + size]
        if i + size >= len(items):
            break
        i += step
