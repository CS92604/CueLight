from types import SimpleNamespace

from reply_copilot.config import Config
from reply_copilot.conversation import Conversation
from reply_copilot.suggest import FALLBACK_BETA, Suggester, build_user_content


def test_user_content_text_only():
    content = build_user_content("Them: hello", context="Job interview", hint="shorter")
    assert len(content) == 1 and content[0]["type"] == "text"
    text = content[0]["text"]
    assert "<background>\nJob interview\n</background>" in text
    assert "Them: hello" in text
    assert "Extra direction from me: shorter" in text


def test_user_content_with_screenshot_puts_image_first():
    content = build_user_content("Them: hi", screenshot_png=b"\x89PNG fake")
    assert [b["type"] for b in content] == ["image", "text"]
    assert content[0]["source"]["media_type"] == "image/png"
    assert "screenshot" in content[1]["text"]


class FakeStream:
    def __init__(self, chunks, stop_reason="end_turn"):
        self.text_stream = iter(chunks)
        self._final = SimpleNamespace(stop_reason=stop_reason)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def get_final_message(self):
        return self._final


class FakeClient:
    def __init__(self, stop_reason="end_turn"):
        self.calls = []
        self.stop_reason = stop_reason
        self.messages = SimpleNamespace(stream=lambda **kw: self._record("messages", kw))
        self.beta = SimpleNamespace(messages=SimpleNamespace(stream=lambda **kw: self._record("beta", kw)))

    def _record(self, api, kwargs):
        self.calls.append((api, kwargs))
        return FakeStream(["• Sure, ", "sounds good."], self.stop_reason)


def convo():
    c = Conversation()
    c.add("them", "Can you start Monday?")
    return c


def test_default_model_uses_beta_fallbacks_and_low_effort():
    client = FakeClient()
    out = "".join(Suggester(Config(), client).stream(convo()))
    assert out == "• Sure, sounds good."
    api, kw = client.calls[0]
    assert api == "beta"
    assert kw["model"] == "claude-opus-5-5"
    assert kw["fallbacks"] == "default" and kw["betas"] == [FALLBACK_BETA]
    assert kw["output_config"] == {"effort": "low"}
    assert "thinking" not in kw and "temperature" not in kw


def test_haiku_gets_no_effort_and_no_fallbacks():
    client = FakeClient()
    list(Suggester(Config(model="claude-haiku-4-5"), client).stream(convo()))
    api, kw = client.calls[0]
    assert api == "messages"
    assert "output_config" not in kw and "fallbacks" not in kw


def test_fallbacks_can_be_disabled():
    client = FakeClient()
    list(Suggester(Config(fallbacks=False), client).stream(convo()))
    assert client.calls[0][0] == "messages"


def test_refusal_is_reported():
    client = FakeClient(stop_reason="refusal")
    out = "".join(Suggester(Config(), client).stream(convo()))
    assert "declined" in out
