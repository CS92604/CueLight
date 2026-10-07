from __future__ import annotations

import io
import threading
from typing import Callable

import numpy as np

from .regions import ChangeDetector, Region, downsample_gray

MAX_EDGE = 1568  # longer edges are downscaled by the API anyway


def virtual_screen_png_and_box():
    """Screenshot of every monitor plus its (left, top, width, height) in physical pixels."""
    import mss
    from PIL import Image

    with mss.mss() as sct:
        mon = sct.monitors[0]  # the union of all monitors
        shot = sct.grab(mon)
        img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
    return img, (mon["left"], mon["top"], mon["width"], mon["height"])


def capture_region_png(region: Region) -> bytes:
    """PNG of the watched region, scaled down if it is larger than the API will read."""
    import mss
    from PIL import Image

    with mss.mss() as sct:
        shot = sct.grab(region.as_mss())
        img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
    if max(img.size) > MAX_EDGE:
        img.thumbnail((MAX_EDGE, MAX_EDGE))
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


class ScreenWatcher(threading.Thread):
    """Samples a screen region and calls on_change() when it has changed and settled."""

    def __init__(
        self,
        region: Region,
        on_change: Callable[[], None],
        interval_s: float = 0.5,
        on_error: Callable[[str], None] | None = None,
        detector: ChangeDetector | None = None,
    ) -> None:
        super().__init__(daemon=True, name="screen-watcher")
        self.region, self.on_change, self.interval_s = region, on_change, interval_s
        self.on_error = on_error
        self.detector = detector or ChangeDetector()
        self._halt = threading.Event()

    @property
    def pending(self) -> bool:
        return self.detector.pending

    def stop(self) -> None:
        self._halt.set()

    def run(self) -> None:
        import mss  # mss handles are per-thread, so create it here

        try:
            with mss.mss() as sct:
                box = self.region.as_mss()
                while not self._halt.is_set():
                    shot = sct.grab(box)
                    frame = downsample_gray(np.frombuffer(shot.bgra, dtype=np.uint8).reshape(shot.height, shot.width, 4))
                    if self.detector.update(frame):
                        self.on_change()
                    self._halt.wait(self.interval_s)
        except Exception as exc:
            if self.on_error:
                self.on_error(f"Screen watching stopped: {exc}")
