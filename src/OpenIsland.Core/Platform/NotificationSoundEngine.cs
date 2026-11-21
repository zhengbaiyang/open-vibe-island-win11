using System.Collections.Concurrent;
using System.Text;

namespace OpenIsland.Core.Platform;

/// <summary>
/// Studio-grade 48 kHz 16-bit Stereo PCM acoustic chime synthesizer.
/// Generates crystal-clear, multi-harmonic notification chimes in memory to replace
/// muddy legacy Windows system sounds while preserving backward-compatible scheme tags.
/// </summary>
public static class NotificationSoundEngine
{
    public const int SampleRate = 48_000;
    public const short Channels = 2;
    public const short BitsPerSample = 16;

    public static readonly IReadOnlyList<string> SupportedSchemes =
    [
        "Asterisk",
        "Exclamation",
        "Beep",
        "Hand",
        "Notify",
        "Ding",
        "Chimes",
        "Tada",
        "WindowsNotify"
    ];

    private static readonly ConcurrentDictionary<string, byte[]> WavCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct ChimeNote(
        double StartSec,
        double DurationSec,
        double FrequencyHz,
        double Amplitude = 1.0,
        double Brightness = 0.42,
        double Pan = 0.0,
        double AttackMs = 5.0);

    public static string NormalizeScheme(string? scheme)
    {
        var trimmed = (scheme ?? "").Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "exclamation" or "windowsexclamation" => "Exclamation",
            "beep" => "Beep",
            "hand" => "Hand",
            "notify" => "Notify",
            "ding" => "Ding",
            "chimes" => "Chimes",
            "tada" => "Tada",
            "windowsnotify" => "WindowsNotify",
            _ => "Asterisk"
        };
    }

    /// <summary>
    /// Returns a complete 48 kHz 16-bit stereo RIFF WAV buffer for the given scheme and urgency.
    /// Buffers are synthesized once and cached in memory.
    /// </summary>
    public static byte[] GetWavBytes(string? scheme, bool isUrgent = false)
    {
        var normalized = NormalizeScheme(scheme);
        var cacheKey = $"{normalized}:{(isUrgent ? "urgent" : "normal")}";
        return WavCache.GetOrAdd(cacheKey, _ => SynthesizeSchemeWav(normalized, isUrgent));
    }

    private static byte[] SynthesizeSchemeWav(string scheme, bool isUrgent)
    {
        var notes = BuildNotesForScheme(scheme, isUrgent);
        double maxEndSec = 0.25;
        foreach (var note in notes)
        {
            var end = note.StartSec + note.DurationSec + 0.045;
            if (end > maxEndSec) maxEndSec = end;
        }

        int totalFrames = (int)Math.Ceiling(maxEndSec * SampleRate);
        var left = new double[totalFrames];
        var right = new double[totalFrames];

        foreach (var note in notes)
        {
            RenderNote(note, left, right, totalFrames);
        }

        // Apply subtle 24 ms stereo cross-reflection for airy studio depth & clarity.
        int delayFrames = (int)(0.024 * SampleRate);
        const double echoGain = 0.14;
        for (int i = totalFrames - 1; i >= delayFrames; i--)
        {
            left[i] += right[i - delayFrames] * echoGain;
            right[i] += left[i - delayFrames] * echoGain;
        }

        // Smooth 16 ms fade-out at buffer tail to guarantee zero end-click.
        int fadeOutFrames = Math.Min(totalFrames / 4, (int)(0.016 * SampleRate));
        for (int i = 0; i < fadeOutFrames; i++)
        {
            int idx = totalFrames - 1 - i;
            double fade = 0.5 * (1.0 - Math.Cos(Math.PI * i / fadeOutFrames));
            left[idx] *= fade;
            right[idx] *= fade;
        }

        // Peak normalize to -1.3 dBFS (0.86 full scale) with soft saturation knee.
        double peak = 0.0001;
        for (int i = 0; i < totalFrames; i++)
        {
            double aL = Math.Abs(left[i]);
            double aR = Math.Abs(right[i]);
            if (aL > peak) peak = aL;
            if (aR > peak) peak = aR;
        }

        const double targetPeak = 0.86;
        double masterGain = targetPeak / peak;

        return EncodePcm16StereoWav(left, right, totalFrames, masterGain);
    }

    private static void RenderNote(ChimeNote note, double[] left, double[] right, int totalFrames)
    {
        int startFrame = Math.Max(0, (int)Math.Round(note.StartSec * SampleRate));
        int noteFrames = Math.Max(1, (int)Math.Round(note.DurationSec * SampleRate));
        int endFrame = Math.Min(totalFrames, startFrame + noteFrames);

        double attackSec = Math.Clamp(note.AttackMs / 1000.0, 0.002, 0.020);
        int attackFrames = Math.Max(1, (int)Math.Round(attackSec * SampleRate));
        int releaseFrames = Math.Max(1, (int)Math.Round(0.018 * SampleRate));

        // Equal-power stereo panning
        double panClamped = Math.Clamp(note.Pan, -0.85, 0.85);
        double panAngle = (panClamped + 1.0) * (Math.PI / 4.0);
        double gainL = Math.Cos(panAngle) * note.Amplitude;
        double gainR = Math.Sin(panAngle) * note.Amplitude;

        double f1 = note.FrequencyHz;
        double f2 = f1 * 2.0;          // Octave warm harmonic
        double f3 = f1 * 3.0;          // Twelfth crystal harmonic
        double f4 = f1 * 4.02;         // Slight inharmonic bell transient shimmer

        // Micro-detune between L and R (+0.32 Hz / -0.32 Hz) for rich three-dimensional clarity
        const double stereoDetuneHz = 0.32;
        double w1L = 2.0 * Math.PI * (f1 - stereoDetuneHz) / SampleRate;
        double w1R = 2.0 * Math.PI * (f1 + stereoDetuneHz) / SampleRate;
        double w2 = 2.0 * Math.PI * f2 / SampleRate;
        double w3 = 2.0 * Math.PI * f3 / SampleRate;
        double w4 = 2.0 * Math.PI * f4 / SampleRate;

        // Nyquist safety: mute upper partials if they approach 20 kHz
        double h2Amp = f2 < 18_500 ? 0.28 * (0.6 + 0.4 * note.Brightness) : 0.0;
        double h3Amp = f3 < 18_500 ? 0.14 * note.Brightness : 0.0;
        double h4Amp = f4 < 18_500 ? 0.09 * note.Brightness : 0.0;

        double bodyDecayRate = 4.6 / Math.Max(0.08, note.DurationSec);
        double octaveDecayRate = bodyDecayRate * 1.45;
        double shimmerDecayRate = bodyDecayRate * 3.1;
        double bellTransientDecayRate = bodyDecayRate * 5.8;

        for (int frame = startFrame; frame < endFrame; frame++)
        {
            int localFrame = frame - startFrame;
            double t = (double)localFrame / SampleRate;

            // Click-free raised-cosine attack ramp
            double attackEnv = localFrame < attackFrames
                ? 0.5 * (1.0 - Math.Cos(Math.PI * localFrame / attackFrames))
                : 1.0;

            // Smooth tail release ramp
            int remaining = endFrame - 1 - frame;
            double releaseEnv = remaining < releaseFrames
                ? 0.5 * (1.0 - Math.Cos(Math.PI * remaining / releaseFrames))
                : 1.0;

            double env = attackEnv * releaseEnv;
            double env1 = env * Math.Exp(-t * bodyDecayRate);
            double env2 = env * Math.Exp(-t * octaveDecayRate);
            double env3 = env * Math.Exp(-t * shimmerDecayRate);
            double env4 = env * Math.Exp(-t * bellTransientDecayRate);

            double overtones =
                h2Amp * env2 * Math.Sin(w2 * localFrame) +
                h3Amp * env3 * Math.Sin(w3 * localFrame) +
                h4Amp * env4 * Math.Sin(w4 * localFrame);

            double sampleL = env1 * Math.Sin(w1L * localFrame) + overtones;
            double sampleR = env1 * Math.Sin(w1R * localFrame) + overtones;

            left[frame] += sampleL * gainL;
            right[frame] += sampleR * gainR;
        }
    }

    private static IReadOnlyList<ChimeNote> BuildNotesForScheme(string scheme, bool isUrgent)
    {
        // Standard equal-temperament frequencies (A4 = 440 Hz) in the 523 Hz - 2637 Hz high-clarity band:
        // C5=523.25, D5=587.33, E5=659.25, F5=698.46, F#5=739.99, G5=783.99, G#5=830.61, A5=880.00, B5=987.77
        // C6=1046.50, C#6=1108.73, D6=1174.66, E6=1318.51, F6=1396.91, F#6=1479.98, G6=1567.98, G#6=1661.22, A6=1760.00, B6=1975.53
        // C7=2093.00, D7=2349.32, E7=2637.02

        return (scheme, isUrgent) switch
        {
            // 1. Asterisk — 晶莹灵动 (Crystal Island): Sparkling Cmaj9 ascending glass chime
            ("Asterisk", false) =>
            [
                new ChimeNote(0.000, 0.30, 1046.50, 0.88, Brightness: 0.48, Pan: -0.22), // C6
                new ChimeNote(0.068, 0.32, 1318.51, 0.92, Brightness: 0.50, Pan: -0.05), // E6
                new ChimeNote(0.136, 0.36, 1567.98, 0.96, Brightness: 0.52, Pan: +0.12), // G6
                new ChimeNote(0.206, 0.52, 2349.32, 1.00, Brightness: 0.45, Pan: +0.24), // D7
                new ChimeNote(0.206, 0.50, 1174.66, 0.46, Brightness: 0.30, Pan: -0.15)  // D6 warm body
            ],
            ("Asterisk", true) =>
            [
                new ChimeNote(0.000, 0.24, 1318.51, 0.92, Brightness: 0.56, Pan: -0.24), // E6
                new ChimeNote(0.062, 0.26, 1975.53, 0.96, Brightness: 0.58, Pan: +0.08), // B6
                new ChimeNote(0.128, 0.48, 2637.02, 1.00, Brightness: 0.52, Pan: +0.24), // E7
                new ChimeNote(0.128, 0.46, 1318.51, 0.52, Brightness: 0.35, Pan: -0.16)  // E6 octave body
            ],

            // 2. Exclamation — 清澈脉冲 (Clear Pulse): Articulate high-definition attention cadence
            ("Exclamation", false) =>
            [
                new ChimeNote(0.000, 0.24, 1174.66, 0.94, Brightness: 0.58, Pan: -0.20), // D6
                new ChimeNote(0.078, 0.30, 1760.00, 1.00, Brightness: 0.60, Pan: +0.20), // A6
                new ChimeNote(0.175, 0.46, 2349.32, 0.96, Brightness: 0.52, Pan: +0.00), // D7
                new ChimeNote(0.175, 0.44, 1174.66, 0.48, Brightness: 0.32, Pan: -0.10)  // D6
            ],
            ("Exclamation", true) =>
            [
                new ChimeNote(0.000, 0.18, 1174.66, 0.92, Brightness: 0.62, Pan: -0.25), // D6
                new ChimeNote(0.064, 0.20, 1760.00, 0.98, Brightness: 0.64, Pan: +0.22), // A6
                new ChimeNote(0.148, 0.18, 1174.66, 0.94, Brightness: 0.62, Pan: -0.20), // D6
                new ChimeNote(0.212, 0.46, 2349.32, 1.00, Brightness: 0.58, Pan: +0.20), // D7
                new ChimeNote(0.212, 0.44, 1174.66, 0.50, Brightness: 0.35, Pan: -0.12)
            ],

            // 3. Beep — 纯净双音 (Pure Duo): Clean, minimalist studio reference two-tone cue
            ("Beep", false) =>
            [
                new ChimeNote(0.000, 0.24, 987.77,  0.90, Brightness: 0.30, Pan: -0.15), // B5
                new ChimeNote(0.082, 0.44, 1479.98, 1.00, Brightness: 0.34, Pan: +0.15), // F#6
                new ChimeNote(0.082, 0.42, 739.99,  0.38, Brightness: 0.20, Pan: -0.05)  // F#5 body
            ],
            ("Beep", true) =>
            [
                new ChimeNote(0.000, 0.18, 987.77,  0.90, Brightness: 0.38, Pan: -0.18), // B5
                new ChimeNote(0.068, 0.22, 1479.98, 0.96, Brightness: 0.42, Pan: +0.16), // F#6
                new ChimeNote(0.142, 0.42, 1975.53, 1.00, Brightness: 0.40, Pan: +0.00)  // B6
            ],

            // 4. Hand — 醒目双铃 (Double Bell): Crisp rhythmic alert bell for immediate attention
            ("Hand", false) =>
            [
                new ChimeNote(0.000, 0.24, 1567.98, 0.96, Brightness: 0.60, Pan: -0.22), // G6
                new ChimeNote(0.085, 0.24, 1174.66, 0.90, Brightness: 0.54, Pan: +0.18), // D6
                new ChimeNote(0.170, 0.48, 1567.98, 1.00, Brightness: 0.62, Pan: +0.00), // G6
                new ChimeNote(0.170, 0.46, 783.99,  0.48, Brightness: 0.32, Pan: -0.10)  // G5
            ],
            ("Hand", true) =>
            [
                new ChimeNote(0.000, 0.20, 1760.00, 0.96, Brightness: 0.65, Pan: -0.24), // A6
                new ChimeNote(0.072, 0.20, 1318.51, 0.92, Brightness: 0.58, Pan: +0.20), // E6
                new ChimeNote(0.144, 0.22, 1760.00, 0.98, Brightness: 0.65, Pan: -0.16), // A6
                new ChimeNote(0.216, 0.48, 2093.00, 1.00, Brightness: 0.60, Pan: +0.18), // C7
                new ChimeNote(0.216, 0.46, 1046.50, 0.52, Brightness: 0.36, Pan: -0.10)
            ],

            // 5. Notify — 极光和弦 (Aurora Chord): Warm Fmaj9 lush arpeggio
            ("Notify", false) =>
            [
                new ChimeNote(0.000, 0.38, 698.46,  0.84, Brightness: 0.36, Pan: -0.26), // F5
                new ChimeNote(0.058, 0.38, 880.00,  0.88, Brightness: 0.38, Pan: -0.10), // A5
                new ChimeNote(0.116, 0.42, 1046.50, 0.94, Brightness: 0.42, Pan: +0.08), // C6
                new ChimeNote(0.176, 0.54, 1567.98, 1.00, Brightness: 0.44, Pan: +0.24), // G6
                new ChimeNote(0.176, 0.50, 1318.51, 0.52, Brightness: 0.32, Pan: -0.12)  // E6 maj7 shimmer
            ],
            ("Notify", true) =>
            [
                new ChimeNote(0.000, 0.30, 880.00,  0.88, Brightness: 0.45, Pan: -0.22), // A5
                new ChimeNote(0.055, 0.32, 1046.50, 0.92, Brightness: 0.48, Pan: -0.04), // C6
                new ChimeNote(0.110, 0.36, 1318.51, 0.96, Brightness: 0.50, Pan: +0.12), // E6
                new ChimeNote(0.166, 0.52, 1975.53, 1.00, Brightness: 0.48, Pan: +0.24)  // B6
            ],

            // 6. Ding — 清脆木琴 (Crisp Marimba): Articulate acoustic glass-marimba fifth interval
            ("Ding", false) =>
            [
                new ChimeNote(0.000, 0.26, 880.00,  0.90, Brightness: 0.55, Pan: -0.18, AttackMs: 3.5), // A5
                new ChimeNote(0.072, 0.50, 1318.51, 1.00, Brightness: 0.58, Pan: +0.18, AttackMs: 3.5), // E6
                new ChimeNote(0.072, 0.46, 1760.00, 0.42, Brightness: 0.40, Pan: -0.06, AttackMs: 4.0)  // A6 overtone
            ],
            ("Ding", true) =>
            [
                new ChimeNote(0.000, 0.22, 1046.50, 0.90, Brightness: 0.60, Pan: -0.22, AttackMs: 3.5), // C6
                new ChimeNote(0.062, 0.24, 1567.98, 0.95, Brightness: 0.62, Pan: +0.06, AttackMs: 3.5), // G6
                new ChimeNote(0.126, 0.48, 2093.00, 1.00, Brightness: 0.56, Pan: +0.22, AttackMs: 3.5)  // C7
            ],

            // 7. Chimes — 空灵风铃 (Ethereal Chimes): Pentatonic cascading celesta shimmer
            ("Chimes", false) =>
            [
                new ChimeNote(0.000, 0.38, 783.99,  0.78, Brightness: 0.50, Pan: -0.30), // G5
                new ChimeNote(0.055, 0.38, 880.00,  0.82, Brightness: 0.52, Pan: -0.15), // A5
                new ChimeNote(0.110, 0.42, 1046.50, 0.88, Brightness: 0.54, Pan: +0.00), // C6
                new ChimeNote(0.165, 0.46, 1318.51, 0.94, Brightness: 0.56, Pan: +0.15), // E6
                new ChimeNote(0.220, 0.56, 1567.98, 1.00, Brightness: 0.52, Pan: +0.30), // G6
                new ChimeNote(0.275, 0.54, 2093.00, 0.72, Brightness: 0.46, Pan: -0.10)  // C7 top sparkle
            ],
            ("Chimes", true) =>
            [
                new ChimeNote(0.000, 0.32, 1046.50, 0.84, Brightness: 0.56, Pan: -0.28), // C6
                new ChimeNote(0.050, 0.34, 1174.66, 0.88, Brightness: 0.58, Pan: -0.10), // D6
                new ChimeNote(0.100, 0.38, 1318.51, 0.92, Brightness: 0.60, Pan: +0.08), // E6
                new ChimeNote(0.150, 0.44, 1567.98, 0.96, Brightness: 0.60, Pan: +0.22), // G6
                new ChimeNote(0.200, 0.54, 2349.32, 1.00, Brightness: 0.54, Pan: +0.00)  // D7
            ],

            // 8. Tada — 欢快和弦 (Bright Fanfare): Uplifting completion fanfare with blooming triad
            ("Tada", _) =>
            [
                new ChimeNote(0.000, 0.24, 587.33,  0.82, Brightness: 0.44, Pan: -0.24), // D5
                new ChimeNote(0.065, 0.26, 880.00,  0.88, Brightness: 0.48, Pan: -0.08), // A5
                new ChimeNote(0.130, 0.30, 1108.73, 0.92, Brightness: 0.50, Pan: +0.10), // C#6
                // Blooming Dadd9 chord at 0.195s
                new ChimeNote(0.195, 0.58, 1174.66, 0.78, Brightness: 0.45, Pan: -0.22), // D6
                new ChimeNote(0.195, 0.58, 1479.98, 0.82, Brightness: 0.48, Pan: +0.18), // F#6
                new ChimeNote(0.195, 0.60, 1760.00, 1.00, Brightness: 0.52, Pan: +0.00)  // A6
            ],

            // 9. WindowsNotify — 现代露珠 (Modern Dewdrop): Fluent Windows 11 Emaj9 dewdrop chime
            ("WindowsNotify", false) =>
            [
                new ChimeNote(0.000, 0.34, 659.25,  0.84, Brightness: 0.42, Pan: -0.24), // E5
                new ChimeNote(0.064, 0.38, 987.77,  0.90, Brightness: 0.45, Pan: -0.04), // B5
                new ChimeNote(0.128, 0.46, 1479.98, 0.96, Brightness: 0.48, Pan: +0.16), // F#6
                new ChimeNote(0.196, 0.54, 1661.22, 1.00, Brightness: 0.46, Pan: +0.22), // G#6
                new ChimeNote(0.196, 0.52, 830.61,  0.46, Brightness: 0.30, Pan: -0.14)  // G#5 warm base
            ],
            ("WindowsNotify", true) =>
            [
                new ChimeNote(0.000, 0.26, 987.77,  0.88, Brightness: 0.52, Pan: -0.22), // B5
                new ChimeNote(0.060, 0.30, 1479.98, 0.94, Brightness: 0.55, Pan: +0.06), // F#6
                new ChimeNote(0.122, 0.50, 1975.53, 1.00, Brightness: 0.52, Pan: +0.22), // B6
                new ChimeNote(0.122, 0.48, 1661.22, 0.54, Brightness: 0.38, Pan: -0.14)  // G#6
            ],

            _ =>
            [
                new ChimeNote(0.000, 0.30, 1046.50, 0.90, Brightness: 0.48, Pan: -0.18),
                new ChimeNote(0.075, 0.48, 1567.98, 1.00, Brightness: 0.50, Pan: +0.18)
            ]
        };
    }

    private static byte[] EncodePcm16StereoWav(double[] left, double[] right, int totalFrames, double masterGain)
    {
        int bytesPerSample = BitsPerSample / 8;
        int blockAlign = Channels * bytesPerSample;
        int byteRate = SampleRate * blockAlign;
        int dataSize = totalFrames * blockAlign;
        int fileSize = 44 + dataSize;

        var buffer = new byte[fileSize];
        using var ms = new MemoryStream(buffer, writable: true);
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // RIFF header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(fileSize - 8);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);               // PCM chunk size
        writer.Write((short)1);         // AudioFormat = 1 (Linear PCM)
        writer.Write(Channels);
        writer.Write(SampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write(BitsPerSample);

        // data subchunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (int i = 0; i < totalFrames; i++)
        {
            double sL = SoftLimit(left[i] * masterGain);
            double sR = SoftLimit(right[i] * masterGain);

            short pcmL = (short)Math.Clamp((int)Math.Round(sL * 32767.0), short.MinValue, short.MaxValue);
            short pcmR = (short)Math.Clamp((int)Math.Round(sR * 32767.0), short.MinValue, short.MaxValue);

            writer.Write(pcmL);
            writer.Write(pcmR);
        }

        writer.Flush();
        return buffer;
    }

    private static double SoftLimit(double x)
    {
        // Clean linear response up to 0.82, smooth tanh knee above 0.82 so peaks never hard-clip.
        const double threshold = 0.82;
        double abs = Math.Abs(x);
        if (abs <= threshold) return x;
        double excess = (abs - threshold) / (1.0 - threshold);
        double compressed = threshold + (1.0 - threshold) * Math.Tanh(excess);
        return Math.Sign(x) * Math.Min(0.985, compressed);
    }
}
