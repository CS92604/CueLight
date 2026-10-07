import threading
import time

from claude_live_conversation_assistant.config import Config
from claude_live_conversation_assistant.engine import Engine
from claude_live_conversation_assistant.regions import Region
from claude_live_conversation_assistant.settings import Settings
from claude_live_conversation_assistant.suggest import MANUAL, SPEECH, TEXT

REGION = Region(10, 20, 300, 200)


class FakeSuggester:
    def __init__(self, chunks=("SAY\n• one", "\n• two"), gate=None):
        self.chunks = chunks
        self.gate = gate  # optional Event: block after the first chunk until set
        self.calls = []
        self.first_chunk_sent = threading.Event()

    def stream(self, conversation, settings, kinds, hint=None, region_png=None):
        self.calls.append(dict(transcript=conversation.render(10_000), kinds=set(kinds), hint=hint, png=region_png))
        for i, chunk in enumerate(self.chunks):
            yield chunk
            if i == 0 and self.gate is not None:
                self.first_chunk_sent.set()
                self.gate.wait(3)


class FakeWatcher:
    instances = []

    def __init__(self, region, on_change, interval_s=0.5, on_error=None):
        self.region, self.on_change, self.started, self.stopped = region, on_change, False, False
        self.pending = False
        FakeWatcher.instances.append(self)

    def start(self):
        self.started = True

    def stop(self):
        self.stopped = True


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


def make(cfg=None, suggester=None, region=None):
    FakeWatcher.instances.clear()
    rec = Recorder()
    sug = suggester or FakeSuggester()
    eng = Engine(
        cfg or Config(debounce_s=0.05), sug, rec, Settings(),
        capture_region=lambda r: b"png:" + str(r.width).encode(), watcher_factory=FakeWatcher,
    )
    eng.start()
    if region:
        eng.set_region(region)
    return eng, sug, rec


def test_speech_triggers_say_request_without_image():
    eng, sug, rec = make(region=REGION)
    eng.add_turn("them", "Can you tell me about your last project?")
    rec.wait_for("suggest_end")
    eng.stop()
    call = sug.calls[0]
    assert call["kinds"] == {SPEECH} and call["png"] is None
    assert "Them: Can you tell me about your last project?" in call["transcript"]
    assert "".join(p for k, p in rec.events if k == "chunk") == "SAY\n• one\n• two"


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
    assert "background. And why you want" in sug.calls[0]["transcript"]


def test_screen_change_triggers_type_request_with_region_image():
    eng, sug, rec = make(region=REGION)
    assert FakeWatcher.instances[-1].started and FakeWatcher.instances[-1].region == REGION
    eng.on_screen_change()
    rec.wait_for("suggest_end")
    eng.stop()
    assert sug.calls[0]["kinds"] == {TEXT}
    assert sug.calls[0]["png"] == b"png:300"


def test_speech_and_screen_change_together_ask_for_both():
    eng, sug, rec = make(Config(debounce_s=0.2), region=REGION)
    eng.add_turn("them", "Did you get my message about the schedule?")
    eng.on_screen_change()
    rec.wait_for("suggest_end")
    time.sleep(0.4)
    eng.stop()
    assert len(sug.calls) == 1
    assert sug.calls[0]["kinds"] == {SPEECH, TEXT}
    assert sug.calls[0]["png"] is not None


def test_interrupted_text_request_is_not_forgotten_when_speech_arrives():
    gate = threading.Event()
    eng, sug, rec = make(Config(debounce_s=0.0), suggester=FakeSuggester(gate=gate), region=REGION)
    eng.on_screen_change()  # run 1: TEXT, blocks after its first chunk
    assert sug.first_chunk_sent.wait(3)
    eng.add_turn("them", "Hey, are you still there with us on the call?")  # supersedes run 1
    gate.set()
    rec.wait_for("suggest_end", timeout=5)
    eng.stop()
    assert sug.calls[0]["kinds"] == {TEXT}
    assert sug.calls[1]["kinds"] == {SPEECH, TEXT}


def test_auto_off_ignores_triggers_but_manual_works():
    eng, sug, rec = make(Config(auto_suggest=False, debounce_s=0.0), region=REGION)
    eng.add_turn("them", "What are your salary expectations for this role?")
    eng.on_screen_change()
    time.sleep(0.2)
    assert sug.calls == []
    eng.request("keep it polite and short")
    rec.wait_for("suggest_end")
    eng.stop()
    assert sug.calls[0]["hint"] == "keep it polite and short"
    assert sug.calls[0]["kinds"] == {MANUAL}
    assert sug.calls[0]["png"] is not None  # manual requests include what's on screen


def test_clearing_region_stops_watching():
    eng, _, rec = make(region=REGION)
    watcher = FakeWatcher.instances[-1]
    eng.set_region(None)
    eng.on_screen_change()  # a late callback from the old watcher must do nothing
    time.sleep(0.15)
    eng.stop()
    assert watcher.stopped and eng.region is None
    assert ("region", None) in rec.events and "suggest_start" not in rec.kinds()


def test_screenshot_failure_degrades_gracefully():
    FakeWatcher.instances.clear()
    rec, sug = Recorder(), FakeSuggester()

    def boom(_r):
        raise OSError("no display")

    eng = Engine(Config(debounce_s=0.0), sug, rec, Settings(), capture_region=boom, watcher_factory=FakeWatcher)
    eng.start()
    eng.set_region(REGION)
    eng.on_screen_change()
    rec.wait_for("suggest_end")
    eng.stop()
    assert sug.calls[0]["png"] is None
    assert any(k == "status" and "no display" in str(p) for k, p in rec.events)


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


def test_spoken_request_waits_for_a_settling_screen_change_and_merges():
    eng, sug, rec = make(Config(debounce_s=0.05, merge_hold_s=3.0), region=REGION)
    watcher = FakeWatcher.instances[-1]
    watcher.pending = True  # the chat text is mid-change
    eng.add_turn("them", "Hey, did you see the message I just sent you?")
    time.sleep(0.4)
    assert sug.calls == []  # held, not sent as speech-only
    watcher.pending = False
    eng.on_screen_change()  # the text settled
    rec.wait_for("suggest_end")
    time.sleep(0.2)
    eng.stop()
    assert len(sug.calls) == 1 and sug.calls[0]["kinds"] == {SPEECH, TEXT}


def test_hold_is_bounded_if_the_screen_never_settles():
    eng, sug, rec = make(Config(debounce_s=0.05, merge_hold_s=0.5), region=REGION)
    FakeWatcher.instances[-1].pending = True  # e.g. a video playing in the watched area
    eng.add_turn("them", "Hey, did you see the message I just sent you?")
    rec.wait_for("suggest_end", timeout=3)
    eng.stop()
    assert sug.calls[0]["kinds"] == {SPEECH}
