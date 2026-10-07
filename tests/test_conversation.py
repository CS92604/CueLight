from claude_live_conversation_assistant.conversation import Conversation


def test_render_labels_and_merges_same_speaker():
    c = Conversation()
    c.add("them", "Hi there.")
    c.add("them", "How are you?")
    c.add("me", "Good, thanks.")
    assert c.render(1000) == "Them: Hi there. How are you?\nMe: Good, thanks."


def test_render_drops_oldest_whole_turns():
    c = Conversation()
    c.add("them", "a" * 50)
    c.add("me", "b" * 50)
    c.add("them", "c" * 50)
    out = c.render(80).splitlines()
    assert out[0] == "[earlier conversation omitted]"
    assert out[-1].endswith("c" * 50)
    assert not any("a" * 50 in line for line in out)


def test_always_keeps_latest_turn_even_if_too_long():
    c = Conversation()
    c.add("them", "x" * 500)
    assert c.render(10).endswith("x" * 500)


def test_ignores_blank_text():
    c = Conversation()
    c.add("them", "   ")
    assert c.is_empty()
