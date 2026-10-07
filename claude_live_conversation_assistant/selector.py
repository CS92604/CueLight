from __future__ import annotations

import time
import tkinter as tk

from . import screen
from .regions import Region, drag_to_region

Rect = tuple[int, int, int, int]  # x, y, width, height in Tk screen coordinates


def select_region(root: tk.Tk) -> tuple[Region, Rect] | None:
    """Snipping-tool style picker: freeze the screen, let the user drag a box over it.

    Returns the region in physical pixels (for capture) and the same box in Tk screen
    coordinates (for drawing the outline), or None if cancelled.
    """
    from PIL import ImageTk

    root.withdraw()
    root.update()
    time.sleep(0.3)  # let our own window disappear before the screenshot
    try:
        img, (left, top, width, height) = screen.virtual_screen_png_and_box()
    except Exception:
        root.deiconify()
        raise

    win = tk.Toplevel(root)
    win.overrideredirect(True)
    win.attributes("-topmost", True)
    win.geometry(f"{width}x{height}+{left}+{top}")
    win.update_idletasks()
    cw, ch = win.winfo_width(), win.winfo_height()
    scale = (width / cw, height / ch)  # physical pixels per Tk unit (≠ 1 on some HiDPI setups)

    canvas = tk.Canvas(win, width=cw, height=ch, highlightthickness=0, cursor="crosshair")
    canvas.pack(fill="both", expand=True)
    photo = ImageTk.PhotoImage(img.resize((cw, ch)))
    canvas.create_image(0, 0, image=photo, anchor="nw")
    canvas.create_rectangle(cw // 2 - 260, 14, cw // 2 + 260, 52, fill="#111111", outline="")
    canvas.create_text(
        cw // 2, 33, fill="white", font=("Segoe UI", 12),
        text="Drag over the text to watch  •  Esc or right-click to cancel",
    )

    state: dict = {"start": None, "rect": None, "result": None}

    def press(e):
        state["start"] = (e.x, e.y)
        state["rect"] = canvas.create_rectangle(e.x, e.y, e.x, e.y, outline="#ff4d4d", width=2)

    def drag(e):
        if state["start"]:
            canvas.coords(state["rect"], *state["start"], e.x, e.y)

    def release(e):
        if not state["start"]:
            return
        region = drag_to_region((left, top), scale, state["start"], (e.x, e.y))
        if region is None:  # too small: treat as a stray click and let them retry
            canvas.delete(state["rect"])
            state["start"] = None
            return
        x0, x1 = sorted((state["start"][0], e.x))
        y0, y1 = sorted((state["start"][1], e.y))
        ox, oy = win.winfo_rootx(), win.winfo_rooty()
        state["result"] = (region, (ox + x0, oy + y0, x1 - x0, y1 - y0))
        win.destroy()

    def cancel(_e=None):
        win.destroy()

    canvas.bind("<ButtonPress-1>", press)
    canvas.bind("<B1-Motion>", drag)
    canvas.bind("<ButtonRelease-1>", release)
    canvas.bind("<ButtonPress-3>", cancel)
    win.bind("<Escape>", cancel)
    win.focus_force()
    win.grab_set()
    win.wait_window()
    root.deiconify()
    root.attributes("-topmost", True)
    return state["result"]


class RegionOutline:
    """Four thin always-on-top bars just outside the watched box, so the box stays visible
    without ever covering (or being captured with) the text inside it."""

    def __init__(self, root: tk.Tk, color: str = "#ff4d4d", thickness: int = 3) -> None:
        self.root, self.color, self.t = root, color, thickness
        self._bars: list[tk.Toplevel] = []

    def show(self, rect: Rect) -> None:
        self.hide()
        x, y, w, h = rect
        t = self.t
        for gx, gy, gw, gh in (
            (x - t, y - t, w + 2 * t, t),  # top
            (x - t, y + h, w + 2 * t, t),  # bottom
            (x - t, y, t, h),  # left
            (x + w, y, t, h),  # right
        ):
            bar = tk.Toplevel(self.root)
            bar.overrideredirect(True)
            bar.attributes("-topmost", True)
            bar.configure(bg=self.color)
            bar.geometry(f"{gw}x{gh}+{gx}+{gy}")
            self._bars.append(bar)

    def hide(self) -> None:
        for bar in self._bars:
            bar.destroy()
        self._bars.clear()
