# claude-live-conversation-assistant

Listens to whatever your PC's speakers are playing, watches a box you draw around any
on-screen text (a chat window, an email, a doc), and uses Claude to tell you what to
**say** and what to **type**, worded the way a real person would put it.

```
speakers ─► split into utterances ─► faster-whisper (local) ─┐
                                                              ├─► Claude ─► SAY (out loud) + TYPE (paste)
drag a box over any text ─► re-read when it changes ──────────┘
your mic (optional, --mic) ─► "Me" in the transcript
```

- **Spoken side:** system audio is transcribed locally; only text is sent to Claude.
- **Written side:** you pick the area once; when its content changes (and stops changing),
  Claude reads a screenshot of just that area. Nothing outside the box is ever captured.
- **Both at once:** if someone is talking *and* the text area updates, you get a **SAY**
  section and a **TYPE** section together, consistent with each other.
- **Copy-paste ready:** every suggestion has a Copy button (or click the text).

## Setup

```bash
python -m venv .venv && . .venv/bin/activate     # Windows: .venv\Scripts\activate
pip install -r requirements.txt
export ANTHROPIC_API_KEY=sk-ant-...              # Windows: setx ANTHROPIC_API_KEY ...
python -m claude_live_conversation_assistant --context "I'm on a call with a recruiter about a backend role"
```

The first run downloads the Whisper model (~150 MB for `base.en`).

| Platform | Notes |
|---|---|
| Windows | Works out of the box (WASAPI loopback for audio). |
| Linux | Audio via PulseAudio/PipeWire monitor. Needs Tk (`sudo apt install python3-tk`) and an **X11** session: screen capture doesn't work under Wayland. |
| macOS | Audio needs a virtual device such as [BlackHole](https://github.com/ExistentialAudio/BlackHole) (`--loopback-device BlackHole`). Grant your terminal *Screen Recording* permission for the text area. |

`python -m claude_live_conversation_assistant --list-devices` shows audio devices.

## Using it

1. Start it. A small always-on-top window opens and begins listening.
2. Click **Select text area**. The screen freezes like a snipping tool: drag a box over
   the text you want watched. A red outline stays around it (drawn just outside the box,
   so it never appears in what Claude sees). **Clear area** stops watching. Keep the
   assistant window itself off the watched area.
3. Talk or chat as normal. Suggestions appear on their own after a short pause:
   - spoken words only → **SAY**
   - watched text changed only → **TYPE**
   - both → **SAY** and **TYPE**
4. **Copy** puts one option on your clipboard. The box under the transcript takes a
   direction ("shorter", "ask about the timeline"); press Enter or **Suggest** for a fresh set.

"Changed" means a real change that has stopped moving for about a second, so a message
that's still being typed isn't read half-finished, and a blinking cursor is ignored.

## Settings (the **Settings** button)

| Setting | What it does |
|---|---|
| Professionalism | Very casual · Casual · Professional · Formal |
| Proficiency | How advanced the *wording* is, so you can say it comfortably: Simple (~B1) · Everyday (~B2) · Fluent (~C1) · Advanced (~C2) |
| Tone | Warm · Neutral · Direct · Diplomatic · Confident |
| Length | Brief · Short · Detailed |
| Options per section | 1–3 |
| Reply language | Empty = match the other person |
| Extra instructions | Free text: who you are, words to avoid, … |

Settings apply to the next suggestion and are saved to
`~/.config/claude-live-conversation-assistant/settings.json` (`%APPDATA%\...` on Windows).
Command-line flags override them for one run: `--professionalism Formal --proficiency Simple
--tone Direct --length Brief --options 3 --reply-language Spanish`.

## Options

```
--context "..." / --context-file FILE   who you are / what this is about (a big quality lever)
--region L,T,W,H       start with a watched area (otherwise drag one in the window)
--mic                  also transcribe your own mic as "Me" (use headphones, or it will re-hear the speakers)
--manual               only suggest when you press Suggest
--model MODEL          default claude-opus-5-5; claude-sonnet-5-5 / claude-haiku-4-5 are faster and cheaper
--effort LEVEL         default low (ignored for Haiku)
--whisper MODEL        tiny.en | base.en (default) | small.en | ...; multilingual: small, medium + --language es
--console              print to the terminal instead of opening the window
--simulate             no audio: type what the other person said (prefix "me:" for yourself)
```

`--simulate` is the quickest way to check your API key and see the style of suggestions.

## Notes

- **Cost.** Each substantial spoken turn or text change is one Claude request (roughly
  1–3k input tokens, ~100 output; a watched-area image adds a little). My rough estimate
  is a dollar or two per busy hour on the default model (not measured). `--manual` or
  `--model claude-sonnet-5-5` cuts it. Only the last ~8000 characters of conversation are sent.
- **Memory.** Claude sees the spoken transcript so far, but only the *current* picture of
  the watched area, not earlier ones.
- **Latency.** Suggestions stream in after a ~1 s pause. If the watched text is mid-change
  when someone speaks, the reply is held (up to 3 s) so the SAY and TYPE arrive together.
- **Refusals.** For models that support it, requests include the server-side
  `fallbacks: "default"` parameter so a safety-classifier decline is retried on a fallback
  model automatically. `--no-fallbacks` or `LIVE_ASSISTANT_FALLBACKS=0` turns that off.
- **Windows silence quirk.** WASAPI loopback can stop delivering audio while nothing is
  playing; if the transcript stalls after a long silence, play any sound.
- **Accuracy.** Speech-to-text and screen reading make mistakes, and Claude is told not to
  invent facts or experiences about you. Treat suggestions as prompts, not scripts, and check
  anything factual before you say or send it.

## Use responsibly

Recording or transcribing other people can require their consent depending on where you
live, and many employers, schools and interviewers prohibit live AI assistance. Check the
rules for your situation. The window deliberately doesn't hide itself from screen sharing.

## Development

```bash
pip install pytest && python -m pytest
```

Tests cover the speech splitter, transcript windowing, change detection, settings,
request construction and response parsing (with a fake Claude client), and the engine's
coalescing of speech and screen triggers. The Tk window (area picker, outline, copy
buttons, settings dialog) was exercised end-to-end under a virtual X display; audio
capture and Whisper need real hardware and aren't covered by automated tests.
