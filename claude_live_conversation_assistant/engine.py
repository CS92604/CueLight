from __future__ import annotations

import threading
import time
from typing import Callable

import anthropic

from . import screen
from .config import Config
from .conversation import Conversation
from .regions import Region
from .settings import Settings
from .suggest import MANUAL, SPEECH, TEXT, Suggester

# emit(kind, payload). Kinds: turn, suggest_start, chunk, suggest_end, status, error, region
Emit = Callable[[str, object], None]


def describe_error(exc: Exception) -> str:
    if isinstance(exc, anthropic.AuthenticationError):
        return "Claude rejected the credentials. Set ANTHROPIC_API_KEY (or run `ant auth login`)."
    if isinstance(exc, anthropic.RateLimitError):
        return "Claude rate limit hit; try again in a moment."
    if isinstance(exc, anthropic.APIConnectionError):
        return "Couldn't reach the Claude API. Check your connection."
    if isinstance(exc, anthropic.APIStatusError):
        return f"Claude API error ({exc.status_code}): {exc.message}"
    return f"{type(exc).__name__}: {exc}"


class Engine:
    """Holds the conversation and turns it into streamed Claude suggestions.

    Two things can trigger a suggestion: new speech from "Them", and the watched screen
    region changing. Requests are coalesced: a newer trigger supersedes one still waiting
    or streaming, and the channels that triggered are merged, so if speech and on-screen
    text change together Claude is asked for both a SAY and a TYPE reply.
    """

    def __init__(
        self,
        cfg: Config,
        suggester: Suggester,
        emit: Emit,
        settings: Settings | None = None,
        capture_region: Callable[[Region], bytes] = screen.capture_region_png,
        watcher_factory: Callable[..., threading.Thread] = screen.ScreenWatcher,
    ) -> None:
        self.cfg = cfg
        self.suggester = suggester
        self.emit = emit
        self.settings = settings or Settings()
        self.capture_region = capture_region
        self.watcher_factory = watcher_factory
        self.conversation = Conversation()
        self.auto = cfg.auto_suggest
        self.region: Region | None = None
        self._watcher = None

        self._cv = threading.Condition()
        self._request: tuple[float, str | None] | None = None
        self._queued_at = 0.0  # when the oldest not-yet-run request was made
        self._kinds: set[str] = set()
        self._gen = 0
        self._stopped = False
        self._thread = threading.Thread(target=self._worker, daemon=True, name="suggester")

    def start(self) -> None:
        self._thread.start()
        if self.cfg.region:
            self.set_region(self.cfg.region)

    def stop(self) -> None:
        self._stop_watcher()
        with self._cv:
            self._stopped = True
            self._gen += 1
            self._cv.notify_all()

    # -- watched region ---------------------------------------------------------------

    def set_region(self, region: Region | None) -> None:
        self._stop_watcher()
        self.region = region
        self.emit("region", region)
        if region is not None:
            self._watcher = self.watcher_factory(
                region,
                self.on_screen_change,
                interval_s=self.cfg.watch_interval_s,
                on_error=lambda msg: self.emit("error", msg),
            )
            self._watcher.start()

    def _stop_watcher(self) -> None:
        if self._watcher is not None:
            self._watcher.stop()
            self._watcher = None

    def on_screen_change(self) -> None:
        if self.auto and self.region is not None:
            self.request(kinds={TEXT}, delay=self.cfg.debounce_s)

    # -- inputs -------------------------------------------------------------------------

    def add_turn(self, speaker: str, text: str) -> None:
        text = text.strip()
        if not text:
            return
        self.conversation.add(speaker, text)
        self.emit("turn", (speaker, text))
        if speaker == "them" and self.auto and len(text.split()) >= self.cfg.auto_min_words:
            self.request(kinds={SPEECH}, delay=self.cfg.debounce_s)

    def request(self, hint: str | None = None, kinds: set[str] | None = None, delay: float = 0.0) -> None:
        """Ask for a suggestion. With no kinds this is a manual request (Claude picks the channels)."""
        with self._cv:
            self._gen += 1  # supersedes anything in flight
            self._kinds |= kinds or {MANUAL}
            if self._request is None:
                self._queued_at = time.monotonic()
            self._request = (time.monotonic() + delay, hint)
            self._cv.notify_all()

    def clear(self) -> None:
        with self._cv:
            self._gen += 1
            self._request = None
            self._kinds.clear()
        self.conversation.clear()

    # -- worker -------------------------------------------------------------------------

    def _worker(self) -> None:
        while True:
            with self._cv:
                while not self._stopped:
                    if self._request is not None:
                        now = time.monotonic()
                        wait = self._request[0] - now
                        if wait <= 0:
                            if self._screen_pending() and now - self._queued_at < self.cfg.merge_hold_s:
                                # The watched text is mid-change: wait for it to settle so the
                                # spoken and written replies arrive together.
                                self._cv.wait(0.2)
                                continue
                            break
                        self._cv.wait(wait)
                    else:
                        self._cv.wait()
                if self._stopped:
                    return
                _, hint = self._request  # type: ignore[misc]
                self._request = None
                kinds, self._kinds = self._kinds, set()
                gen = self._gen
            self._run(gen, kinds, hint)

    def _screen_pending(self) -> bool:
        return bool(getattr(self._watcher, "pending", False))

    def _current(self, gen: int) -> bool:
        with self._cv:
            return gen == self._gen and not self._stopped

    def _run(self, gen: int, kinds: set[str], hint: str | None) -> None:
        self.emit("suggest_start", None)
        region_png = None
        if self.region is not None and kinds != {SPEECH}:
            # Spoken-only triggers skip the image; any other trigger includes what's on screen now.
            try:
                region_png = self.capture_region(self.region)
            except Exception as exc:
                self.emit("status", f"Couldn't capture the watched area ({exc}); continuing without it.")
        chunks = self.suggester.stream(self.conversation, self.settings, kinds, hint, region_png)
        try:
            for chunk in chunks:
                if not self._current(gen):
                    with self._cv:
                        self._kinds |= kinds  # hand what this run owed to the one that replaced it
                    return  # superseded; the newer request emits its own suggest_start
                self.emit("chunk", chunk)
        except Exception as exc:
            self.emit("error", describe_error(exc))
        finally:
            chunks.close()
        self.emit("suggest_end", None)
