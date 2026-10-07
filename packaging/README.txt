Claude Live Assistant
=====================

1. Double-click ClaudeLiveAssistant.exe. There is nothing to install.
2. Paste your Claude API key when asked. That is the only setup.
   Get a key at https://console.anthropic.com/settings/keys
3. The first launch downloads a speech model (about 140 MB, once; 75 MB on PCs with few
   processor cores). Rest the mouse on any button or option to see what it does.

Works on 64-bit Windows 10 (version 1607 or later) and Windows 11, Intel/AMD. It carries
everything it needs (the .NET runtime and the Visual C++ runtime), and it also runs on
processors without AVX2 (older and low-end PCs), just more slowly. On ARM PCs, Windows runs it
through its built-in x64 emulation. "Hide from screen sharing" needs Windows 10 version 2004+.

Windows may say "Windows protected your PC" because the app is not code-signed yet.
Click "More info", then "Run anyway".

If something doesn't work
-------------------------
* A log of problems (never what was said, shown on screen, or your key) is kept in
  %LOCALAPPDATA%\Claude Live Assistant\logs\app.log
* To check this PC, run:  ClaudeLiveAssistant.exe --self-test --out C:\temp\check
  then read C:\temp\check\self-test.txt
* The window doesn't appear, or is black: the app switches to drawing with the CPU after a
  start that didn't finish. To force it: ClaudeLiveAssistant.exe --software-rendering
* The speech model can't download (blocked network): download ggml-base.en.bin from
  https://huggingface.co/ggerganov/whisper.cpp and put it in
  %LOCALAPPDATA%\Claude Live Assistant\models  (ggml-tiny.en.bin for Fast,
  ggml-small.en.bin for Accurate).
* No sound is heard: the app listens to your default speakers/headphones and follows them when
  you switch. It says in the status line when none are available.

To uninstall, delete this folder. Your settings and encrypted key live in
%APPDATA%\Claude Live Assistant, and the speech model and log in
%LOCALAPPDATA%\Claude Live Assistant (delete those folders too if you want everything gone).

Source and documentation: https://github.com/CS92604/Claude
