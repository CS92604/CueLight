from __future__ import annotations

import threading
from dataclasses import dataclass

LABELS = {"them": "Them", "me": "Me"}


@dataclass
class Turn:
    speaker: str  # "them" | "me"
    text: str


class Conversation:
    """Thread-safe rolling transcript."""

    def __init__(self) -> None:
        self._turns: list[Turn] = []
        self._lock = threading.Lock()

    def add(self, speaker: str, text: str) -> None:
        text = text.strip()
        if not text:
            return
        with self._lock:
            # Merge consecutive turns by the same speaker (one person, several pauses).
            if self._turns and self._turns[-1].speaker == speaker:
                self._turns[-1].text += " " + text
            else:
                self._turns.append(Turn(speaker, text))

    def clear(self) -> None:
        with self._lock:
            self._turns.clear()

    def is_empty(self) -> bool:
        with self._lock:
            return not self._turns

    def render(self, max_chars: int) -> str:
        """Newest turns that fit in max_chars, oldest first. Whole turns only."""
        with self._lock:
            turns = list(self._turns)
        lines: list[str] = []
        used = 0
        dropped = False
        for turn in reversed(turns):
            line = f"{LABELS.get(turn.speaker, turn.speaker)}: {turn.text}"
            if lines and used + len(line) > max_chars:
                dropped = True
                break
            lines.append(line)
            used += len(line) + 1
        lines.reverse()
        if dropped:
            lines.insert(0, "[earlier conversation omitted]")
        return "\n".join(lines)
