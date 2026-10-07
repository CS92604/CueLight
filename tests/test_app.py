import io
import sys

import pytest

from claude_live_conversation_assistant import app
from claude_live_conversation_assistant import settings as S
from claude_live_conversation_assistant.regions import Region


@pytest.fixture(autouse=True)
def isolated_config(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path))
    monkeypatch.setenv("APPDATA", str(tmp_path))


def test_cli_overrides_saved_settings_for_this_run():
    S.save(S.Settings(professionalism="Casual", tone="Direct"))
    cfg, settings, _ = app.parse_args(["--professionalism", "Formal", "--options", "3", "--reply-language", "French"])
    assert settings.professionalism == "Formal"  # flag wins
    assert settings.tone == "Direct"  # saved value kept
    assert settings.options == 3 and settings.reply_language == "French"


def test_region_flag_and_defaults():
    cfg, settings, args = app.parse_args(["--region", "100,50,400,300", "--manual"])
    assert cfg.region == Region(100, 50, 400, 300)
    assert cfg.auto_suggest is False
    assert settings == S.Settings()
    assert args.simulate is False


def test_simulate_mode_end_to_end_with_fake_client(monkeypatch, capsys):
    from test_suggest import FakeClient

    real = app.Suggester
    monkeypatch.setattr(app, "Suggester", lambda cfg: real(cfg, FakeClient()))
    monkeypatch.setattr(sys, "stdin", io.StringIO("Can you start Monday morning?\nme: I think so.\n/hint be more formal\n/quit\n"))
    assert app.main(["--simulate", "--context", "Job offer call"]) == 0
    out = capsys.readouterr().out
    assert "[Them] Can you start Monday morning?" in out
    assert "[Me] I think so." in out
    assert out.count("--- suggestions ---") == 2  # one for the question, one for the /hint
    assert "Sure, sounds good." in out
