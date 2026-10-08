# Security

## Reporting a problem

If you find a security problem in Cuelight (for example, a way for an API key, or what was said or shown on
screen, to leak, or a way to make the app run something it shouldn't), please report it **privately**:
use **Security → Report a vulnerability** on this repository's GitHub page. Please don't open a public issue
with the details until it's fixed. If that option isn't available, open an issue that says only that you have
a security report, with no details, and a way to reach you will be arranged.

This is a small hobby project maintained in spare time, so there is no guaranteed response time and no bounty,
but reports are taken seriously.

**Never paste an API key anywhere in a report**, and revoke any key that was exposed.

## What the app does with your data

- API keys are stored encrypted with Windows' per-user protection (DPAPI) in your profile folder, one per
  provider. A key is only sent to the provider it belongs to (for "Other", the address you typed).
- Speech is transcribed on your PC. The chosen AI provider receives the transcript text and, only if you pick a
  text area, a picture of it.
- The app connects to the AI provider you choose and, once, to huggingface.co to download the speech model.
  It sends no analytics or crash reports.
- Plain `http://` addresses are refused unless they point at this PC or your own network.
- The log file (`%LOCALAPPDATA%\Cuelight\logs\app.log`) holds errors and facts about the PC, not what was said or
  shown, and anything that looks like a key or password is blanked out before it is written.

## Known limits

- The downloaded speech model is checked for size and format, not against a published checksum.
- `Cuelight.exe` is not code-signed yet, so Windows SmartScreen will warn on first run. Each release lists the
  file's SHA-256 so you can check what you downloaded.
- "Hide from screen sharing" uses a Windows setting; it doesn't hide anything from a camera, a capture card,
  or a person looking at the monitor.
