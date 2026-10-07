from __future__ import annotations

from dataclasses import dataclass
from time import monotonic

import numpy as np


@dataclass(frozen=True)
class Region:
    """A rectangle in physical screen pixels (the coordinate space mss captures in)."""

    left: int
    top: int
    width: int
    height: int

    def as_mss(self) -> dict:
        return {"left": self.left, "top": self.top, "width": self.width, "height": self.height}

    def __str__(self) -> str:
        return f"{self.width}x{self.height} at ({self.left}, {self.top})"

    @staticmethod
    def parse(text: str) -> "Region":
        """'left,top,width,height'"""
        try:
            left, top, width, height = (int(p) for p in text.split(","))
        except ValueError:
            raise ValueError("expected left,top,width,height (four integers)") from None
        if width < 1 or height < 1:
            raise ValueError("width and height must be positive")
        return Region(left, top, width, height)


def drag_to_region(
    origin: tuple[int, int],
    scale: tuple[float, float],
    p0: tuple[float, float],
    p1: tuple[float, float],
    min_size: int = 16,
) -> Region | None:
    """Turn a drag on the selection canvas into physical screen pixels.

    origin: physical screen position of the canvas's top-left corner.
    scale: physical pixels per canvas unit (differs from 1 on high-DPI displays).
    """
    x0, x1 = sorted((p0[0], p1[0]))
    y0, y1 = sorted((p0[1], p1[1]))
    region = Region(
        left=origin[0] + round(x0 * scale[0]),
        top=origin[1] + round(y0 * scale[1]),
        width=round((x1 - x0) * scale[0]),
        height=round((y1 - y0) * scale[1]),
    )
    return region if region.width >= min_size and region.height >= min_size else None


def downsample_gray(bgra: np.ndarray, target_width: int = 160) -> np.ndarray:
    """(h, w, 4) BGRA -> small float32 grayscale, area-averaged so thin text survives."""
    gray = bgra[..., 2] * 0.299 + bgra[..., 1] * 0.587 + bgra[..., 0] * 0.114
    h, w = gray.shape
    step = max(1, w // target_width)
    h2, w2 = (h // step) * step, (w // step) * step
    if h2 == 0 or w2 == 0:
        return gray.astype(np.float32)
    return gray[:h2, :w2].reshape(h2 // step, step, w2 // step, step).mean(axis=(1, 3)).astype(np.float32)


class ChangeDetector:
    """Decides when the watched region has *meaningfully changed and settled*.

    Feed it a small grayscale frame every sample. update() returns True once per change,
    after the content has stopped moving for `settle_s` (so a message that is still being
    typed isn't read half-finished). Blinking cursors and similar tiny differences are
    ignored; continuous change (video) fires at most every `min_interval_s`.
    """

    def __init__(
        self,
        pixel_delta: float = 24.0,
        changed_fraction: float = 0.002,
        settle_s: float = 1.0,
        max_wait_s: float = 8.0,
        min_interval_s: float = 3.0,
    ) -> None:
        self.pixel_delta = pixel_delta
        self.changed_fraction = changed_fraction
        self.settle_s = settle_s
        self.max_wait_s = max_wait_s
        self.min_interval_s = min_interval_s
        self._baseline: np.ndarray | None = None
        self._prev: np.ndarray | None = None
        self._first_change: float | None = None
        self._last_motion = 0.0
        self._last_fire = -1e9

    @property
    def pending(self) -> bool:
        """True while the region differs from the last reported state but hasn't settled yet."""
        return self._first_change is not None

    def reset(self) -> None:
        self._baseline = self._prev = None
        self._first_change = None

    def _differs(self, a: np.ndarray, b: np.ndarray) -> bool:
        if a.shape != b.shape:
            return True
        return float(np.mean(np.abs(a - b) > self.pixel_delta)) > self.changed_fraction

    def update(self, frame: np.ndarray, now: float | None = None) -> bool:
        now = monotonic() if now is None else now
        if self._baseline is None or self._baseline.shape != frame.shape:
            self._baseline = self._prev = frame
            self._first_change = None
            return False

        moved = self._differs(frame, self._prev)  # type: ignore[arg-type]
        self._prev = frame
        if not self._differs(frame, self._baseline):
            self._first_change = None  # back to what we last reported (or never really changed)
            return False

        if self._first_change is None:
            self._first_change = now
            self._last_motion = now
        elif moved:
            self._last_motion = now

        settled = now - self._last_motion >= self.settle_s
        too_long = now - self._first_change >= self.max_wait_s
        if (settled or too_long) and now - self._last_fire >= self.min_interval_s:
            self._baseline = frame
            self._first_change = None
            self._last_fire = now
            return True
        return False
