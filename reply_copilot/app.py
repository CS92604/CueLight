from __future__ import annotations

import argparse
import os
import queue
import sys
import threading

from .config import Config
from .engine import Engine
from .suggest import Suggester


def parse_args(argv: list[str] | None = None) -> tuple[Config, argparse.Namespace]:
    p = argparse.ArgumentParser(
        prog="reply_copilot",
        description="Listen to your PC's audio, transcribe it locally, and get Claude-suggested replies.",
    )
    d = Config()
    p.add_argument("--context", default="", help='What this conversation is about / who you are, e.g. "I\'m interviewing for a backend role"')
    p.add_argument("--context-file", help="Read --context from a text file")
    p.add_argument("--model", default=d.model, help=f"Claude model (default {d.model}; try claude-sonnet-5-5 or claude-haiku-4-5 for lower latency)")
    p.add_argument("--effort", default=d.effort, choices=["low", "medium", "high", "xhigh", "max"], help="Claude effort level (ignored for Haiku)")
    p.add_argument("--whisper", default=d.whisper_model, help="faster-whisper model: tiny.en, base.en, small.en, medium.en, or multilingual (small, medium, ...)")
    p.add_argument("--language", default=d.language, help="Spoken language code for transcription (default en)")
    p.add_argument("--loopback-device", help="Substring of the device that carries the other people's audio (needed on macOS: e.g. BlackHole)")
    p.add_argument("--mic", action="store_true", help="Also transcribe your own microphone as 'Me' (use headphones to avoid echo)")
    p.add_argument("--mic-device", help="Substring of the microphone to use with --mic")
    p.add_argument("--manual", action="store_true", help="Only suggest when you press the button; no automatic suggestions")
    p.add_argument("--screen", action="store_true", help="Attach a screenshot of your primary monitor to each request")
    p.add_argument("--no-fallbacks", action="store_true", help="Don't send the server-side refusal-fallback parameter")
    p.add_argument("--console", action="store_true", help="Print to the terminal instead of opening the overlay window")
    p.add_argument("--text", action="store_true", help="No audio: type what the other person said (prefix 'me:' for yourself). Implies --console")
    p.add_argument("--list-devices", action="store_true", help="List audio devices and exit")
    args = p.parse_args(argv)

    context = args.context
    if args.context_file:
        with open(args.context_file, encoding="utf-8") as f:
            context = f.read().strip()
    cfg = Config(
        model=args.model,
        effort=args.effort,
        fallbacks=d.fallbacks and not args.no_fallbacks,
        context=context,
        whisper_model=args.whisper,
        language=args.language,
        loopback_device=args.loopback_device,
        mic_device=args.mic_device,
        use_mic=args.mic,
        auto_suggest=not args.manual,
        include_screen=args.screen,
    )
    return cfg, args


def console_sink(done: threading.Event):
    def emit(kind: str, payload: object) -> None:
        if kind == "turn":
            speaker, text = payload  # type: ignore[misc]
            print(f"\n[{'Them' if speaker == 'them' else 'Me'}] {text}", flush=True)
        elif kind == "suggest_start":
            print("\n--- suggestions ---", flush=True)
        elif kind == "chunk":
            print(payload, end="", flush=True)
        elif kind == "suggest_end":
            print("\n-------------------", flush=True)
            done.set()
        elif kind == "status":
            print(f"\n[{payload}]", flush=True)
        elif kind == "error":
            print(f"\n[error] {payload}", file=sys.stderr, flush=True)
            done.set()

    return emit


def run_text_mode(engine: Engine, done: threading.Event) -> None:
    print("Type what the other person says and press Enter. Prefix 'me:' for your own lines.")
    print("Commands: /clear, /hint <direction>, /quit\n")
    try:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            if line == "/quit":
                break
            if line == "/clear":
                engine.clear()
                print("[cleared]")
                continue
            done.clear()
            if line.startswith("/hint "):
                engine.request(line[len("/hint ") :].strip())
            elif line.lower().startswith("me:"):
                engine.conversation.add("me", line[3:])
                engine.emit("turn", ("me", line[3:].strip()))
                continue
            else:
                engine.conversation.add("them", line)
                engine.emit("turn", ("them", line))
                engine.request()
            done.wait(timeout=120)
    except KeyboardInterrupt:
        pass


def run_audio(cfg: Config, engine: Engine, emit) -> tuple[threading.Event, list]:
    """Start capture + transcription threads. Returns (stop_event, capture_threads)."""
    from . import audio

    stop = threading.Event()
    q: queue.Queue = queue.Queue()
    captures = [audio.Capture("them", audio.pick_loopback(cfg.loopback_device), q, stop)]
    if cfg.use_mic:
        captures.append(audio.Capture("me", audio.pick_mic(cfg.mic_device), q, stop))

    def transcribe_loop() -> None:
        from .transcribe import Transcriber

        emit("status", f"Loading speech model ({cfg.whisper_model})…")
        try:
            stt = Transcriber(cfg.whisper_model, cfg.language, cfg.compute_type)
        except Exception as exc:
            emit("error", f"Couldn't load the speech model: {exc}")
            return
        emit("status", "Listening.")
        while not stop.is_set():
            try:
                utt = q.get(timeout=0.2)
            except queue.Empty:
                continue
            if utt.speaker == "error":
                for c in captures:
                    if c.error:
                        emit("error", f"Audio capture failed: {c.error}")
                continue
            try:
                text = stt.transcribe(utt.audio)
            except Exception as exc:
                emit("error", f"Transcription failed: {exc}")
                continue
            engine.add_turn(utt.speaker, text)

    for c in captures:
        c.start()
    threading.Thread(target=transcribe_loop, daemon=True, name="transcribe").start()
    return stop, captures


def main(argv: list[str] | None = None) -> int:
    cfg, args = parse_args(argv)

    if args.list_devices:
        from . import audio

        print(audio.list_devices())
        return 0

    if not os.environ.get("ANTHROPIC_API_KEY") and not os.environ.get("ANTHROPIC_AUTH_TOKEN"):
        print("Note: ANTHROPIC_API_KEY is not set; relying on an `ant auth login` profile if you have one.", file=sys.stderr)

    use_window = not (args.console or args.text)
    overlay = None
    if use_window:
        try:
            from .overlay import Overlay
        except ImportError as exc:  # tkinter missing
            print(f"Can't open the overlay window ({exc}); falling back to --console.", file=sys.stderr)
            use_window = False

    done = threading.Event()
    events: queue.Queue = queue.Queue()
    emit = (lambda kind, payload: events.put((kind, payload))) if use_window else console_sink(done)

    engine = Engine(cfg, Suggester(cfg), emit)
    engine.start()

    stop = None
    if not args.text:
        try:
            stop, _ = run_audio(cfg, engine, emit)
        except Exception as exc:
            print(f"Couldn't start audio capture: {exc}\nTry --list-devices, or --text to type the conversation.", file=sys.stderr)
            engine.stop()
            return 1

    try:
        if use_window:
            overlay = Overlay(engine, cfg, events)
            overlay.run()
        elif args.text:
            run_text_mode(engine, done)
        else:
            print("Listening… press Ctrl+C to stop.")
            threading.Event().wait()
    except KeyboardInterrupt:
        pass
    finally:
        if stop:
            stop.set()
        engine.stop()
    return 0
