# Third-party notices

Cuelight itself is under the [MIT license](LICENSE). The `Cuelight.exe` it builds contains, or downloads, the
open-source components below, each under its own license and copyright. This list is kept with the source so
that anyone who gets only the `.exe` can still find it here.

## Components built into `Cuelight.exe`

| Component | What it is used for | License | Copyright |
|---|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) and the `System.*` / `Microsoft.*` libraries | Runs the app (carried inside the `.exe`) | MIT | .NET Foundation and contributors; Microsoft Corporation |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (`Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Skia`, `.Win32`, `.Native`, `.X11`, `.FreeDesktop`, `.Remote.Protocol`, `.BuildServices`) | The interface | MIT | The AvaloniaUI Project |
| [ANGLE](https://github.com/AvaloniaUI/Avalonia) (`Avalonia.Angle.Windows.Natives`) | Draws the interface through Direct3D | BSD 3-Clause | The ANGLE Project Authors (Google) |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and [HarfBuzzSharp](https://github.com/mono/SkiaSharp), with the native Skia, HarfBuzz and FreeType libraries | Drawing and text shaping | MIT (bindings); BSD 3-Clause (Skia); MIT (HarfBuzz); FreeType License | Xamarin, Inc. and Microsoft Corporation; Google; the HarfBuzz and FreeType authors. Their own notices are in the SkiaSharp package's `THIRD-PARTY-NOTICES.txt`. |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | COM access used by Avalonia | MIT | Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | Linux desktop integration (unused on Windows) | MIT | Tom Deseyn |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View-model helpers | MIT | .NET Foundation and Contributors |
| [NAudio](https://github.com/naudio/NAudio) (`NAudio.Core`, `NAudio.Wasapi`) | Hearing what the PC plays, and the microphone | MIT | Mark Heath |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | Runs speech recognition from .NET | MIT | sandrohanea |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) (the native libraries in `Whisper.net.Runtime` and `Whisper.net.Runtime.NoAvx`) | Speech recognition on your PC | MIT | The ggml authors |
| [Anthropic .NET SDK](https://github.com/anthropics/anthropic-sdk-csharp) | Talks to Claude | MIT | Anthropic |
| Microsoft Visual C++ runtime (`vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`, `vcomp140.dll`) | Needed by the speech engine's native libraries | Microsoft's [redistributable terms](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files) | Microsoft Corporation |

## Fonts

| Font | License | Copyright |
|---|---|---|
| [Inter](https://rsms.me/inter/) (carried in the `Avalonia.Fonts.Inter` package) | SIL Open Font License 1.1 | The Inter Project Authors |
| [Young Serif](https://github.com/noirblancrouge/YoungSerif) | SIL Open Font License 1.1: the text is in [`src/Cuelight.App/Assets/Fonts/LICENSE-YoungSerif.txt`](src/Cuelight.App/Assets/Fonts/LICENSE-YoungSerif.txt) | The Young Serif Project Authors |

## Downloaded on first run, not part of the `.exe`

| Component | License | Copyright |
|---|---|---|
| The Whisper speech models (`ggml-tiny.en.bin`, `ggml-base.en.bin`, `ggml-small.en.bin`) from [huggingface.co/ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp): OpenAI's [Whisper](https://github.com/openai/whisper) weights in whisper.cpp's format | MIT | OpenAI |

## The MIT license

The components marked MIT above are each under the following terms, with the copyright holder named in the table:

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The full text of the BSD 3-Clause license for ANGLE ships in the `Avalonia.Angle.Windows.Natives` package, and the
SIL Open Font License 1.1 is at <https://openfontlicense.org>.
