from __future__ import annotations

import os
from dataclasses import dataclass


@dataclass
class Config:
    # Claude
    model: str = os.environ.get("COPILOT_MODEL", "claude-opus-5-5")
    effort: str = os.environ.get("COPILOT_EFFORT", "low")  # low | medium | high | xhigh | max
    max_tokens: int = 2048  # thinking tokens count toward this
    fallbacks: bool = os.environ.get("COPILOT_FALLBACKS", "1") != "0"

    # What the user wants help with ("I'm in a job interview for ...")
    context: str = ""

    # Speech to text (faster-whisper, runs locally)
    whisper_model: str = "base.en"
    language: str = "en"
    compute_type: str = "int8"

    # Audio
    loopback_device: str | None = None  # substring of a device name; default = default speaker loopback
    mic_device: str | None = None
    use_mic: bool = False  # also transcribe your own microphone as "Me"

    # Behaviour
    auto_suggest: bool = True  # suggest after each thing "Them" says
    auto_min_words: int = 4  # ignore "mm-hmm", "okay" for auto mode
    debounce_s: float = 1.0  # wait this long after the last utterance before asking Claude
    include_screen: bool = False  # attach a screenshot to each request
    transcript_chars: int = 8000  # rolling window sent to Claude
