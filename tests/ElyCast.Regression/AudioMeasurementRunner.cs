using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Elysium_Cast_IPTV.Services.Audio;

internal static class AudioMeasurementRunner
{
    private const int SampleRate = 48_000;
    private const int Seconds = 12;
    private const int ImpulseSample = SampleRate * 4 + 100;

    public static int Run(string libMpvDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var input = Path.Combine(outputDirectory, "elysound-input.wav");
        WriteWave(input, GenerateSignal());

        var rows = new List<Measurement>();
        var entries = new List<(string Id, Elysium_Cast_IPTV.Models.ElySoundProfile? Profile)>
        {
            ("off", null)
        };
        entries.AddRange(ElySoundCatalog.BuiltIn.Select(profile =>
            (profile.Id, (Elysium_Cast_IPTV.Models.ElySoundProfile?)profile)));
        foreach (var entry in entries)
        {
            var output = Path.Combine(outputDirectory, "elysound-" + entry.Id + ".wav");
            var render = Render(libMpvDirectory, input, output, entry.Profile);
            if (!render.Success)
            {
                Console.Error.WriteLine($"FAIL  {entry.Id}: {render.Message}");
                return 1;
            }
            var samples = ReadWave(output);
            var measurement = Measure(entry.Id, samples);
            rows.Add(measurement);
            Console.WriteLine(measurement.ToString());
        }

        var report = Path.Combine(outputDirectory, "elysound-measurements.tsv");
        File.WriteAllLines(report,
            new[] { "preset\tpeak_dbfs\ttrue_peak_4x_dbtp\trms_dbfs\tlufs_integrated_gated\tmax_momentary_lufs\tmax_short_term_lufs\tlra_lu\tcorrelation\tmono_peak_dbfs\tlatency_samples" }
                .Concat(rows.Select(row => row.ToTsv())));

        foreach (var channels in new[] { 1, 6 })
        {
            var layoutInput = Path.Combine(outputDirectory, $"elysound-input-{channels}ch.wav");
            var layoutOutput = Path.Combine(outputDirectory, $"elysound-output-{channels}ch.wav");
            WriteWave(layoutInput, GenerateLayoutSignal(channels), channels);
            var layoutResult = Render(libMpvDirectory, layoutInput, layoutOutput, ElySoundCatalog.BuiltIn[1]);
            if (!layoutResult.Success)
            {
                Console.Error.WriteLine($"FAIL  layout {channels}ch: {layoutResult.Message}");
                return 1;
            }
            Console.WriteLine($"PASS  layout {channels}ch: graphe accepté sans élargissement stéréo");
        }
        Console.WriteLine("REPORT " + report);
        return 0;
    }

    public static int RunReferences(string libMpvDirectory, string outputDirectory, IReadOnlyList<string> inputs)
    {
        Directory.CreateDirectory(outputDirectory);
        var rows = new List<string>
        {
            "reference\tpreset\tpeak_dbfs\ttrue_peak_4x_dbtp\trms_dbfs\tlufs_integrated_gated\tmax_momentary_lufs\tmax_short_term_lufs\tlra_lu\tcorrelation\tmono_peak_dbfs\tlatency_samples"
        };
        foreach (var input in inputs)
        {
            if (!File.Exists(input)) { Console.Error.WriteLine("MISSING " + input); return 2; }
            var reference = Path.GetFileNameWithoutExtension(input);
            var profiles = new List<(string Id, Elysium_Cast_IPTV.Models.ElySoundProfile? Profile)> { ("off", null) };
            profiles.AddRange(ElySoundCatalog.BuiltIn.Select(profile => (profile.Id, (Elysium_Cast_IPTV.Models.ElySoundProfile?)profile)));
            foreach (var entry in profiles)
            {
                var output = Path.Combine(outputDirectory, $"reference-{Sanitize(reference)}-{entry.Id}.wav");
                var render = Render(libMpvDirectory, Path.GetFullPath(input), output, entry.Profile);
                if (!render.Success) { Console.Error.WriteLine($"FAIL {reference}/{entry.Id}: {render.Message}"); return 1; }
                var measurement = Measure(entry.Id, ReadWave(output));
                rows.Add(reference + "\t" + measurement.ToTsv());
                Console.WriteLine($"REFERENCE {reference} {measurement}");
            }
        }
        var report = Path.Combine(outputDirectory, "elysound-reference-measurements.tsv");
        File.WriteAllLines(report, rows);
        Console.WriteLine("REPORT " + report);
        return 0;
    }

    public static int RunCandidates(string libMpvDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var input = Path.Combine(outputDirectory, "elysound-input.wav");
        if (!File.Exists(input)) { WriteWave(input, GenerateSignal()); }
        var candidates = ElySoundCatalog.BuiltIn.Select(Copy).ToList();
        foreach (var profile in candidates) profile.LimiterCeilingDb = -2.0;
        var voice = candidates.Single(profile => profile.Id == "voice");
        voice.Mid = 4; voice.Presence = 4; voice.Clarity = 1;
        var night = candidates.Single(profile => profile.Id == "night");
        night.Preamp = 2; night.Bass = -5; night.Mid = 3; night.Presence = 3;
        var bass = candidates.Single(profile => profile.Id == "bass");
        bass.Preamp = -1; bass.Bass = 6;

        var rows = new List<string> { "preset\tpeak_dbfs\ttrue_peak_4x_dbtp\trms_dbfs\tlufs_integrated_gated\tmax_momentary_lufs\tmax_short_term_lufs\tlra_lu\tcorrelation\tmono_peak_dbfs\tlatency_samples" };
        foreach (var profile in candidates)
        {
            var output = Path.Combine(outputDirectory, "candidate-" + profile.Id + ".wav");
            var render = Render(libMpvDirectory, input, output, profile);
            if (!render.Success) { Console.Error.WriteLine("FAIL " + profile.Id + ": " + render.Message); return 1; }
            var measurement = Measure(profile.Id, ReadWave(output)); rows.Add(measurement.ToTsv()); Console.WriteLine("CANDIDATE " + measurement);
        }
        var report = Path.Combine(outputDirectory, "elysound-candidate-measurements.tsv"); File.WriteAllLines(report, rows); Console.WriteLine("REPORT " + report);
        return 0;
    }

    public static int RunEbur128(string libMpvDirectory, string outputDirectory, IReadOnlyList<string> inputs)
    {
        Directory.CreateDirectory(outputDirectory);
        var lines = new List<string>();
        foreach (var input in inputs)
        {
            using var mpv = new LibMpv(libMpvDirectory);
            foreach (var option in new[] { ("config", "no"), ("terminal", "no"), ("vo", "null"), ("vid", "no"), ("ao", "null"), ("untimed", "no"), ("idle", "yes") })
                if (!mpv.SetOption(option.Item1, option.Item2)) return 1;
            if (!mpv.Initialize() || !mpv.RequestLogMessages("info")) return 1;
            if (!mpv.Command("af", "add", "@ebumeter:lavfi=[ebur128=metadata=1:peak=true]") || !mpv.Command("loadfile", Path.GetFullPath(input), "replace")) return 1;
            var captured = new List<string>();
            string lastMetadata = "";
            for (var i = 0; i < 2000; i++)
            {
                captured.AddRange(mpv.DrainLogMessages());
                var metadata = mpv.Get("af-metadata/ebumeter");
                if (!string.IsNullOrWhiteSpace(metadata) && !string.Equals(metadata, lastMetadata, StringComparison.Ordinal))
                {
                    lastMetadata = metadata;
                    captured.Add("af-metadata/ebumeter: " + metadata);
                }
                if (string.Equals(mpv.Get("eof-reached"), "yes", StringComparison.OrdinalIgnoreCase)) break;
                Thread.Sleep(5);
            }
            captured.AddRange(mpv.DrainLogMessages());
            lines.Add("=== " + Path.GetFileName(input) + " ===");
            lines.AddRange(captured);
        }
        var report = Path.Combine(outputDirectory, "ffmpeg-ebur128.log"); File.WriteAllLines(report, lines); Console.WriteLine(string.Join(Environment.NewLine, lines));
        return lines.Count > inputs.Count ? 0 : 1;
    }

    private static Elysium_Cast_IPTV.Models.ElySoundProfile Copy(Elysium_Cast_IPTV.Models.ElySoundProfile source) => new()
    {
        Version = 2, Id = source.Id, Name = source.Name, Preamp = source.Preamp, Bass = source.Bass,
        LowMid = source.LowMid, Mid = source.Mid, Presence = source.Presence, Treble = source.Treble,
        Clarity = source.Clarity, Width = source.Width, Compressor = source.Compressor,
        LimiterCeilingDb = source.LimiterCeilingDb
    };

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static RenderResult Render(string libMpvDirectory, string input, string output,
        Elysium_Cast_IPTV.Models.ElySoundProfile? profile)
    {
        if (File.Exists(output)) File.Delete(output);
        using var mpv = new LibMpv(libMpvDirectory);
        foreach (var option in new[]
        {
            ("config", "no"), ("terminal", "no"), ("vo", "null"), ("vid", "no"),
            ("ao", "pcm"), ("ao-pcm-file", output), ("ao-pcm-waveheader", "yes"),
            ("audio-format", "s16"), ("audio-samplerate", SampleRate.ToString(CultureInfo.InvariantCulture)),
            ("untimed", "yes"), ("pause", "yes"), ("keep-open", "yes"), ("idle", "yes")
        })
            if (!mpv.SetOption(option.Item1, option.Item2)) return new(false, "option refusée: " + option.Item1);
        if (!mpv.Initialize()) return new(false, "mpv_initialize a échoué");
        if (!mpv.Command("loadfile", input, "replace")) return new(false, "loadfile refusé");

        for (var i = 0; i < 200 && string.IsNullOrEmpty(mpv.Get("audio-params/channel-count")); i++)
            Thread.Sleep(10);
        if (profile != null)
        {
            var controller = new ElySoundController(mpv.Get, mpv.Command);
            var result = controller.Apply(profile, enabled: true, virtualSurround: false);
            if (!result.Applied) return new(false, result.Message);
        }

        if (!mpv.SetProperty("pause", "no")) return new(false, "resume refusé");
        for (var i = 0; i < 1000; i++)
        {
            if (string.Equals(mpv.Get("eof-reached"), "yes", StringComparison.OrdinalIgnoreCase))
                break;
            Thread.Sleep(10);
        }
        mpv.Command("stop");
        return File.Exists(output) && new FileInfo(output).Length > 44
            ? new(true, "ok")
            : new(false, "sortie PCM absente");
    }

    private static float[] GenerateSignal()
    {
        var result = new float[SampleRate * Seconds * 2];
        var random = new Random(0x454C59);
        double pinkL = 0, pinkR = 0;
        for (var i = 0; i < SampleRate * Seconds; i++)
        {
            var t = i / (double)SampleRate;
            double left, right;
            if (t < 2)
            {
                pinkL = pinkL * 0.985 + (random.NextDouble() * 2 - 1) * 0.015;
                pinkR = pinkR * 0.985 + (random.NextDouble() * 2 - 1) * 0.015;
                left = pinkL * 2.8; right = pinkR * 2.8;
            }
            else if (t < 4)
            {
                var local = t - 2;
                var f0 = 20.0;
                var k = Math.Log(20_000.0 / f0) / 2.0;
                var phase = 2 * Math.PI * f0 * (Math.Exp(k * local) - 1) / k;
                left = Math.Sin(phase) * 0.45; right = Math.Sin(phase + 0.2) * 0.45;
            }
            else if (t < 5)
            {
                left = right = i == ImpulseSample ? 0.95 : 0;
            }
            else if (t < 7)
            {
                left = right = Math.Sin(2 * Math.PI * 440 * t) * 0.35;
            }
            else if (t < 9)
            {
                left = Math.Sin(2 * Math.PI * 700 * t) * 0.35; right = -left;
            }
            else if (t < 10.5)
            {
                left = Math.Sin(2 * Math.PI * 180 * t) * 0.035;
                right = Math.Sin(2 * Math.PI * 1200 * t) * 0.035;
            }
            else
            {
                var transient = i % 4800 < 24 ? 0.92 * Math.Exp(-(i % 4800) / 7.0) : 0;
                left = transient + Math.Sin(2 * Math.PI * 90 * t) * 0.12;
                right = transient + Math.Sin(2 * Math.PI * 3000 * t) * 0.08;
            }
            result[i * 2] = (float)Math.Clamp(left, -0.98, 0.98);
            result[i * 2 + 1] = (float)Math.Clamp(right, -0.98, 0.98);
        }
        return result;
    }

    private static float[] GenerateLayoutSignal(int channels)
    {
        var result = new float[SampleRate * channels];
        for (var i = 0; i < SampleRate; i++)
            for (var channel = 0; channel < channels; channel++)
                result[i * channels + channel] = (float)(0.2 * Math.Sin(2 * Math.PI * (180 + channel * 170) * i / SampleRate));
        return result;
    }

    private static Measurement Measure(string id, float[] interleaved)
    {
        double peak = 0, sum = 0, sumL = 0, sumR = 0, sumLR = 0, monoPeak = 0;
        var left = new double[interleaved.Length / 2];
        var right = new double[left.Length];
        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i] = interleaved[i * 2];
            var r = right[i] = interleaved[i * 2 + 1];
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            sum += l * l + r * r; sumL += l * l; sumR += r * r; sumLR += l * r;
            monoPeak = Math.Max(monoPeak, Math.Abs((l + r) * 0.5));
        }
        var weightedL = KWeight(left);
        var weightedR = KWeight(right);
        var weightedPower = weightedL.Zip(weightedR, (l, r) => l * l + r * r).ToArray();
        var loudness = MeasureBs1770(weightedPower);
        var impulse = FindPeak(left, Math.Max(0, ImpulseSample - 4096), Math.Min(left.Length, ImpulseSample + 4096));
        return new(id, Db(peak), Db(TruePeak4x(left, right)), Db(Math.Sqrt(sum / interleaved.Length)),
            loudness.Integrated, loudness.MaxMomentary, loudness.MaxShortTerm, loudness.Lra,
            sumLR / Math.Sqrt(Math.Max(1e-15, sumL * sumR)), Db(monoPeak), impulse - ImpulseSample);
    }

    // BS.1770-style measurement: K-weighted channel power, 400 ms blocks at
    // 100 ms steps, -70 LKFS absolute gate then a -10 LU relative gate.
    // LRA follows the EBU Tech 3342 percentile method on 3 s/1 s blocks. This
    // implementation is standards-aligned but not a certified meter.
    private static Loudness MeasureBs1770(double[] power)
    {
        var momentaryEnergy = Blocks(power, (int)(SampleRate * 0.4), (int)(SampleRate * 0.1));
        var momentary = momentaryEnergy.Select(LoudnessOf).ToArray();
        var absolute = momentaryEnergy.Where((energy, i) => momentary[i] >= -70).ToArray();
        var preliminary = LoudnessOf(absolute.Length == 0 ? 1e-15 : absolute.Average());
        var relativeGate = preliminary - 10;
        var gated = momentaryEnergy.Where((energy, i) => momentary[i] >= -70 && momentary[i] >= relativeGate).ToArray();
        var integrated = LoudnessOf(gated.Length == 0 ? 1e-15 : gated.Average());

        var shortEnergy = Blocks(power, SampleRate * 3, SampleRate);
        var shortTerm = shortEnergy.Select(LoudnessOf).ToArray();
        var lraValues = shortTerm.Where(value => value >= -70 && value >= integrated - 20).Order().ToArray();
        var lra = lraValues.Length < 2 ? 0 : Percentile(lraValues, 0.95) - Percentile(lraValues, 0.10);
        return new(integrated, momentary.Length == 0 ? -150 : momentary.Max(),
            shortTerm.Length == 0 ? -150 : shortTerm.Max(), lra);
    }

    private static double[] Blocks(double[] values, int length, int step)
    {
        if (values.Length < length) return values.Length == 0 ? [] : [values.Average()];
        var prefix = new double[values.Length + 1];
        for (var i = 0; i < values.Length; i++) prefix[i + 1] = prefix[i] + values[i];
        var result = new List<double>();
        for (var start = 0; start + length <= values.Length; start += step)
            result.Add((prefix[start + length] - prefix[start]) / length);
        return result.ToArray();
    }

    private static double LoudnessOf(double energy) => -0.691 + 10 * Math.Log10(Math.Max(1e-15, energy));
    private static double Percentile(double[] sorted, double fraction)
    {
        var index = fraction * (sorted.Length - 1);
        var low = (int)Math.Floor(index); var high = (int)Math.Ceiling(index);
        return sorted[low] + (sorted[high] - sorted[low]) * (index - low);
    }

    // Four-times oversampled, 12-tap Blackman-windowed sinc estimate. It is
    // materially safer than sample peak but is labelled an estimate because
    // the coefficients are not from a certified IEC meter implementation.
    private static double TruePeak4x(double[] left, double[] right)
    {
        var peak = 0.0;
        foreach (var channel in new[] { left, right })
            for (var i = 6; i < channel.Length - 6; i++)
                for (var phase = 0; phase < 4; phase++)
                {
                    var fraction = phase / 4.0; var sample = 0.0; var norm = 0.0;
                    for (var tap = -5; tap <= 6; tap++)
                    {
                        var x = tap - fraction;
                        var sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                        var n = tap + 5;
                        var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / 11.0) + 0.08 * Math.Cos(4 * Math.PI * n / 11.0);
                        var coefficient = sinc * window; sample += channel[i + tap] * coefficient; norm += coefficient;
                    }
                    peak = Math.Max(peak, Math.Abs(sample / Math.Max(1e-12, norm)));
                }
        return peak;
    }

    private static double[] KWeight(double[] input)
    {
        var shelf = Biquad(input, 1.53512485958697, -2.69169618940638, 1.19839281085285,
            -1.69065929318241, 0.73248077421585);
        return Biquad(shelf, 1, -2, 1, -1.99004745483398, 0.99007225036621);
    }

    private static double[] Biquad(double[] input, double b0, double b1, double b2, double a1, double a2)
    {
        var output = new double[input.Length]; double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var x = input[i]; var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            output[i] = y; x2 = x1; x1 = x; y2 = y1; y1 = y;
        }
        return output;
    }

    private static int FindPeak(double[] samples, int start, int end)
    {
        var index = start; var peak = 0.0;
        for (var i = start; i < end; i++) if (Math.Abs(samples[i]) > peak) { peak = Math.Abs(samples[i]); index = i; }
        return index;
    }

    private static double Db(double linear) => 20 * Math.Log10(Math.Max(1e-15, linear));

    private static void WriteWave(string path, float[] samples, int channels = 2)
    {
        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = samples.Length * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)channels); writer.Write(SampleRate); writer.Write(SampleRate * channels * 2);
        writer.Write((short)(channels * 2)); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes);
        foreach (var sample in samples) writer.Write((short)Math.Round(Math.Clamp(sample, -1, 1) * 32767));
    }

    private static float[] ReadWave(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("WAV RIFF attendu");
        reader.ReadInt32(); reader.ReadBytes(4);
        short channels = 0, bits = 0; byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4)); var size = reader.ReadInt32();
            if (id == "fmt ") { reader.ReadInt16(); channels = reader.ReadInt16(); reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt16(); bits = reader.ReadInt16(); if (size > 16) reader.ReadBytes(size - 16); }
            else if (id == "data") { data = reader.ReadBytes(size); break; }
            else reader.ReadBytes(size);
        }
        if (channels != 2 || bits != 16 || data == null) throw new InvalidDataException($"WAV PCM stéréo 16-bit attendu ({channels}ch/{bits}bit)");
        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        return samples;
    }

    private readonly record struct RenderResult(bool Success, string Message);
    private readonly record struct Loudness(double Integrated, double MaxMomentary, double MaxShortTerm, double Lra);
    private readonly record struct Measurement(string Id, double Peak, double TruePeak, double Rms, double Integrated,
        double MaxMomentary, double MaxShortTerm, double Lra, double Correlation, double MonoPeak, int Latency)
    {
        public string ToTsv() => FormattableString.Invariant($"{Id}\t{Peak:0.00}\t{TruePeak:0.00}\t{Rms:0.00}\t{Integrated:0.00}\t{MaxMomentary:0.00}\t{MaxShortTerm:0.00}\t{Lra:0.00}\t{Correlation:0.000}\t{MonoPeak:0.00}\t{Latency}");
        public override string ToString() => FormattableString.Invariant($"MEASURE {Id,-7} peak={Peak,6:0.00} dBFS truePeak~={TruePeak,6:0.00} dBTP rms={Rms,6:0.00} dBFS I={Integrated,6:0.00} LUFS Mmax={MaxMomentary,6:0.00} Smax={MaxShortTerm,6:0.00} LRA={Lra,5:0.00} LU corr={Correlation,6:0.000}");
    }

    internal sealed class LibMpv : IDisposable
    {
        private IntPtr _handle;
        public LibMpv(string directory) { SetDllDirectory(directory); _handle = mpv_create(); }
        public bool SetOption(string name, string value) => mpv_set_option_string(_handle, name, value) >= 0;
        public bool SetProperty(string name, string value) => mpv_set_property_string(_handle, name, value) >= 0;
        public bool Initialize() => mpv_initialize(_handle) >= 0;
        public string Get(string name) { var pointer = mpv_get_property_string(_handle, name); if (pointer == IntPtr.Zero) return ""; try { return Marshal.PtrToStringUTF8(pointer) ?? ""; } finally { mpv_free(pointer); } }
        public bool Command(params string[] args)
            => CommandCode(args) >= 0;
        public int CommandCode(params string[] args)
        {
            var pointers = new IntPtr[args.Length + 1];
            try
            {
                for (var i = 0; i < args.Length; i++) { var bytes = Encoding.UTF8.GetBytes(args[i] + '\0'); pointers[i] = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, pointers[i], bytes.Length); }
                var pinned = GCHandle.Alloc(pointers, GCHandleType.Pinned);
                try { return mpv_command(_handle, pinned.AddrOfPinnedObject()); } finally { pinned.Free(); }
            }
            finally { for (var i = 0; i < args.Length; i++) if (pointers[i] != IntPtr.Zero) Marshal.FreeHGlobal(pointers[i]); }
        }
        public int DrainEventCount(int eventId)
        {
            var count = 0;
            while (true)
            {
                var pointer = mpv_wait_event(_handle, 0);
                if (pointer == IntPtr.Zero) break;
                var evt = Marshal.PtrToStructure<MpvEvent>(pointer);
                if (evt.EventId == 0) break;
                if (evt.EventId == eventId) count++;
            }
            return count;
        }
        public bool RequestLogMessages(string level) => mpv_request_log_messages(_handle, level) >= 0;
        public IReadOnlyList<string> DrainLogMessages()
        {
            var result = new List<string>();
            while (true)
            {
                var pointer = mpv_wait_event(_handle, 0);
                if (pointer == IntPtr.Zero) break;
                var evt = Marshal.PtrToStructure<MpvEvent>(pointer);
                if (evt.EventId == 0) break;
                if (evt.EventId == 2 && evt.Data != IntPtr.Zero)
                {
                    var message = Marshal.PtrToStructure<MpvLogMessage>(evt.Data);
                    result.Add((Marshal.PtrToStringUTF8(message.Prefix) ?? "") + ": " + (Marshal.PtrToStringUTF8(message.Text) ?? "").TrimEnd());
                }
            }
            return result;
        }
        public void Dispose() { if (_handle != IntPtr.Zero) { mpv_terminate_destroy(_handle); _handle = IntPtr.Zero; } SetDllDirectory(null); }
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string? path);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, string name, string value);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, string name, string value);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_free(IntPtr data);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr args);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_request_log_messages(IntPtr ctx, string minLevel);
        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEvent
        {
            public int EventId;
            public int Error;
            public ulong ReplyUserdata;
            public IntPtr Data;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MpvLogMessage
        {
            public IntPtr Prefix;
            public IntPtr Level;
            public IntPtr Text;
            public int LogLevel;
        }
    }
}
