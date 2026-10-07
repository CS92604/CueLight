from __future__ import annotations

import queue
import tkinter as tk
from tkinter import messagebox, ttk

from . import settings as settings_mod
from .config import Config
from .engine import Engine
from .regions import Region
from .selector import RegionOutline, select_region
from .suggest import parse_suggestions

BG = "#1e1e24"
PANEL = "#26262e"
FG = "#e8e8ec"
DIM = "#8a8a96"
SAY = "#7aa2f7"
TYPE = "#9ece6a"

HEADINGS = {"say": ("SAY  (out loud)", SAY), "type": ("TYPE  (reply to the text on screen)", TYPE)}


class Overlay:
    """Always-on-top window: live transcript, watched-area controls, and copyable suggestions."""

    def __init__(self, engine: Engine, cfg: Config, events: "queue.Queue[tuple[str, object]]") -> None:
        self.engine, self.cfg, self.events = engine, cfg, events
        self._raw = ""  # suggestion text streamed so far
        self.root = root = tk.Tk()
        root.title("Claude Live Conversation Assistant")
        root.geometry("500x660+40+40")
        root.configure(bg=BG)
        root.attributes("-topmost", True)
        try:
            root.attributes("-alpha", 0.95)
        except tk.TclError:
            pass
        self.outline = RegionOutline(root)

        self.status = tk.StringVar(value="Starting…")
        tk.Label(root, textvariable=self.status, bg=BG, fg=DIM, anchor="w", font=("Segoe UI", 9)).pack(fill="x", padx=10, pady=(8, 2))

        # Watched-area controls
        bar = tk.Frame(root, bg=BG)
        bar.pack(fill="x", padx=10, pady=(0, 6))
        ttk.Button(bar, text="Select text area", command=self.pick_region).pack(side="left")
        self.clear_btn = ttk.Button(bar, text="Clear area", command=self.clear_region, state="disabled")
        self.clear_btn.pack(side="left", padx=(6, 0))
        ttk.Button(bar, text="Settings", command=self.open_settings).pack(side="right")
        self.area_label = tk.StringVar(value="No text area selected")
        tk.Label(root, textvariable=self.area_label, bg=BG, fg=DIM, anchor="w", font=("Segoe UI", 9)).pack(fill="x", padx=10)

        self.transcript = self._text(root, height=7, font=("Segoe UI", 9), fg=DIM)
        self.transcript.pack(fill="x", padx=10, pady=(6, 0))
        self.transcript.tag_config("them", foreground=FG)
        self.transcript.tag_config("me", foreground=SAY)

        tk.Label(root, text="What to say / type:", bg=BG, fg=DIM, anchor="w", font=("Segoe UI", 9)).pack(fill="x", padx=10, pady=(10, 2))
        self.suggestions = self._text(root, height=14, font=("Segoe UI", 11), fg=FG)
        self.suggestions.pack(fill="both", expand=True, padx=10)
        for kind, (_, color) in HEADINGS.items():
            self.suggestions.tag_config(f"h_{kind}", foreground=color, font=("Segoe UI", 9, "bold"), spacing1=8, spacing3=2)
        self.suggestions.tag_config("opt", spacing3=6, lmargin1=4, lmargin2=4)
        self.suggestions.tag_config("note", foreground=DIM)

        row = tk.Frame(root, bg=BG)
        row.pack(fill="x", padx=10, pady=8)
        self.hint = ttk.Entry(row)
        self.hint.pack(side="left", fill="x", expand=True)
        self.hint.bind("<Return>", lambda _e: self._suggest())
        ttk.Button(row, text="Suggest", command=self._suggest).pack(side="left", padx=(6, 0))
        ttk.Button(row, text="Clear chat", command=self._clear).pack(side="left", padx=(6, 0))

        self.auto = tk.BooleanVar(value=engine.auto)
        tk.Checkbutton(
            root, text="Auto-suggest when speech or the text area changes", variable=self.auto,
            command=lambda: setattr(self.engine, "auto", self.auto.get()), bg=BG, fg=FG,
            selectcolor=BG, activebackground=BG, activeforeground=FG, font=("Segoe UI", 9),
        ).pack(anchor="w", padx=10, pady=(0, 8))

        root.protocol("WM_DELETE_WINDOW", root.destroy)
        root.after(50, self._drain)
        if cfg.region:
            root.after(200, lambda: self._show_outline_for(cfg.region))

    # -- helpers ------------------------------------------------------------------------

    @staticmethod
    def _text(parent, height: int, font, fg: str) -> tk.Text:
        t = tk.Text(
            parent, height=height, wrap="word", bg=PANEL, fg=fg, font=font, relief="flat",
            padx=8, pady=6, insertbackground=fg, borderwidth=0, highlightthickness=0,
        )
        t.config(state="disabled")
        return t

    def _copy(self, text: str) -> None:
        self.root.clipboard_clear()
        self.root.clipboard_append(text)
        self.root.update()  # keep the clipboard after the app exits on some platforms
        self.status.set("Copied to clipboard.")

    # -- watched area -------------------------------------------------------------------

    def pick_region(self) -> None:
        try:
            picked = select_region(self.root)
        except Exception as exc:
            messagebox.showerror("Select text area", f"Couldn't capture the screen: {exc}")
            return
        if picked is None:
            return
        region, rect = picked
        self.outline.show(rect)
        self.engine.set_region(region)

    def clear_region(self) -> None:
        self.outline.hide()
        self.engine.set_region(None)

    def _show_outline_for(self, region: Region) -> None:
        """Outline for a region given on the command line (physical px -> Tk coords)."""
        try:
            import mss

            with mss.mss() as sct:
                scale = sct.monitors[1]["width"] / self.root.winfo_screenwidth()
        except Exception:
            scale = 1.0
        self.outline.show(
            (round(region.left / scale), round(region.top / scale), round(region.width / scale), round(region.height / scale))
        )

    # -- settings -----------------------------------------------------------------------

    def open_settings(self) -> None:
        cur = self.engine.settings.normalized()
        dlg = tk.Toplevel(self.root)
        dlg.title("Settings")
        dlg.configure(bg=BG)
        dlg.attributes("-topmost", True)
        dlg.transient(self.root)
        dlg.geometry("+%d+%d" % (self.root.winfo_rootx() + 30, self.root.winfo_rooty() + 30))

        frame = ttk.Frame(dlg, padding=12)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(1, weight=1)

        rows = [
            ("Professionalism", "professionalism", "How formal the replies sound"),
            ("Proficiency", "proficiency", "How advanced the wording is (pick what you can say comfortably)"),
            ("Tone", "tone", "Overall feel of the replies"),
            ("Length", "length", "How much to say per option"),
        ]
        vars_: dict[str, tk.StringVar] = {}
        for i, (label, key, tip) in enumerate(rows):
            ttk.Label(frame, text=label).grid(row=2 * i, column=0, sticky="w", pady=(6, 0))
            var = tk.StringVar(value=getattr(cur, key))
            ttk.Combobox(frame, textvariable=var, values=list(settings_mod.CHOICES[key]), state="readonly", width=18).grid(
                row=2 * i, column=1, sticky="ew", pady=(6, 0), padx=(10, 0)
            )
            ttk.Label(frame, text=tip, foreground="#777").grid(row=2 * i + 1, column=1, sticky="w", padx=(10, 0))
            vars_[key] = var

        r = len(rows) * 2
        ttk.Label(frame, text="Options per section").grid(row=r, column=0, sticky="w", pady=(10, 0))
        opts = tk.IntVar(value=cur.options)
        ttk.Spinbox(frame, from_=1, to=3, textvariable=opts, width=4).grid(row=r, column=1, sticky="w", padx=(10, 0), pady=(10, 0))

        ttk.Label(frame, text="Reply language").grid(row=r + 1, column=0, sticky="w", pady=(10, 0))
        lang = tk.StringVar(value=cur.reply_language)
        ttk.Entry(frame, textvariable=lang).grid(row=r + 1, column=1, sticky="ew", padx=(10, 0), pady=(10, 0))
        ttk.Label(frame, text="Leave empty to match the other person", foreground="#777").grid(row=r + 2, column=1, sticky="w", padx=(10, 0))

        ttk.Label(frame, text="Extra instructions").grid(row=r + 3, column=0, sticky="nw", pady=(10, 0))
        custom = tk.Text(frame, height=4, width=36, wrap="word")
        custom.insert("1.0", cur.custom)
        custom.grid(row=r + 3, column=1, sticky="ew", padx=(10, 0), pady=(10, 0))
        ttk.Label(frame, text='e.g. "I\'m a junior analyst", "never use the word synergy"', foreground="#777").grid(
            row=r + 4, column=1, sticky="w", padx=(10, 0)
        )

        def save() -> None:
            new = settings_mod.Settings(
                professionalism=vars_["professionalism"].get(),
                proficiency=vars_["proficiency"].get(),
                tone=vars_["tone"].get(),
                length=vars_["length"].get(),
                options=opts.get(),
                reply_language=lang.get(),
                custom=custom.get("1.0", "end").strip(),
            ).normalized()
            self.engine.settings = new
            try:
                settings_mod.save(new)
                self.status.set("Settings saved.")
            except OSError as exc:
                self.status.set(f"Settings applied, but couldn't be saved: {exc}")
            dlg.destroy()

        buttons = ttk.Frame(frame)
        buttons.grid(row=r + 5, column=0, columnspan=2, sticky="e", pady=(14, 0))
        ttk.Button(buttons, text="Cancel", command=dlg.destroy).pack(side="right")
        ttk.Button(buttons, text="Save", command=save).pack(side="right", padx=(0, 6))

    # -- actions ------------------------------------------------------------------------

    def _suggest(self) -> None:
        hint = self.hint.get().strip() or None
        self.hint.delete(0, "end")
        self.engine.request(hint)

    def _clear(self) -> None:
        self.engine.clear()
        self._raw = ""
        for w in (self.transcript, self.suggestions):
            self._set(w, "")

    # -- rendering ----------------------------------------------------------------------

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

    def _render_final(self) -> None:
        """Replace the streamed text with headed sections whose options can be copied."""
        sections = parse_suggestions(self._raw)
        w = self.suggestions
        w.config(state="normal")
        w.delete("1.0", "end")
        if not sections:
            w.insert("end", self._raw.strip(), "note")
        for sec in sections:
            if sec.kind == "note":
                for text in sec.options:
                    w.insert("end", text + "\n", "note")
                continue
            title, _ = HEADINGS[sec.kind]
            w.insert("end", title + "\n", f"h_{sec.kind}")
            for i, text in enumerate(sec.options):
                tag = f"opt_{sec.kind}_{i}"
                w.insert("end", text + "  ", ("opt", tag))
                w.tag_bind(tag, "<Button-1>", lambda _e, t=text: self._copy(t))
                w.tag_bind(tag, "<Enter>", lambda _e: w.config(cursor="hand2"))
                w.tag_bind(tag, "<Leave>", lambda _e: w.config(cursor=""))
                btn = ttk.Button(w, text="Copy", width=6, command=lambda t=text: self._copy(t))
                w.window_create("end", window=btn)
                w.insert("end", "\n")
        w.config(state="disabled")

    def _drain(self) -> None:
        try:
            while True:
                kind, payload = self.events.get_nowait()
                if kind == "turn":
                    speaker, text = payload  # type: ignore[misc]
                    label = "Them" if speaker == "them" else "Me"
                    self._append(self.transcript, f"{label}: {text}\n", speaker)
                elif kind == "suggest_start":
                    self._raw = ""
                    self._set(self.suggestions, "")
                    self.status.set("Thinking…")
                elif kind == "chunk":
                    self._raw += str(payload)
                    self._append(self.suggestions, str(payload))
                elif kind == "suggest_end":
                    self._render_final()
                    self.status.set("Listening.")
                elif kind == "region":
                    region = payload
                    self.clear_btn.config(state="normal" if region else "disabled")
                    self.area_label.set(f"Watching text area: {region}" if region else "No text area selected")
                elif kind == "status":
                    self.status.set(str(payload))
                elif kind == "error":
                    self.status.set(f"Error: {payload}")
        except queue.Empty:
            pass
        self.root.after(50, self._drain)

    def run(self) -> None:
        self.root.mainloop()
