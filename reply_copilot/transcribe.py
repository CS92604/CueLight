from __future__ import annotations

import numpy as np


class Transcriber:
    """Local speech-to-text via faster-whisper (downloads the model on first use)."""

    def __init__(self, model_size: str = "base.en", language: str | None = "en", compute_type: str = "int8"):
        from faster_whisper import WhisperModel

        self.language = language
        self.model = WhisperModel(model_size, device="auto", compute_type=compute_type)

    def transcribe(self, audio: np.ndarray) -> str:
        segments, _ = self.model.transcribe(
            audio,
            language=self.language,
            beam_size=1,
            condition_on_previous_text=False,
        )
        kept = []
        for seg in segments:
            # Whisper hallucinates on noise; drop segments it itself doubts.
            if seg.no_speech_prob > 0.6 and seg.avg_logprob < -1.0:
                continue
            kept.append(seg.text.strip())
        return " ".join(t for t in kept if t)
