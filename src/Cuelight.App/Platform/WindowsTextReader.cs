using System.Runtime.Versioning;
using Cuelight.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Cuelight.App.Platform;

/// <summary>
/// Reads the words in a screen region with Windows' own text recognition (the engine behind the Snipping Tool's "Text
/// actions"). It runs on this PC, sends nothing anywhere, and needs a recognition language installed in Windows, which
/// the PC's usual display language comes with.
/// </summary>
[SupportedOSPlatform("windows10.0.14393.0")]
public sealed class WindowsTextReader : ITextReader
{
    private readonly Lazy<OcrEngine?> _engine = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable => _engine.Value is not null;

    /// <summary>Starts loading the recognition engine in the background, so the first read isn't the slow one.</summary>
    public void WarmUp() => _ = Task.Run(() => _engine.Value);

    private static OcrEngine? Create()
    {
        try
        {
            // The languages the user has set up, then English, then whatever Windows can recognise at all.
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null && OcrEngine.IsLanguageSupported(new Language("en-US"))) engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (engine is null)
                foreach (var language in OcrEngine.AvailableRecognizerLanguages)
                    if ((engine = OcrEngine.TryCreateFromLanguage(language)) is not null) break;
            AppLog.Info(engine is null
                ? "Windows has no text recognition installed, so Fast screen reading will send a picture instead."
                : $"Text recognition ready ({engine.RecognizerLanguage.LanguageTag}).");
            return engine;
        }
        catch (Exception ex)
        {
            AppLog.Error("Couldn't start Windows' text recognition", ex);
            return null;
        }
    }

    public async Task<string> ReadAsync(byte[] bgra, int width, int height, CancellationToken ct)
    {
        var engine = _engine.Value ?? throw new InvalidOperationException("Windows has no text recognition installed for your language.");
        if (width < 1 || height < 1 || bgra.Length < width * height * 4) throw new ArgumentException("The picture of the text area is empty.");
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException("The text area is too large to read.");

        // Recognition works best on dark text on a light background, so a mostly dark area (dark mode) is read inverted.
        bool dark = MeanBrightness(bgra) < 110;
        var text = await RecognizeAsync(engine, dark ? Inverted(bgra) : bgra, width, height, ct);
        if (text.Count(char.IsLetterOrDigit) < 2)   // nothing found: try the other way round once
            text = await RecognizeAsync(engine, dark ? bgra : Inverted(bgra), width, height, ct);
        return text;
    }

    private static async Task<string> RecognizeAsync(OcrEngine engine, byte[] bgra, int width, int height, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var writer = new DataWriter();
        writer.WriteBytes(bgra);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap);
        ct.ThrowIfCancellationRequested();
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }

    /// <summary>The average of the red, green and blue values (0-255), sampling a scattering of pixels.</summary>
    internal static double MeanBrightness(byte[] bgra)
    {
        int pixels = bgra.Length / 4;
        if (pixels == 0) return 255;
        int step = Math.Max(1, pixels / 4000);
        long sum = 0;
        int count = 0;
        for (int p = 0; p < pixels; p += step, count++)
            sum += bgra[p * 4] + bgra[p * 4 + 1] + bgra[p * 4 + 2];
        return sum / (3.0 * count);
    }

    internal static byte[] Inverted(byte[] bgra)
    {
        var copy = new byte[bgra.Length];
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            copy[i] = (byte)(255 - bgra[i]);
            copy[i + 1] = (byte)(255 - bgra[i + 1]);
            copy[i + 2] = (byte)(255 - bgra[i + 2]);
            copy[i + 3] = 255;
        }
        return copy;
    }
}
