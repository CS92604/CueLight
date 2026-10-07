from __future__ import annotations

import base64
from typing import Iterator

import anthropic

from .config import Config
from .conversation import Conversation

SYSTEM_PROMPT = """\
You are a real-time conversation assistant running on the user's computer. You hear the \
other people in the user's conversation (labelled "Them") and sometimes the user \
themselves ("Me"), through automatic speech transcription that may contain errors. You \
may also be shown a screenshot of the user's screen.

Your job: give the user something natural to say next, in their own voice.

Rules:
- Offer up to 3 options, one per line, each starting with "• ". Each option is 1-2 \
spoken sentences, first person, plain conversational language: how a person actually \
talks, not how an email reads.
- Make the options meaningfully different (for example direct, diplomatic, or a question \
back to them), not rewordings of each other.
- Respond to what Them said or asked most recently.
- Never invent personal facts, credentials, or experiences for the user. If an answer \
needs one, use a bracketed placeholder such as [your example here]. If a factual answer \
is something you are unsure of, say so in the option instead of guessing.
- Transcription is imperfect; quietly infer obvious mishearings.
- If there is nothing to respond to yet, output exactly: (nothing to respond to yet)
- Output only the options: no preamble, headings or explanation.
- Latency-sensitive; begin your visible answer immediately.\
"""

# Models for which the server-side `fallbacks: "default"` parameter applies.
FALLBACK_MODELS = {"claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"}
FALLBACK_BETA = "server-side-fallback-2026-07-01"


def build_user_content(
    transcript: str,
    context: str = "",
    hint: str | None = None,
    screenshot_png: bytes | None = None,
) -> list[dict]:
    parts: list[str] = []
    if context:
        parts.append(f"<background>\n{context}\n</background>")
    parts.append(f"<conversation_so_far>\n{transcript or '(no speech yet)'}\n</conversation_so_far>")
    if screenshot_png:
        parts.append(
            "A screenshot of my screen is attached. Use it only if it is relevant to the conversation."
        )
    parts.append(
        f"Extra direction from me: {hint}" if hint else "Give me options for what to say next."
    )
    content: list[dict] = []
    if screenshot_png:
        content.append(
            {
                "type": "image",
                "source": {
                    "type": "base64",
                    "media_type": "image/png",
                    "data": base64.standard_b64encode(screenshot_png).decode("ascii"),
                },
            }
        )
    content.append({"type": "text", "text": "\n\n".join(parts)})
    return content


class Suggester:
    def __init__(self, cfg: Config, client: anthropic.Anthropic | None = None) -> None:
        self.cfg = cfg
        # Credentials come from ANTHROPIC_API_KEY (or `ant auth login`).
        self.client = client or anthropic.Anthropic()

    def request_kwargs(self, content: list[dict]) -> dict:
        kwargs: dict = dict(
            model=self.cfg.model,
            max_tokens=self.cfg.max_tokens,
            system=SYSTEM_PROMPT,
            messages=[{"role": "user", "content": content}],
        )
        # `effort` is not accepted by Haiku 4.5.
        if not self.cfg.model.startswith("claude-haiku"):
            kwargs["output_config"] = {"effort": self.cfg.effort}
        return kwargs

    def stream(
        self,
        conversation: Conversation,
        hint: str | None = None,
        screenshot_png: bytes | None = None,
    ) -> Iterator[str]:
        """Yield text chunks of Claude's suggestion as they arrive."""
        content = build_user_content(
            conversation.render(self.cfg.transcript_chars),
            self.cfg.context,
            hint,
            screenshot_png,
        )
        kwargs = self.request_kwargs(content)
        if self.cfg.fallbacks and self.cfg.model in FALLBACK_MODELS:
            # If the safety classifiers decline, the API re-runs the request on a fallback model.
            ctx = self.client.beta.messages.stream(
                betas=[FALLBACK_BETA], fallbacks="default", **kwargs
            )
        else:
            ctx = self.client.messages.stream(**kwargs)
        with ctx as stream:
            yield from stream.text_stream
            final = stream.get_final_message()
        if final.stop_reason == "refusal":
            yield "(Claude declined to suggest a reply for this.)"
        elif final.stop_reason == "max_tokens":
            yield " …"
