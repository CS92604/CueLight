from __future__ import annotations

import threading
import time
from typing import Callable

import anthropic

from . import screen
from .config import Config
from .conversation import Conversation
from .suggest import Suggester

# emit(kind, payload). Kinds: turn, suggest_start, chunk, suggest_end, status, error
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

    Requests are coalesced: a newer request (or a newer utterance in auto mode)
    supersedes one still waiting or streaming, so suggestions never lag behind the call.
    """

    def __init__(
        self,
        cfg: Config,
        suggester: Suggester,
        emit: Emit,
        screenshot: Callable[[], bytes] = screen.capture_png,
    ) -> None:
        self.cfg = cfg
        self.suggester = suggester
        self.emit = emit
        self.screenshot = screenshot
        self.conversation = Conversation()
        self.auto = cfg.auto_suggest
        self.include_screen = cfg.include_screen

        self._cv = threading.Condition()
        self._request: tuple[float, str | None] | None = None
        self._gen = 0
        self._stopped = False
        self._thread = threading.Thread(target=self._worker, daemon=True, name="suggester")

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        with self._cv:
            self._stopped = True
            self._gen += 1
            self._cv.notify_all()

    # -- inputs ---------------------------------------------------------------------

    def add_turn(self, speaker: str, text: str) -> None:
        text = text.strip()
        if not text:
            return
        self.conversation.add(speaker, text)
        self.emit("turn", (speaker, text))
        if speaker == "them" and self.auto and len(text.split()) >= self.cfg.auto_min_words:
            self.request(delay=self.cfg.debounce_s)

    def request(self, hint: str | None = None, delay: float = 0.0) -> None:
        with self._cv:
            self._gen += 1  # supersedes anything in flight
            self._request = (time.monotonic() + delay, hint)
            self._cv.notify_all()

    def clear(self) -> None:
        with self._cv:
            self._gen += 1
            self._request = None
        self.conversation.clear()

    # -- worker ---------------------------------------------------------------------

    def _worker(self) -> None:
        while True:
            with self._cv:
                while not self._stopped:
                    if self._request is not None:
                        wait = self._request[0] - time.monotonic()
                        if wait <= 0:
                            break
                        self._cv.wait(wait)
                    else:
                        self._cv.wait()
                if self._stopped:
                    return
                _, hint = self._request  # type: ignore[misc]
                self._request = None
                gen = self._gen
            self._run(gen, hint)

    def _current(self, gen: int) -> bool:
        with self._cv:
            return gen == self._gen and not self._stopped

    def _run(self, gen: int, hint: str | None) -> None:
        self.emit("suggest_start", None)
        shot = None
        if self.include_screen:
            try:
                shot = self.screenshot()
            except Exception as exc:
                self.emit("status", f"Screenshot failed ({exc}); continuing without it.")
        chunks = self.suggester.stream(self.conversation, hint, shot)
        try:
            for chunk in chunks:
                if not self._current(gen):
                    return  # superseded; a newer request will emit its own suggest_start
                self.emit("chunk", chunk)
        except Exception as exc:
            self.emit("error", describe_error(exc))
        finally:
            chunks.close()
        self.emit("suggest_end", None)
