from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, fields
from pathlib import Path

# Each setting: label shown in the UI -> instruction given to Claude.
PROFESSIONALISM = {
    "Very casual": "very casual, like texting a friend: contractions, relaxed phrasing, light slang is fine",
    "Casual": "casual and friendly, like talking to a colleague you get along with",
    "Professional": "professional: polite and clear, no slang, still natural and not stiff",
    "Formal": "formal: courteous, precise and respectful, as with a senior stakeholder or in a formal setting",
}
PROFICIENCY = {
    "Simple": "simple English at about CEFR B1: common words, short sentences, easy to say out loud; avoid idioms and long words",
    "Everyday": "everyday conversational English at about CEFR B2: clear and natural, occasional idiom, no rare vocabulary",
    "Fluent": "fluent, native-like English at about CEFR C1: natural idioms and varied sentence structure",
    "Advanced": "highly articulate English at about CEFR C2: rich vocabulary and precise, polished phrasing",
}
TONE = {
    "Warm": "warm and personable",
    "Neutral": "neutral and matter-of-fact",
    "Direct": "direct and to the point, without padding",
    "Diplomatic": "diplomatic and tactful, softening anything that could land badly",
    "Confident": "confident and assertive, without being aggressive",
}
LENGTH = {
    "Brief": "brief: one short sentence per option",
    "Short": "short: one or two sentences per option",
    "Detailed": "detailed: two to four sentences per option, with a reason or example placeholder where it helps",
}

DEFAULTS = {
    "professionalism": "Professional",
    "proficiency": "Fluent",
    "tone": "Warm",
    "length": "Short",
}
CHOICES = {
    "professionalism": PROFESSIONALISM,
    "proficiency": PROFICIENCY,
    "tone": TONE,
    "length": LENGTH,
}


@dataclass
class Settings:
    professionalism: str = DEFAULTS["professionalism"]
    proficiency: str = DEFAULTS["proficiency"]  # how advanced the *wording* should be
    tone: str = DEFAULTS["tone"]
    length: str = DEFAULTS["length"]
    options: int = 2  # suggestions per section (1-3)
    reply_language: str = ""  # empty = same language as the other person
    custom: str = ""  # free-text extra instructions (who you are, phrases to avoid, ...)

    def normalized(self) -> "Settings":
        s = Settings(**asdict(self))
        for key, table in CHOICES.items():
            if getattr(s, key) not in table:
                setattr(s, key, DEFAULTS[key])
        try:
            s.options = min(3, max(1, int(s.options)))
        except (TypeError, ValueError):
            s.options = 2
        s.reply_language = str(s.reply_language or "").strip()
        s.custom = str(s.custom or "").strip()
        return s

    def to_prompt(self) -> str:
        s = self.normalized()
        lines = [
            f"- Formality: {PROFESSIONALISM[s.professionalism]}",
            f"- My language proficiency (write wording I can say comfortably): {PROFICIENCY[s.proficiency]}",
            f"- Tone: {TONE[s.tone]}",
            f"- Length: {LENGTH[s.length]}",
            f"- Options: up to {s.options} per section",
            f"- Reply language: {s.reply_language or 'the same language the other person is using'}",
        ]
        if s.custom:
            lines.append(f"- Extra instructions from me: {s.custom}")
        return "\n".join(lines)


def default_path() -> Path:
    if os.name == "nt":
        base = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    else:
        base = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return base / "claude-live-conversation-assistant" / "settings.json"


def load(path: Path | None = None) -> Settings:
    path = path or default_path()
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
        known = {f.name for f in fields(Settings)}
        return Settings(**{k: v for k, v in raw.items() if k in known}).normalized()
    except (OSError, ValueError, TypeError):
        return Settings()


def save(settings: Settings, path: Path | None = None) -> Path:
    path = path or default_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(asdict(settings.normalized()), indent=2), encoding="utf-8")
    return path
