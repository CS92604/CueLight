using System.Runtime.InteropServices;

namespace Cuelight.Core;

/// <summary>Turns the raw bytes a sound device delivers into floating-point samples (-1 to 1).</summary>
public static class PcmDecoder
{
    /// <summary>
    /// Decodes interleaved samples. Handles 16, 24 and 32-bit integer PCM and 32-bit float, which
    /// covers every shared-mode format Windows hands out. Returns false for anything else, so the
    /// caller can say so instead of silently hearing nothing. A trailing partial sample is ignored.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, int bitsPerSample, bool isFloat, out float[] samples)
    {
        switch (bitsPerSample)
        {
            case 32 when isFloat:
            {
                var f = MemoryMarshal.Cast<byte, float>(bytes[..(bytes.Length - bytes.Length % 4)]);
                samples = f.ToArray();
                return true;
            }
            case 32:
            {
                var s32 = MemoryMarshal.Cast<byte, int>(bytes[..(bytes.Length - bytes.Length % 4)]);
                samples = new float[s32.Length];
                for (int i = 0; i < samples.Length; i++) samples[i] = s32[i] / 2147483648f;
                return true;
            }
            case 24:
            {
                samples = new float[bytes.Length / 3];
                for (int i = 0; i < samples.Length; i++)
                {
                    int v = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); // sign-extend
                    samples[i] = v / 8388608f;
                }
                return true;
            }
            case 16:
            {
                var s16 = MemoryMarshal.Cast<byte, short>(bytes[..(bytes.Length - bytes.Length % 2)]);
                samples = new float[s16.Length];
                for (int i = 0; i < samples.Length; i++) samples[i] = s16[i] / 32768f;
                return true;
            }
            default:
                samples = Array.Empty<float>();
                return false;
        }
    }
}
