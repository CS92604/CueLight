import numpy as np

from claude_live_conversation_assistant.audio import SAMPLE_RATE, Segmenter


def tone(seconds: float, amp: float = 0.2, freq: float = 220.0) -> np.ndarray:
    t = np.arange(int(SAMPLE_RATE * seconds)) / SAMPLE_RATE
    return (amp * np.sin(2 * np.pi * freq * t)).astype(np.float32)


def silence(seconds: float) -> np.ndarray:
    return np.zeros(int(SAMPLE_RATE * seconds), dtype=np.float32)


def feed_in_chunks(seg: Segmenter, audio: np.ndarray, chunk: int = 1600):
    out = []
    for i in range(0, len(audio), chunk):
        out += seg.feed(audio[i : i + chunk])
    return out


def test_two_utterances_are_split_on_silence():
    seg = Segmenter()
    audio = np.concatenate([silence(1), tone(1.0), silence(1.2), tone(0.8), silence(1.2)])
    utts = feed_in_chunks(seg, audio)
    assert len(utts) == 2
    assert 1.0 < len(utts[0]) / SAMPLE_RATE < 2.0
    assert 0.8 < len(utts[1]) / SAMPLE_RATE < 1.8


def test_short_blip_is_discarded():
    seg = Segmenter()
    audio = np.concatenate([silence(1), tone(0.1), silence(1.5)])
    assert feed_in_chunks(seg, audio) == []


def test_background_hiss_is_not_speech():
    rng = np.random.default_rng(0)
    hiss = (0.003 * rng.standard_normal(SAMPLE_RATE * 5)).astype(np.float32)
    assert feed_in_chunks(Segmenter(), hiss) == []


def test_long_speech_is_capped():
    seg = Segmenter(max_utterance_s=5)
    utts = feed_in_chunks(seg, np.concatenate([tone(12.0), silence(1.0)]))
    assert len(utts) >= 2
    assert all(len(u) / SAMPLE_RATE <= 5.1 for u in utts)


def test_flush_returns_pending_speech():
    seg = Segmenter()
    feed_in_chunks(seg, np.concatenate([silence(0.5), tone(1.0)]))
    tail = seg.flush()
    assert tail is not None and len(tail) / SAMPLE_RATE > 0.9
    assert seg.flush() is None
