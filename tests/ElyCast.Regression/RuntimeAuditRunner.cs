using System.Diagnostics;
using System.Globalization;
using Elysium_Cast_IPTV.Services.Audio;

internal static class RuntimeAuditRunner
{
    private const int AudioReconfigEvent = 18;

    public static int ProbeContinuity(string libMpvDirectory, string outputDirectory, string? inputOverride = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var input = inputOverride ?? Path.Combine(outputDirectory, "elysound-input.wav");
        using var mpv = CreatePlayer(libMpvDirectory, input, inputOverride != null);
        var commands = new List<string[]>(); var rejectedAttempts = 0;
        bool Command(string[] args)
        {
            commands.Add(args.ToArray());
            var ok = mpv.Command(args);
            if (!ok) rejectedAttempts++;
            return ok;
        }
        var controller = new ElySoundController(mpv.Get, Command);
        if (!controller.Apply(ElySoundCatalog.BuiltIn[0], true, false).Applied) return 1;
        mpv.SetProperty("pause", "no");
        Thread.Sleep(300);
        mpv.DrainEventCount(AudioReconfigEvent);
        commands.Clear(); rejectedAttempts = 0;
        var afBaseline = mpv.Get("af");
        var rows = new List<string> { "update\ttype\told\tnew\ttime_before\ttime_after\tdelta_ms\twall_ms\tavsync_before\tavsync_after\taudio_buffer_before\taudio_buffer_after\taudio_reconfig\taf_changed\tcommand_count\trejected_attempts" };
        var update = 0; var totalAo = 0; var terminalFailures = 0;

        void Apply(string type, string oldValue, string newValue, Elysium_Cast_IPTV.Models.ElySoundProfile profile, bool enabled, bool surround)
        {
            commands.Clear(); rejectedAttempts = 0; mpv.DrainEventCount(AudioReconfigEvent);
            var positionBefore = D(mpv.Get("time-pos")); var avBefore = mpv.Get("avsync"); var bufferBefore = mpv.Get("audio-buffer"); var afBefore = mpv.Get("af");
            var watch = Stopwatch.StartNew(); var result = controller.Apply(profile, enabled, surround); watch.Stop();
            var positionAfter = D(mpv.Get("time-pos")); var avAfter = mpv.Get("avsync"); var bufferAfter = mpv.Get("audio-buffer"); var afAfter = mpv.Get("af");
            var ao = mpv.DrainEventCount(AudioReconfigEvent); totalAo += ao; if (!result.Applied && enabled) terminalFailures++;
            rows.Add(string.Join('\t', ++update, type, oldValue, newValue, F(positionBefore), F(positionAfter), F((positionAfter-positionBefore)*1000),
                F(watch.Elapsed.TotalMilliseconds), avBefore, avAfter, bufferBefore, bufferAfter, ao,
                !string.Equals(afBefore, afAfter, StringComparison.Ordinal), commands.Count, rejectedAttempts));
        }

        var prior = "cinema";
        for (var i = 0; i < 100; i++)
        {
            var profile = ElySoundCatalog.BuiltIn[i % ElySoundCatalog.BuiltIn.Count];
            Apply("preset", prior, profile.Id, profile, true, (i & 1) == 0); prior = profile.Id;
            Thread.Sleep(10);
        }
        var source = ElySoundCatalog.BuiltIn[1];
        var custom = new Elysium_Cast_IPTV.Models.ElySoundProfile { Version=2, Id="continuity", Name="Continuity", Preamp=source.Preamp, Bass=source.Bass, LowMid=source.LowMid, Mid=source.Mid, Presence=source.Presence, Treble=source.Treble, Clarity=source.Clarity, Width=source.Width, Compressor=source.Compressor, LimiterCeilingDb=source.LimiterCeilingDb };
        foreach (var field in new[] { "Preamp", "Bass", "LowMid", "Mid", "Presence", "Treble", "Clarity", "Width", "Compressor", "Limiter" })
            for (var step = 0; step <= 20; step++) { ApplySlider(custom, field, step); Apply("slider-" + field, (step-1).ToString(), step.ToString(), custom, true, false); }
        for (var i=0; i<10; i++) { Apply("toggle", "on", "off", custom, false, false); Apply("toggle", "off", "on", custom, true, false); }
        mpv.SetProperty("pause", "yes"); Apply("paused-preset", "custom", "music", ElySoundCatalog.BuiltIn[1], true, false); mpv.SetProperty("pause", "no");
        mpv.Command("seek", "1", "relative"); Thread.Sleep(100); Apply("after-seek", "music", "cinema", ElySoundCatalog.BuiltIn[0], true, false);

        var suffix = inputOverride == null ? "audio" : "video";
        var path = Path.Combine(outputDirectory, $"elysound-continuity-{suffix}.tsv"); File.WriteAllLines(path, rows);
        var runtimeAfChanges = rows.Skip(1).Count(row => row.Split('\t')[13] == "True");
        var finalAf = mpv.Get("af"); var finalEntries = Count(finalAf, "@elysound");
        var summary = Path.Combine(outputDirectory, $"elysound-continuity-{suffix}-summary.txt");
        File.WriteAllLines(summary, [ $"updates={update}", $"terminal_failures={terminalFailures}", $"audio_reconfigs={totalAo}", $"runtime_af_changes={runtimeAfChanges}", $"final_elysound_entries={finalEntries}", $"af_unchanged_from_baseline={string.Equals(afBaseline, finalAf, StringComparison.Ordinal)}", $"report={path}" ]);
        Console.WriteLine(File.ReadAllText(summary));
        return terminalFailures == 0 && totalAo == 0 && runtimeAfChanges == 0 && finalEntries == 1 ? 0 : 1;
    }

    public static int RunAudit(string libMpvDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var input = Path.Combine(outputDirectory, "elysound-input.wav");
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("FAIL  input absent; run --measure-audio first");
            return 1;
        }

        var logPath = Path.Combine(outputDirectory, "elysound-runtime-audit.tsv");
        var rows = new List<string>
        {
            "sequence\tpreset\tparameter\told\tnew\tcommand\tresult\ttime_before\ttime_after\tposition_delta_ms\taudio_pts_before\taudio_pts_after\tavsync_before\tavsync_after\tao_reconfig\taf_changed\texecution_ms"
        };
        using var mpv = CreatePlayer(libMpvDirectory, input);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var forbidden = 0;
        var afAdds = 0;
        var afRemoves = 0;
        var sequence = 0;
        var currentPreset = "install";

        bool Command(string[] command)
        {
            if (command.Any(value => value is "loadfile" or "seek" or "stop")) forbidden++;
            if (command is ["af", "add", ..]) afAdds++;
            if (command is ["af", "remove", ..]) afRemoves++;
            var runtime = command.Length >= 5 && command[0] == "af-command";
            var parameter = runtime ? command[4] + "." + command[2] : string.Join(' ', command.Take(2));
            values.TryGetValue(parameter, out var oldValue);
            var newValue = runtime ? command[3] : command.LastOrDefault() ?? "";
            mpv.DrainEventCount(AudioReconfigEvent);
            var before = D(mpv.Get("time-pos"));
            var audioPtsBefore = mpv.Get("audio-pts");
            var avsyncBefore = mpv.Get("avsync");
            var afBefore = mpv.Get("af");
            var watch = Stopwatch.StartNew();
            var commandCode = mpv.CommandCode(command);
            var accepted = commandCode >= 0;
            watch.Stop();
            Thread.Sleep(2);
            var after = D(mpv.Get("time-pos"));
            var audioPtsAfter = mpv.Get("audio-pts");
            var avsyncAfter = mpv.Get("avsync");
            var afAfter = mpv.Get("af");
            var aoReconfig = mpv.DrainEventCount(AudioReconfigEvent);
            if (accepted && runtime) values[parameter] = newValue;
            rows.Add(string.Join('\t', ++sequence, currentPreset, parameter, oldValue ?? "<unknown>", newValue,
                string.Join(" | ", command), accepted + " (code " + commandCode + ")", F(before), F(after), F((after - before) * 1000),
                audioPtsBefore, audioPtsAfter, avsyncBefore, avsyncAfter, aoReconfig,
                !string.Equals(afBefore, afAfter, StringComparison.Ordinal), F(watch.Elapsed.TotalMilliseconds)));
            return accepted;
        }

        var controller = new ElySoundController(mpv.Get, Command);
        currentPreset = "cinema";
        var initial = controller.Apply(ElySoundCatalog.BuiltIn[0], true, false);
        if (!initial.Applied) return Fail(initial.Message);
        mpv.SetProperty("pause", "no");
        Thread.Sleep(200);
        mpv.DrainEventCount(AudioReconfigEvent);

        var startPosition = D(mpv.Get("time-pos"));
        var startWall = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            var profile = ElySoundCatalog.BuiltIn[i % ElySoundCatalog.BuiltIn.Count];
            currentPreset = profile.Id;
            var result = controller.Apply(profile, true, (i & 1) == 0);
            if (!result.Applied) return Fail(result.Message);
            Thread.Sleep(8);
        }

        // Continuous motion over the complete domain of every UI control.
        var source = ElySoundCatalog.BuiltIn[1];
        var custom = new Elysium_Cast_IPTV.Models.ElySoundProfile
        {
            Version = source.Version, Id = "audit-custom", Name = "Audit custom", Preamp = source.Preamp,
            Bass = source.Bass, LowMid = source.LowMid, Mid = source.Mid, Presence = source.Presence,
            Treble = source.Treble, Clarity = source.Clarity, Width = source.Width,
            Compressor = source.Compressor, LimiterCeilingDb = source.LimiterCeilingDb
        };
        foreach (var field in new[] { "Preamp", "Bass", "LowMid", "Mid", "Presence", "Treble", "Clarity", "Width", "Compressor", "Limiter" })
        {
            currentPreset = "slider-" + field.ToLowerInvariant();
            for (var step = 0; step <= 20; step++)
            {
                ApplySlider(custom, field, step);
                var result = controller.Apply(custom, true, false);
                if (!result.Applied) return Fail(result.Message);
                Thread.Sleep(3);
            }
        }

        currentPreset = "toggle";
        for (var i = 0; i < 20; i++)
        {
            controller.Apply(custom, false, false);
            controller.Apply(custom, true, false);
        }
        mpv.SetProperty("pause", "yes");
        currentPreset = "paused";
        controller.Apply(ElySoundCatalog.BuiltIn[2], true, false);
        mpv.SetProperty("pause", "no");

        startWall.Stop();
        var endPosition = D(mpv.Get("time-pos"));
        File.WriteAllLines(logPath, rows);
        var runtimeRows = rows.Skip(1).Select(line => line.Split('\t')).Where(parts => parts[2].Contains('@')).ToArray();
        var rebuilt = runtimeRows.Count(parts => bool.Parse(parts[15]));
        var ao = runtimeRows.Sum(parts => int.Parse(parts[14], CultureInfo.InvariantCulture));
        var rejected = runtimeRows.Count(parts => !parts[6].StartsWith("True", StringComparison.Ordinal));
        var mediaElapsed = endPosition - startPosition;
        var driftMs = (mediaElapsed - startWall.Elapsed.TotalSeconds) * 1000;
        var summary = Path.Combine(outputDirectory, "elysound-runtime-summary.txt");
        File.WriteAllLines(summary,
        [
            $"commands={runtimeRows.Length}", $"rejected={rejected}", $"af_adds={afAdds}", $"af_removes={afRemoves}",
            $"transport_commands_from_controller={forbidden}", $"audio_reconfig_events={ao}", $"runtime_af_changes={rebuilt}",
            $"wall_seconds={startWall.Elapsed.TotalSeconds:0.000}", $"media_seconds={mediaElapsed:0.000}", $"drift_ms={driftMs:0.000}",
            $"final_af={mpv.Get("af")}", $"log={logPath}"
        ]);
        Console.WriteLine(File.ReadAllText(summary));
        return rejected == 0 && forbidden == 0 && afAdds == 1 && afRemoves == 0 && ao == 0 && rebuilt == 0 ? 0 : 1;

        int Fail(string message) { Console.Error.WriteLine("FAIL  " + message); return 1; }
    }

    public static int RunStress(string libMpvDirectory, string outputDirectory, int seconds)
    {
        Directory.CreateDirectory(outputDirectory);
        var input = Path.Combine(outputDirectory, "elysound-input.wav");
        if (!File.Exists(input) || seconds < 1) return 2;
        var path = Path.Combine(outputDirectory, "elysound-stress.tsv");
        var rows = new List<string> { "elapsed_s\tposition_s\tworking_set\tprivate_bytes\thandles\tthreads\taf_entries\taudio_reconfigs\tcommands\terrors" };
        using var mpv = CreatePlayer(libMpvDirectory, input);
        var commands = 0; var errors = 0; var terminalErrors = 0; var audioReconfigs = 0;
        bool Command(string[] args) { commands++; var ok = mpv.Command(args); if (!ok) errors++; return ok; }
        var controller = new ElySoundController(mpv.Get, Command);
        controller.Apply(ElySoundCatalog.BuiltIn[0], true, false);
        mpv.DrainEventCount(AudioReconfigEvent); // initial graph installation is the baseline
        mpv.SetProperty("pause", "no");
        var watch = Stopwatch.StartNew();
        var nextSample = TimeSpan.Zero;
        var iteration = 0;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            var profile = ElySoundCatalog.BuiltIn[iteration % ElySoundCatalog.BuiltIn.Count];
            if (!controller.Apply(profile, true, (iteration & 3) == 0).Applied) terminalErrors++;
            if (iteration % 17 == 0) { controller.Apply(profile, false, false); controller.Apply(profile, true, false); }
            if (iteration % 53 == 0) { mpv.SetProperty("pause", "yes"); Thread.Sleep(20); controller.Apply(profile, true, false); mpv.SetProperty("pause", "no"); }
            if (iteration % 97 == 0) mpv.Command("seek", "1", "relative");
            if (iteration == seconds * 2)
            {
                mpv.DrainEventCount(AudioReconfigEvent);
                mpv.Command("loadfile", input, "replace");
                controller.MediaChanged();
                Thread.Sleep(100);
                controller.TryApplyWhenAudioReady();
                mpv.DrainEventCount(AudioReconfigEvent); // expected decoder/AO transition for a new media
            }
            audioReconfigs += mpv.DrainEventCount(AudioReconfigEvent);
            if (watch.Elapsed >= nextSample)
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var af = mpv.Get("af");
                var entries = Count(af, "@elysound");
                rows.Add(string.Join('\t', F(watch.Elapsed.TotalSeconds), mpv.Get("time-pos"), process.WorkingSet64,
                    process.PrivateMemorySize64, process.HandleCount, process.Threads.Count, entries, audioReconfigs, commands, errors));
                nextSample += TimeSpan.FromSeconds(10);
                File.WriteAllLines(path, rows);
            }
            iteration++;
            Thread.Sleep(250);
        }
        var finalEntries = Count(mpv.Get("af"), "@elysound");
        rows.Add($"SUMMARY\tseconds={watch.Elapsed.TotalSeconds:0.0}\titerations={iteration}\tcommands={commands}\trejected_attempts={errors}\tterminal_errors={terminalErrors}\taudio_reconfigs={audioReconfigs}\tfinal_elysound_entries={finalEntries}");
        File.WriteAllLines(path, rows);
        Console.WriteLine(rows[^1]);
        return terminalErrors == 0 && finalEntries == 1 ? 0 : 1;
    }

    private static AudioMeasurementRunner.LibMpv CreatePlayer(string libMpvDirectory, string input, bool video = false)
    {
        var mpv = new AudioMeasurementRunner.LibMpv(libMpvDirectory);
        var options = new List<(string, string)> { ("config", "no"), ("terminal", "no"), ("vo", "null"),
                     ("ao", "null"), ("pause", "yes"), ("idle", "yes"), ("keep-open", "yes"), ("loop-file", "inf") };
        if (!video) options.Add(("vid", "no"));
        foreach (var option in options)
            if (!mpv.SetOption(option.Item1, option.Item2)) throw new InvalidOperationException("option " + option.Item1);
        if (!mpv.Initialize() || !mpv.Command("loadfile", input, "replace")) throw new InvalidOperationException("mpv init/loadfile");
        for (var i = 0; i < 300 && string.IsNullOrEmpty(mpv.Get("audio-params/channel-count")); i++) Thread.Sleep(10);
        mpv.DrainEventCount(AudioReconfigEvent);
        return mpv;
    }

    private static void ApplySlider(Elysium_Cast_IPTV.Models.ElySoundProfile profile, string field, int step)
    {
        var db = -12 + (int)Math.Round(step * 24 / 20.0);
        switch (field)
        {
            case "Preamp": profile.Preamp = Math.Clamp(db, -12, 6); break;
            case "Bass": profile.Bass = db; break;
            case "LowMid": profile.LowMid = db; break;
            case "Mid": profile.Mid = db; break;
            case "Presence": profile.Presence = db; break;
            case "Treble": profile.Treble = db; break;
            case "Clarity": profile.Clarity = db; break;
            case "Width": profile.Width = step * 3; break;
            case "Compressor": profile.Compressor = step * 3; break;
            case "Limiter": profile.LimiterCeilingDb = -3 + step * 2.7 / 20; break;
        }
    }

    private static double D(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static int Count(string value, string needle) => (value.Length - value.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;
    public static int ProbeCommands(string libMpvDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var input = Path.Combine(outputDirectory, "elysound-input.wav");
        var probes = new[]
        {
            ("volume@preamp", "volume", "0.5"),
            ("equalizer@bass", "gain", "6"),
            ("equalizer@lowmid", "gain", "-6"),
            ("equalizer@mid", "gain", "6"),
            ("equalizer@presence", "gain", "-6"),
            ("equalizer@treble", "gain", "6"),
            ("equalizer@clarity", "gain", "-6"),
            ("extrastereo@width", "m", "1.1"),
            ("acompressor@compressor", "threshold", "0.25"),
            ("acompressor@compressor", "ratio", "3"),
            ("acompressor@compressor", "makeup", "1.25"),
            ("alimiter@limiter", "limit", "0.8")
        };
        var rows = new List<string> { "target\tparameter\tvalue\tcode\taf_unchanged\taudio_reconfig\ttime_before\ttime_after" };
        var failures = 0;
        foreach (var probe in probes)
        {
            using var mpv = CreatePlayer(libMpvDirectory, input);
            var add = mpv.CommandCode("af", "add", "@elysound:lavfi=[" + ElySoundGraph.BuildTopology(true) + "]");
            mpv.SetProperty("pause", "no");
            Thread.Sleep(250);
            mpv.DrainEventCount(AudioReconfigEvent);
            var afBefore = mpv.Get("af"); var timeBefore = mpv.Get("time-pos");
            var code = mpv.CommandCode("af-command", "elysound", probe.Item2, probe.Item3, probe.Item1);
            Thread.Sleep(20);
            var afAfter = mpv.Get("af"); var timeAfter = mpv.Get("time-pos");
            var ao = mpv.DrainEventCount(AudioReconfigEvent);
            rows.Add(string.Join('\t', probe.Item1, probe.Item2, probe.Item3, code,
                string.Equals(afBefore, afAfter, StringComparison.Ordinal), ao, timeBefore, timeAfter));
            if (add < 0 || code < 0 || ao != 0 || !string.Equals(afBefore, afAfter, StringComparison.Ordinal)) failures++;
        }
        using (var mpv = CreatePlayer(libMpvDirectory, input))
        {
            mpv.Command("af", "add", "@elysound:lavfi=[" + ElySoundGraph.BuildTopology(true) + "]");
            mpv.SetProperty("pause", "no"); Thread.Sleep(250);
            rows.Add("negative: unsupported command\tbogus\t1\t" + mpv.CommandCode("af-command", "elysound", "bogus", "1", "volume@preamp") + "\tN/A\tN/A\tN/A\tN/A");
            rows.Add("negative: missing instance\tvolume\t1\t" + mpv.CommandCode("af-command", "elysound", "volume", "1", "volume@missing") + "\tN/A\tN/A\tN/A\tN/A");
            rows.Add("negative: missing mpv label\tvolume\t1\t" + mpv.CommandCode("af-command", "missing", "volume", "1", "volume@preamp") + "\tN/A\tN/A\tN/A\tN/A");
        }
        var path = Path.Combine(outputDirectory, "elysound-af-command-compatibility.tsv");
        File.WriteAllLines(path, rows);
        Console.WriteLine(string.Join(Environment.NewLine, rows));
        return failures == 0 ? 0 : 1;
    }
}
