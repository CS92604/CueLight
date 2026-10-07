using Assistant.Core;

namespace Assistant.Tests;

public class SegmenterTests
{
    const int Rate = 16000;

    static float[] Tone(double seconds, float amp = 0.2f, double freq = 220)
        => Enumerable.Range(0, (int)(Rate * seconds)).Select(i => amp * (float)Math.Sin(2 * Math.PI * freq * i / Rate)).ToArray();

    static float[] Silence(double seconds) => new float[(int)(Rate * seconds)];

    static List<float[]> FeedInChunks(Segmenter seg, params float[][] parts)
    {
        var all = parts.SelectMany(p => p).ToArray();
        var output = new List<float[]>();
        for (int i = 0; i < all.Length; i += 1600) output.AddRange(seg.Feed(all.AsSpan(i, Math.Min(1600, all.Length - i))));
        return output;
    }

    [Fact]
    public void Two_utterances_are_split_on_silence()
    {
        var utts = FeedInChunks(new Segmenter(), Silence(1), Tone(1.0), Silence(1.2), Tone(0.8), Silence(1.2));
        Assert.Equal(2, utts.Count);
        Assert.InRange(utts[0].Length / (double)Rate, 1.0, 2.0);
        Assert.InRange(utts[1].Length / (double)Rate, 0.8, 1.8);
    }

    [Fact]
    public void Short_blip_is_discarded() =>
        Assert.Empty(FeedInChunks(new Segmenter(), Silence(1), Tone(0.1), Silence(1.5)));

    [Fact]
    public void Background_hiss_is_not_speech()
    {
        var rng = new Random(0);
        var hiss = Enumerable.Range(0, Rate * 5).Select(_ => 0.003f * (float)(rng.NextDouble() * 2 - 1)).ToArray();
        Assert.Empty(FeedInChunks(new Segmenter(), hiss));
    }

    [Fact]
    public void Long_speech_is_capped()
    {
        var utts = FeedInChunks(new Segmenter(maxUtteranceS: 5), Tone(12.0), Silence(1.0));
        Assert.True(utts.Count >= 2);
        Assert.All(utts, u => Assert.True(u.Length / (double)Rate <= 5.1));
    }

    [Fact]
    public void Flush_returns_pending_speech()
    {
        var seg = new Segmenter();
        FeedInChunks(seg, Silence(0.5), Tone(1.0));
        var tail = seg.Flush();
        Assert.NotNull(tail);
        Assert.True(tail!.Length / (double)Rate > 0.9);
        Assert.Null(seg.Flush());
    }
}

public class ResamplerTests
{
    static float[] Sine(int rate, int channels, double seconds, double freq)
    {
        int n = (int)(rate * seconds);
        var data = new float[n * channels];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++) data[i * channels + c] = 0.5f * (float)Math.Sin(2 * Math.PI * freq * i / rate);
        return data;
    }

    [Theory]
    [InlineData(48000, 2)]
    [InlineData(44100, 2)]
    [InlineData(16000, 1)]
    [InlineData(32000, 1)]
    public void Output_length_matches_the_target_rate(int rate, int channels)
    {
        var r = new StreamResampler(rate, channels);
        var output = r.Process(Sine(rate, channels, 2.0, 440));
        Assert.InRange(output.Length, 31990, 32000);
    }

    [Fact]
    public void Chunked_input_gives_the_same_result_as_one_piece()
    {
        var input = Sine(44100, 2, 1.0, 300);
        var whole = new StreamResampler(44100, 2).Process(input);
        var r = new StreamResampler(44100, 2);
        var parts = new List<float>();
        for (int i = 0; i < input.Length; i += 1764) parts.AddRange(r.Process(input.AsSpan(i, Math.Min(1764, input.Length - i))));
        Assert.InRange(parts.Count, whole.Length - 1, whole.Length); // the last sample may land on the next chunk
        for (int i = 0; i < parts.Count; i++) Assert.InRange(parts[i], whole[i] - 1e-4f, whole[i] + 1e-4f);
    }

    [Fact]
    public void Tone_keeps_its_amplitude_and_downmixes_channels()
    {
        var output = new StreamResampler(48000, 2).Process(Sine(48000, 2, 1.0, 200));
        Assert.InRange(output.Max(), 0.45f, 0.51f);
    }

    [Fact]
    public void Content_above_the_new_nyquist_is_attenuated()
    {
        // 7 kHz is fine at 16 kHz, 20 kHz must not alias into the speech band at full strength.
        var low = new StreamResampler(48000, 1).Process(Sine(48000, 1, 1.0, 1000)).Max();
        var high = new StreamResampler(48000, 1).Process(Sine(48000, 1, 1.0, 20000)).Max();
        Assert.True(high < low * 0.5f, $"high={high} low={low}");
    }
}
