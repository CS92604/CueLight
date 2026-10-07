from types import SimpleNamespace

from claude_live_conversation_assistant.config import Config
from claude_live_conversation_assistant.conversation import Conversation
from claude_live_conversation_assistant.settings import Settings
from claude_live_conversation_assistant.suggest import (
    FALLBACK_BETA,
    MANUAL,
    SPEECH,
    TEXT,
    Suggester,
    build_user_content,
    parse_suggestions,
)


def text_of(content):
    return next(b["text"] for b in content if b["type"] == "text")


def test_speech_only_request_has_no_image_and_asks_for_say():
    content = build_user_content("Them: hello", Settings(), {SPEECH}, context="Job interview", hint="shorter")
    assert [b["type"] for b in content] == ["text"]
    t = text_of(content)
    assert "<background>\nJob interview\n</background>" in t
    assert "<changed>spoken</changed>" in t
    assert "what to SAY" in t and "Extra direction from me: shorter" in t
    assert "<style_settings>" in t and "Formality:" in t


def test_text_change_includes_region_image_first_and_asks_for_type():
    content = build_user_content("", Settings(), {TEXT}, region_png=b"\x89PNG fake")
    assert [b["type"] for b in content] == ["image", "text"]
    assert content[0]["source"]["media_type"] == "image/png"
    t = text_of(content)
    assert "<changed>written</changed>" in t and "what to TYPE" in t


def test_both_channels_ask_for_say_and_type():
    t = text_of(build_user_content("Them: hi", Settings(), {SPEECH, TEXT}, region_png=b"png"))
    assert "<changed>spoken, written</changed>" in t
    assert "SAY and what to TYPE" in t


def test_manual_request_lists_available_channels():
    assert "<changed>spoken</changed>" in text_of(build_user_content("x", Settings(), {MANUAL}))
    t = text_of(build_user_content("x", Settings(), {MANUAL}, region_png=b"png"))
    assert "<changed>spoken, written</changed>" in t


def test_parse_say_and_type_sections():
    out = parse_suggestions(
        "SAY\n• Yeah, Monday works for me.\n• Monday's fine. Should I come by the office first?\n"
        "TYPE\n• Monday works, see you then!\n"
    )
    assert [(s.kind, len(s.options)) for s in out] == [("say", 2), ("type", 1)]
    assert out[0].options[0] == "Yeah, Monday works for me."
    assert out[1].options == ["Monday works, see you then!"]


def test_parse_is_lenient_about_formatting():
    out = parse_suggestions("**SAY:**\n- first option\n  continues here\n2) second\n### TYPE\n* typed")
    assert out[0].kind == "say" and out[0].options == ["first option continues here", "second"]
    assert out[1].kind == "type" and out[1].options == ["typed"]


def test_parse_note_and_headingless_bullets():
    assert [(s.kind, s.options) for s in parse_suggestions("(nothing to respond to yet)")] == [
        ("note", ["(nothing to respond to yet)"])
    ]
    assert parse_suggestions("• hi there")[0].kind == "say"
    assert parse_suggestions("") == []


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
        return FakeStream(["SAY\n• Sure, ", "sounds good."], self.stop_reason)


def convo():
    c = Conversation()
    c.add("them", "Can you start Monday?")
    return c


def test_default_model_uses_beta_fallbacks_and_low_effort():
    client = FakeClient()
    out = "".join(Suggester(Config(), client).stream(convo(), Settings(), {SPEECH}))
    assert out == "SAY\n• Sure, sounds good."
    api, kw = client.calls[0]
    assert api == "beta"
    assert kw["model"] == "claude-opus-5-5"
    assert kw["fallbacks"] == "default" and kw["betas"] == [FALLBACK_BETA]
    assert kw["output_config"] == {"effort": "low"}
    assert "thinking" not in kw and "temperature" not in kw
    assert "SAY" in kw["system"] and "TYPE" in kw["system"]


def test_haiku_gets_no_effort_and_no_fallbacks():
    client = FakeClient()
    list(Suggester(Config(model="claude-haiku-4-5"), client).stream(convo(), Settings()))
    api, kw = client.calls[0]
    assert api == "messages"
    assert "output_config" not in kw and "fallbacks" not in kw


def test_fallbacks_can_be_disabled():
    client = FakeClient()
    list(Suggester(Config(fallbacks=False), client).stream(convo(), Settings()))
    assert client.calls[0][0] == "messages"


def test_refusal_is_reported():
    client = FakeClient(stop_reason="refusal")
    out = "".join(Suggester(Config(), client).stream(convo(), Settings()))
    assert "declined" in out
