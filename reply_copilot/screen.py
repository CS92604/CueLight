from __future__ import annotations

import io

MAX_EDGE = 1568  # longer edges are downscaled by the API anyway


def capture_png() -> bytes:
    """Screenshot of the primary monitor as PNG bytes."""
    import mss
    from PIL import Image

    with mss.mss() as sct:
        shot = sct.grab(sct.monitors[1])
        img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
    if max(img.size) > MAX_EDGE:
        img.thumbnail((MAX_EDGE, MAX_EDGE))
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()
