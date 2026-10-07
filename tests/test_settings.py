import json

from claude_live_conversation_assistant import settings as S


def test_defaults_render_every_setting():
    prompt = S.Settings().to_prompt()
    for label in ("Formality", "proficiency", "Tone", "Length", "Options", "Reply language"):
        assert label in prompt
    assert "same language the other person" in prompt


def test_choices_change_the_prompt():
    prompt = S.Settings(professionalism="Formal", proficiency="Simple", tone="Direct", length="Brief", options=3,
                        reply_language="Spanish", custom="I'm a junior analyst").to_prompt()
    assert "formal: courteous" in prompt
    assert "CEFR B1" in prompt
    assert "direct and to the point" in prompt
    assert "one short sentence" in prompt
    assert "up to 3 per section" in prompt
    assert "Spanish" in prompt and "junior analyst" in prompt


def test_invalid_values_fall_back_to_defaults():
    s = S.Settings(professionalism="Chaotic", options=99, tone=None).normalized()
    assert s.professionalism == S.DEFAULTS["professionalism"]
    assert s.options == 3
    assert s.tone == S.DEFAULTS["tone"]
    assert S.Settings(options="abc").normalized().options == 2


def test_save_and_load_round_trip(tmp_path):
    path = tmp_path / "sub" / "settings.json"
    S.save(S.Settings(professionalism="Casual", options=1, custom="hi"), path)
    loaded = S.load(path)
    assert (loaded.professionalism, loaded.options, loaded.custom) == ("Casual", 1, "hi")


def test_load_tolerates_missing_corrupt_and_unknown(tmp_path):
    assert S.load(tmp_path / "nope.json") == S.Settings()
    bad = tmp_path / "bad.json"
    bad.write_text("{not json")
    assert S.load(bad) == S.Settings()
    extra = tmp_path / "extra.json"
    extra.write_text(json.dumps({"tone": "Direct", "future_option": 1}))
    assert S.load(extra).tone == "Direct"
