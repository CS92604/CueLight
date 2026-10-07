from __future__ import annotations

import queue
import threading
import warnings
from collections import deque
from dataclasses import dataclass

import numpy as np

SAMPLE_RATE = 16_000  # what Whisper wants


class Segmenter:
    """Energy-based voice activity detector that cuts a mono 16 kHz stream into utterances.

    Feed it arbitrary-sized chunks; it returns finished utterances (float32 arrays).
    """

    def __init__(
        self,
        sample_rate: int = SAMPLE_RATE,
        frame_ms: int = 30,
        silence_ms: int = 700,
        min_speech_ms: int = 300,
        max_utterance_s: float = 25.0,
        preroll_ms: int = 240,
        min_rms: float = 0.008,
        noise_factor: float = 3.0,
    ) -> None:
        self.frame = int(sample_rate * frame_ms / 1000)
        self.frame_ms = frame_ms
        self.silence_frames = max(1, silence_ms // frame_ms)
        self.min_speech_frames = max(1, min_speech_ms // frame_ms)
        self.max_frames = int(max_utterance_s * 1000 / frame_ms)
        self.min_rms = min_rms
        self.noise_factor = noise_factor
        self._noise = 0.0
        self._pending = np.zeros(0, dtype=np.float32)
        self._preroll: deque[np.ndarray] = deque(maxlen=max(1, preroll_ms // frame_ms))
        self._reset()

    def _reset(self) -> None:
        self._active: list[np.ndarray] = []
        self._speech = 0
        self._silence = 0

    def feed(self, samples: np.ndarray) -> list[np.ndarray]:
        out: list[np.ndarray] = []
        data = np.concatenate([self._pending, samples.astype(np.float32, copy=False)])
        n = len(data) // self.frame
        for i in range(n):
            frame = data[i * self.frame : (i + 1) * self.frame]
            utt = self._step(frame)
            if utt is not None:
                out.append(utt)
        self._pending = data[n * self.frame :]
        return out

    def flush(self) -> np.ndarray | None:
        """Return whatever speech is buffered (call at shutdown)."""
        if self._active and self._speech >= self.min_speech_frames:
            utt = np.concatenate(self._active)
            self._reset()
            return utt
        self._reset()
        return None

    def _step(self, frame: np.ndarray) -> np.ndarray | None:
        rms = float(np.sqrt(np.mean(frame * frame)))
        threshold = max(self.min_rms, self._noise * self.noise_factor)
        speaking = rms > threshold

        if not self._active:
            if speaking:
                self._active = list(self._preroll) + [frame]
                self._preroll.clear()
                self._speech, self._silence = 1, 0
            else:
                # Track the noise floor only while nobody is talking.
                self._noise = rms if self._noise == 0.0 else 0.95 * self._noise + 0.05 * rms
                self._preroll.append(frame)
            return None

        self._active.append(frame)
        if speaking:
            self._speech += 1
            self._silence = 0
        else:
            self._silence += 1
        if self._silence >= self.silence_frames or len(self._active) >= self.max_frames:
            utt = np.concatenate(self._active) if self._speech >= self.min_speech_frames else None
            self._reset()
            return utt
        return None


@dataclass
class Utterance:
    speaker: str  # "them" | "me"
    audio: np.ndarray


def _soundcard():
    import soundcard as sc  # imported lazily: needs an audio backend (PulseAudio / WASAPI / CoreAudio)

    # Windows emits this on every small buffer hiccup; it is harmless for speech.
    warnings.filterwarnings("ignore", message="data discontinuity")
    return sc


def pick_loopback(name: str | None = None):
    """The device that carries what the other people say (what your speakers play)."""
    sc = _soundcard()
    if name:
        for mic in sc.all_microphones(include_loopback=True):
            if name.lower() in mic.name.lower():
                return mic
        raise RuntimeError(f"No audio input matching {name!r}. Run with --list-devices.")
    return sc.get_microphone(id=str(sc.default_speaker().name), include_loopback=True)


def pick_mic(name: str | None = None):
    sc = _soundcard()
    if name:
        for mic in sc.all_microphones():
            if name.lower() in mic.name.lower():
                return mic
        raise RuntimeError(f"No microphone matching {name!r}. Run with --list-devices.")
    return sc.default_microphone()


def list_devices() -> str:
    sc = _soundcard()
    lines = ["Inputs (use with --loopback-device / --mic-device):"]
    for mic in sc.all_microphones(include_loopback=True):
        kind = "loopback" if getattr(mic, "isloopback", False) else "microphone"
        lines.append(f"  [{kind}] {mic.name}")
    return "\n".join(lines)


class Capture(threading.Thread):
    """Records one device and pushes finished utterances onto `out`."""

    def __init__(self, speaker: str, device, out: "queue.Queue[Utterance]", stop: threading.Event):
        super().__init__(daemon=True, name=f"capture-{speaker}")
        self.speaker, self.device, self.out, self.stop_event = speaker, device, out, stop
        self.error: Exception | None = None

    def run(self) -> None:
        seg = Segmenter()
        chunk = SAMPLE_RATE // 10  # 100 ms
        try:
            with self.device.recorder(samplerate=SAMPLE_RATE) as rec:
                while not self.stop_event.is_set():
                    data = rec.record(numframes=chunk)
                    mono = data.mean(axis=1) if data.ndim > 1 else data
                    for audio in seg.feed(mono):
                        self.out.put(Utterance(self.speaker, audio))
                tail = seg.flush()
                if tail is not None:
                    self.out.put(Utterance(self.speaker, tail))
        except Exception as exc:  # surfaced to the UI by the engine
            self.error = exc
            self.out.put(Utterance("error", np.zeros(0, dtype=np.float32)))
