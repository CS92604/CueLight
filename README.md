# Reply Copilot

Listens to the audio playing on your PC (the other people on a call), transcribes it
locally, and uses Claude to suggest natural things for you to say next. It can also
look at a screenshot of your screen for the written side of the conversation.

```
system audio ──► voice-activity split ──► faster-whisper (local) ──┐
your mic (optional, --mic) ─────────────────────────────────────────┼─► transcript ─► Claude ─► overlay
screenshot (optional, --screen) ─────────────────────────────────────┘
```

Only text (and a screenshot, if you turn that on) is sent to the Claude API. Audio is
transcribed on your machine.

## Setup

```bash
python -m venv .venv && . .venv/bin/activate     # Windows: .venv\Scripts\activate
pip install -r requirements.txt
export ANTHROPIC_API_KEY=sk-ant-...              # Windows: setx ANTHROPIC_API_KEY ...
python -m reply_copilot --context "I'm on a call with a recruiter about a backend role"
```

The first run downloads the Whisper model (~150 MB for `base.en`).

| Platform | System-audio capture |
|---|---|
| Windows | Works out of the box (WASAPI loopback). |
| Linux | Works out of the box (PulseAudio / PipeWire monitor of the default output). |
| macOS | Needs a virtual device such as [BlackHole](https://github.com/ExistentialAudio/BlackHole). Route output through it (Multi-Output Device) and run with `--loopback-device BlackHole`. |

Run `python -m reply_copilot --list-devices` to see what's available.

## Usage

```
python -m reply_copilot [options]

--context "..."        who you are / what the conversation is about (the single biggest quality lever)
--context-file FILE    same, from a file (paste a job description, your notes, ...)
--mic                  also transcribe your own mic as "Me" (use headphones, or the mic will re-hear the speakers)
--screen               attach a screenshot of the primary monitor to every request
--manual               only suggest when you press Suggest
--model MODEL          default claude-opus-5-5; claude-sonnet-5-5 / claude-haiku-4-5 are faster and cheaper
--effort LEVEL         default low (ignored for Haiku)
--whisper MODEL        tiny.en | base.en (default) | small.en | ... ; multilingual: small, medium + --language es
--console              print to the terminal instead of opening the overlay window
--text                 no audio: type what the other person said (prefix "me:" for yourself)
```

The overlay stays on top. Type a direction in the box ("shorter", "more formal", "ask
about the timeline") and press Enter to get a fresh set of options.

`--text` is the quickest way to check your API key and see what the suggestions look like
without any audio setup.

## Notes

- **Cost.** In auto mode every substantial thing "Them" says triggers one Claude request
  (roughly 1-3k input tokens, ~100 output). My rough estimate is a dollar or two per busy
  hour on the default model (not measured); use `--model claude-sonnet-5-5` / `--manual` to cut it. Only the last
  `transcript_chars` (8000) characters of the conversation are sent.
- **Latency.** Suggestions stream in as Claude writes them, after a ~1 s pause that lets the
  speaker finish. For faster responses use a smaller model (`--model claude-haiku-4-5`).
- **Refusals.** For models that support it, the request includes the server-side
  `fallbacks: "default"` parameter so a safety-classifier decline is retried on a fallback
  model automatically. `--no-fallbacks` or `COPILOT_FALLBACKS=0` turns that off.
- **Windows silence quirk.** WASAPI loopback can stop delivering audio while nothing is
  playing; if the transcript stalls after a long silence, play any sound.
- **Accuracy.** Speech-to-text makes mistakes, and Claude is told not to invent facts or
  experiences about you. Treat suggestions as prompts, not scripts, and check anything
  factual before you say it.

## Use responsibly

Recording or transcribing other people can require their consent depending on where you
live, and many employers, schools and interviewers prohibit live AI assistance. Check the
rules for your situation. The window deliberately doesn't hide itself from screen sharing.

## Development

```bash
pip install pytest && python -m pytest
```

Tests cover the voice-activity splitter, transcript windowing, request construction (with
a fake Claude client) and the suggestion engine's coalescing. Audio capture, Whisper and
the Tk window need real hardware/display, so they aren't covered by automated tests.
