# Contributing

Thanks for taking a look. Bug reports, fixes, and small improvements are welcome.

## Before you start

- **Bugs:** open an issue using the bug form. It asks for the things that help most (your Windows version, which AI
  provider, and the log). Remove any API key before pasting anything.
- **Bigger changes:** open an issue first to check it fits. Cuelight is deliberately small: a Windows app that
  listens, optionally watches a screen area, and suggests what to say or type.
- **Security problems:** see [SECURITY.md](SECURITY.md), not a public issue.

## Build and test

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download). Everything except actually hearing audio and
reading the screen works on any OS.

```bash
dotnet test                              # all tests, including headless UI tests
dotnet run --project src/Cuelight.App    # run it
```

- `src/Cuelight.Core` holds everything that isn't UI or Windows-specific; keep new logic there so it can be
  tested without a screen.
- Tests are expected with a change. The UI tests render windows headlessly and also measure the layout, so a
  change that pushes something off the edge fails a test. To look at the pictures they produce, set
  `SCREENSHOT_DIR` to a folder before running the app tests.
- Keep to the style of the code around your change (naming, comment density, how errors are worded for the
  user: plain language, saying what to do next).
- Colours and fonts live in `src/Cuelight.App/Styles/Theme.axaml`. Fonts must be under an open license that
  allows bundling, with the license text beside the font file; the tests check that.
- Please don't add logos, icons or fonts that belong to another company.

## Pull requests

- One change per pull request, with a short description of what and why.
- CI (the **Build** workflow) runs the tests on Linux and Windows, builds the single `.exe`, and runs a real-Windows
  self-test of it. It needs to be green.
- By contributing you agree your work is released under the project's [MIT license](LICENSE).
- AI-assisted contributions are fine (most of this project was made that way); you're still responsible for
  checking that the change does what it says.
