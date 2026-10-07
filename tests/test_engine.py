import threading
import time

from reply_copilot.config import Config
from reply_copilot.engine import Engine


class FakeSuggester:
    def __init__(self, chunks=("• one", "\n• two")):
        self.chunks = chunks
        self.calls = []

    def stream(self, conversation, hint=None, screenshot_png=None):
        self.calls.append((conversation.render(10_000), hint, screenshot_png))
        yield from self.chunks


class Recorder:
    def __init__(self):
        self.events = []
        self.cv = threading.Condition()

    def __call__(self, kind, payload):
        with self.cv:
            self.events.append((kind, payload))
            self.cv.notify_all()

    def wait_for(self, kind, count=1, timeout=3.0):
        with self.cv:
            ok = self.cv.wait_for(lambda: sum(1 for k, _ in self.events if k == kind) >= count, timeout)
        assert ok, f"timed out waiting for {kind}: {self.events}"

    def kinds(self):
        return [k for k, _ in self.events]


def make(cfg=None, suggester=None, **kw):
    rec = Recorder()
    sug = suggester or FakeSuggester()
    eng = Engine(cfg or Config(debounce_s=0.05), sug, rec, **kw)
    eng.start()
    return eng, sug, rec


def test_them_utterance_triggers_suggestion():
    eng, sug, rec = make()
    eng.add_turn("them", "Can you tell me about your last project?")
    rec.wait_for("suggest_end")
    eng.stop()
    assert "".join(p for k, p in rec.events if k == "chunk") == "• one\n• two"
    assert "Them: Can you tell me about your last project?" in sug.calls[0][0]


def test_my_own_speech_and_short_backchannels_do_not_trigger():
    eng, sug, rec = make()
    eng.add_turn("me", "I built a scheduling service at my last job.")
    eng.add_turn("them", "mm-hmm")
    time.sleep(0.3)
    eng.stop()
    assert sug.calls == []
    assert rec.kinds().count("turn") == 2


def test_rapid_utterances_are_coalesced_into_one_request():
    eng, sug, rec = make(Config(debounce_s=0.2))
    eng.add_turn("them", "So tell me about yourself and your background.")
    eng.add_turn("them", "And why you want this particular role here.")
    rec.wait_for("suggest_end")
    time.sleep(0.4)
    eng.stop()
    assert len(sug.calls) == 1
    assert "background. And why you want" in sug.calls[0][0]


def test_manual_request_passes_hint():
    eng, sug, rec = make(Config(auto_suggest=False))
    eng.add_turn("them", "What are your salary expectations for this role?")
    eng.request("keep it polite and short")
    rec.wait_for("suggest_end")
    eng.stop()
    assert sug.calls[0][1] == "keep it polite and short"


def test_screenshot_only_when_enabled():
    shots = []
    eng, sug, rec = make(Config(auto_suggest=False, include_screen=True), screenshot=lambda: shots.append(1) or b"png")
    eng.request()
    rec.wait_for("suggest_end")
    assert sug.calls[0][2] == b"png"
    eng.include_screen = False
    eng.request()
    rec.wait_for("suggest_end", count=2)
    eng.stop()
    assert sug.calls[1][2] is None and len(shots) == 1


def test_api_errors_are_reported_not_raised():
    class Boom(FakeSuggester):
        def stream(self, *a, **k):
            raise RuntimeError("kaput")
            yield  # pragma: no cover

    eng, _, rec = make(Config(auto_suggest=False), suggester=Boom())
    eng.request()
    rec.wait_for("error")
    eng.stop()
    assert any("kaput" in str(p) for k, p in rec.events if k == "error")
