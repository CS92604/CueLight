from __future__ import annotations

import queue
import tkinter as tk
from tkinter import ttk

from .config import Config
from .engine import Engine

BG = "#1e1e24"
FG = "#e8e8ec"
DIM = "#8a8a96"
ACCENT = "#7aa2f7"


class Overlay:
    """Small always-on-top window: live transcript on top, Claude's suggestions below."""

    def __init__(self, engine: Engine, cfg: Config, events: "queue.Queue[tuple[str, object]]") -> None:
        self.engine, self.cfg, self.events = engine, cfg, events
        self.root = root = tk.Tk()
        root.title("Reply Copilot")
        root.geometry("460x520+40+40")
        root.configure(bg=BG)
        root.attributes("-topmost", True)
        try:
            root.attributes("-alpha", 0.94)
        except tk.TclError:
            pass

        self.status = tk.StringVar(value="Starting…")
        tk.Label(root, textvariable=self.status, bg=BG, fg=DIM, anchor="w", font=("Segoe UI", 9)).pack(fill="x", padx=10, pady=(8, 2))

        self.transcript = self._text(root, height=8, font=("Segoe UI", 9), fg=DIM)
        self.transcript.pack(fill="x", padx=10)
        self.transcript.tag_config("them", foreground=FG)
        self.transcript.tag_config("me", foreground=ACCENT)

        tk.Label(root, text="Say something like:", bg=BG, fg=DIM, anchor="w", font=("Segoe UI", 9)).pack(fill="x", padx=10, pady=(10, 2))
        self.suggestions = self._text(root, height=12, font=("Segoe UI", 12), fg=FG)
        self.suggestions.pack(fill="both", expand=True, padx=10)

        row = tk.Frame(root, bg=BG)
        row.pack(fill="x", padx=10, pady=8)
        self.hint = ttk.Entry(row)
        self.hint.pack(side="left", fill="x", expand=True)
        self.hint.bind("<Return>", lambda _e: self._suggest())
        ttk.Button(row, text="Suggest", command=self._suggest).pack(side="left", padx=(6, 0))
        ttk.Button(row, text="Clear", command=self._clear).pack(side="left", padx=(6, 0))

        opts = tk.Frame(root, bg=BG)
        opts.pack(fill="x", padx=10, pady=(0, 8))
        self.auto = tk.BooleanVar(value=engine.auto)
        self.screen = tk.BooleanVar(value=engine.include_screen)
        for text, var, cb in (
            ("Auto-suggest", self.auto, self._toggle_auto),
            ("Include screenshot", self.screen, self._toggle_screen),
        ):
            tk.Checkbutton(
                opts, text=text, variable=var, command=cb, bg=BG, fg=FG, selectcolor=BG,
                activebackground=BG, activeforeground=FG, font=("Segoe UI", 9),
            ).pack(side="left", padx=(0, 12))

        root.protocol("WM_DELETE_WINDOW", root.destroy)
        root.after(50, self._drain)

    @staticmethod
    def _text(parent, height: int, font, fg: str) -> tk.Text:
        t = tk.Text(
            parent, height=height, wrap="word", bg="#26262e", fg=fg, font=font, relief="flat",
            padx=8, pady=6, insertbackground=fg, borderwidth=0, highlightthickness=0,
        )
        t.config(state="disabled")
        return t

    # -- actions --------------------------------------------------------------------

    def _suggest(self) -> None:
        hint = self.hint.get().strip() or None
        self.hint.delete(0, "end")
        self.engine.request(hint)

    def _clear(self) -> None:
        self.engine.clear()
        for w in (self.transcript, self.suggestions):
            self._set(w, "")

    def _toggle_auto(self) -> None:
        self.engine.auto = self.auto.get()

    def _toggle_screen(self) -> None:
        self.engine.include_screen = self.screen.get()

    # -- event pump -----------------------------------------------------------------

    @staticmethod
    def _set(widget: tk.Text, text: str) -> None:
        widget.config(state="normal")
        widget.delete("1.0", "end")
        widget.insert("end", text)
        widget.config(state="disabled")

    @staticmethod
    def _append(widget: tk.Text, text: str, tag: str | None = None) -> None:
        widget.config(state="normal")
        widget.insert("end", text, tag or ())
        widget.see("end")
        widget.config(state="disabled")

    def _drain(self) -> None:
        try:
            while True:
                kind, payload = self.events.get_nowait()
                if kind == "turn":
                    speaker, text = payload  # type: ignore[misc]
                    label = "Them" if speaker == "them" else "Me"
                    self._append(self.transcript, f"{label}: {text}\n", speaker)
                elif kind == "suggest_start":
                    self._set(self.suggestions, "")
                    self.status.set("Thinking…")
                elif kind == "chunk":
                    self._append(self.suggestions, str(payload))
                elif kind == "suggest_end":
                    self.status.set("Listening.")
                elif kind == "status":
                    self.status.set(str(payload))
                elif kind == "error":
                    self.status.set(f"Error: {payload}")
        except queue.Empty:
            pass
        self.root.after(50, self._drain)

    def run(self) -> None:
        self.root.mainloop()
