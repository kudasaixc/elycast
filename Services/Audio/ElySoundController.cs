using System.Globalization;
using Elysium_Cast_IPTV.Models;

namespace Elysium_Cast_IPTV.Services.Audio;

/// <summary>
/// Owns ElySound's single labelled mpv/libavfilter graph. The graph topology is
/// installed once per channel layout; preset and slider changes are sent to
/// named FFmpeg filter instances and never seek or reload the media.
/// </summary>
internal sealed class ElySoundController
{
    public const string MpvLabel = "elysound";
    private readonly Func<string, string> _get;
    private readonly Func<string[], bool> _command;
    private ElySoundRequest _requested = ElySoundRequest.Bypass;
    private int _installedChannels;
    private bool _installed;
    private bool _dirty;

    public bool HasPendingApply => _dirty;

    public ElySoundController(Func<string, string> get, Func<string[], bool> command)
    {
        _get = get;
        _command = command;
    }

    public ElySoundApplyResult Apply(ElySoundProfile profile, bool enabled, bool virtualSurround)
    {
        if (!enabled)
        {
            _requested = ElySoundRequest.Bypass;
            _dirty = false;
            if (!_installed)
                return new(false, false, true, "bypass neutre (aucun graphe installé)", "");
            var bypass = ElySoundGraph.ResolveBypass(_installedChannels == 2);
            foreach (var update in bypass.RuntimeUpdates)
                if (!SendRuntime(update))
                    return FailSafeBypass($"commande de bypass {update.Target}.{update.Command} refusée", bypass.Description);
            return new(false, false, true, "bypass neutre par commandes runtime", bypass.Description);
        }

        _requested = new ElySoundRequest(true, profile, virtualSurround);
        _dirty = true;

        return TryApplyWhenAudioReady();
    }

    public ElySoundApplyResult TryApplyWhenAudioReady()
    {
        if (!_requested.Enabled)
            return new(false, false, false, "ELYSOUND+ désactivé", "");

        if (!_dirty && _installed)
            return new(true, false, true, "déjà à jour", "");

        var channels = ReadChannelCount();
        if (channels <= 0)
            return new(false, true, false, "paramètres audio pas encore disponibles", "");

        if (_installed && !_get("af").Contains("@" + MpvLabel, StringComparison.Ordinal))
        {
            _installed = false;
            _installedChannels = 0;
        }

        var stereo = channels == 2;
        var values = ElySoundGraph.Resolve(_requested.Profile!, _requested.VirtualSurround, stereo);
        var justInstalled = false;
        if (!_installed || _installedChannels != channels)
        {
            // Rebuild is allowed only for a real channel-layout transition.
            // mpv's labelled add is transactional: a rejected graph leaves the
            // previous chain intact. We still remove our label on failure so a
            // DSP error can never stop the principal media pipeline.
            var graph = ElySoundGraph.BuildTopology(stereo, values);
            if (!_command(["af", "add", $"@{MpvLabel}:lavfi=[{graph}]"]))
                return FailSafeBypass("graphe libavfilter refusé", graph);

            _installed = true;
            _installedChannels = channels;
            justInstalled = true;
        }

        foreach (var update in values.RuntimeUpdates)
        {
            if (!SendRuntime(update))
            {
                if (justInstalled)
                {
                    _dirty = false;
                    return new(true, false, false,
                        "graphe final actif; instances runtime publiées au prochain bloc audio",
                        values.Description);
                }
                return FailSafeBypass($"commande {update.Target}.{update.Command} refusée", values.Description);
            }
        }

        _dirty = false;
        return new(true, false, true,
            stereo ? "graphe actif, commandes runtime" : "graphe actif, largeur désactivée (source non stéréo)",
            values.Description);
    }

    public void MediaChanged()
    {
        // mpv keeps the requested af list across files, but a new decoder can
        // expose a different layout. Force one capability/layout verification
        // after the new audio-params become authoritative.
        if (!_requested.Enabled && _installed)
        {
            RemoveOnlyElySound("nouveau média en bypass");
            return;
        }
        // Keep the last authoritative layout. The next ready audio parameters
        // trigger a rebuild only when the new track really changes channels.
        _dirty = _requested.Enabled;
    }

    public void Reset()
    {
        _requested = ElySoundRequest.Bypass;
        RemoveOnlyElySound("reset");
    }

    private ElySoundApplyResult FailSafeBypass(string reason, string graph)
    {
        _command(["af", "remove", $"@{MpvLabel}"]);
        _installed = false;
        _installedChannels = 0;
        _dirty = false;
        return new(false, false, false, reason + " — bypass neutre conservé", graph);
    }

    private ElySoundApplyResult RemoveOnlyElySound(string reason)
    {
        if (_installed)
            _command(["af", "remove", $"@{MpvLabel}"]);
        _installed = false;
        _installedChannels = 0;
        _dirty = false;
        return new(false, false, false, reason, "");
    }

    private bool SendRuntime(ElySoundRuntimeUpdate update)
    {
        // libavfilter can briefly report a named instance as unavailable while
        // the audio worker owns the graph between adjacent commands. Retry the
        // same runtime command without replacing `af` or touching transport.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_command(["af-command", MpvLabel, update.Command, update.Value, update.Target]))
                return true;
            if (attempt < 2) Thread.Sleep(2);
        }
        return false;
    }

    private int ReadChannelCount() => int.TryParse(
        _get("audio-params/channel-count"), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : 0;

    private sealed record ElySoundRequest(bool Enabled, ElySoundProfile? Profile, bool VirtualSurround)
    {
        public static ElySoundRequest Bypass { get; } = new(false, null, false);
    }
}

public readonly record struct ElySoundApplyResult(
    bool Applied, bool Pending, bool RuntimeOnly, string Message, string Graph);

public static class ElySoundCatalog
{
    public static IReadOnlyList<ElySoundProfile> BuiltIn { get; } =
    [
        new() { Id = "cinema", Name = "ELYSOUND+ Cinema", Preamp = 0, Bass = 3, LowMid = -1, Mid = 1, Presence = 2, Treble = 1, Clarity = 1, Width = 24, Compressor = 20, LimiterCeilingDb = -2.3 },
        new() { Id = "music", Name = "ELYSOUND+ Music", Preamp = -1, Bass = 2, LowMid = 1, Mid = 0, Presence = 1, Treble = 1, Clarity = 1, Width = 12, Compressor = 6, LimiterCeilingDb = -2.3 },
        new() { Id = "anime", Name = "ELYSOUND+ Anime", Preamp = -1, Bass = 3, LowMid = -1, Mid = 2, Presence = 3, Treble = 2, Clarity = 2, Width = 16, Compressor = 12, LimiterCeilingDb = -2.3 },
        new() { Id = "voice", Name = "ELYSOUND+ Voix", Preamp = -1, Bass = -4, LowMid = -2, Mid = 5, Presence = 5, Treble = 0, Clarity = 2, Width = 0, Compressor = 18, LimiterCeilingDb = -2.3 },
        new() { Id = "night", Name = "ELYSOUND+ Nuit", Preamp = 0, Bass = -6, LowMid = -2, Mid = 4, Presence = 4, Treble = -2, Clarity = -1, Width = 0, Compressor = 45, LimiterCeilingDb = -2.3 },
        new() { Id = "bass", Name = "ELYSOUND+ Bass Boost", Preamp = 0, Bass = 7, LowMid = -1, Mid = 0, Presence = 1, Treble = 1, Clarity = 1, Width = 14, Compressor = 14, LimiterCeilingDb = -2.3 },
        new() { Id = "horror", Name = "ELYSOUND+ Horror", Preamp = -1, Bass = 4, LowMid = 1, Mid = -1, Presence = 2, Treble = 2, Clarity = 2, Width = 28, Compressor = 8, LimiterCeilingDb = -2.3 },
        new() { Id = "sport", Name = "ELYSOUND+ Sport", Preamp = -1, Bass = 1, LowMid = -2, Mid = 4, Presence = 3, Treble = 1, Clarity = 1, Width = 14, Compressor = 20, LimiterCeilingDb = -2.3 }
    ];
}

internal static class ElySoundGraph
{
    public static readonly ElySoundBand[] Bands =
    [
        new("bass", 64, 1.05, p => p.Bass),
        new("lowmid", 320, 1.10, p => p.LowMid),
        new("mid", 950, 1.05, p => p.Mid),
        new("presence", 2600, 1.00, p => p.Presence),
        new("treble", 5200, 1.00, p => p.Treble),
        new("clarity", 9500, 1.15, p => p.Clarity)
    ];

    public static string BuildTopology(bool stereo)
    {
        var filters = new List<string> { "volume@preamp=volume=0dB" };
        filters.AddRange(Bands.Select(b => string.Create(CultureInfo.InvariantCulture,
            $"equalizer@{b.Target}=f={b.Frequency}:t=q:w={b.Q:0.###}:g=0")));
        if (stereo)
            filters.Add("extrastereo@width=m=1:c=0");

        // RMS detection, soft knee and slow release avoid transient pumping.
        // The limiter is a ceiling only: level_in and level_out stay exactly 1.
        filters.Add("acompressor@compressor=threshold=1:ratio=1:attack=25:release=260:makeup=1:knee=4:link=average:detection=rms");
        filters.Add("alimiter@limiter=level_in=1:level_out=1:level=0:limit=0.891:attack=5:release=80:latency=1");
        return string.Join(',', filters);
    }

    public static string BuildTopology(bool stereo, ElySoundResolvedValues values)
    {
        string Value(string target, string command) => values.RuntimeUpdates
            .First(update => update.Target == target && update.Command == command).Value;

        var filters = new List<string>
        {
            "volume@preamp=volume=" + Value("volume@preamp", "volume")
        };
        filters.AddRange(Bands.Select(b => string.Create(CultureInfo.InvariantCulture,
            $"equalizer@{b.Target}=f={b.Frequency}:t=q:w={b.Q:0.###}:g={Value("equalizer@" + b.Target, "gain")}")));
        if (stereo)
            filters.Add("extrastereo@width=m=" + Value("extrastereo@width", "m") + ":c=0");
        filters.Add("acompressor@compressor=threshold=" + Value("acompressor@compressor", "threshold") +
                    ":ratio=" + Value("acompressor@compressor", "ratio") +
                    ":attack=25:release=260:makeup=" + Value("acompressor@compressor", "makeup") +
                    ":knee=4:link=average:detection=rms");
        filters.Add("alimiter@limiter=level_in=1:level_out=1:level=0:limit=" + Value("alimiter@limiter", "limit") +
                    ":attack=5:release=80:latency=1");
        return string.Join(',', filters);
    }

    public static ElySoundResolvedValues Resolve(ElySoundProfile profile, bool virtualSurround, bool stereo)
    {
        var updates = new List<ElySoundRuntimeUpdate>
        {
            new("volume@preamp", "volume", F(DbToLinear(Math.Clamp(profile.Preamp, -12, 6))))
        };
        updates.AddRange(Bands.Select(b => new ElySoundRuntimeUpdate(
            "equalizer@" + b.Target, "gain", F(Math.Clamp(b.Value(profile), -12, 12)))));

        if (stereo)
        {
            var effectiveWidth = Math.Clamp(profile.Width + (virtualSurround ? 10 : 0), 0, 60);
            // extrastereo m=1 is bit-identical bypass. The deliberately narrow
            // 1.00..1.12 range preserves centre energy and mono compatibility.
            updates.Add(new("extrastereo@width", "m", F(1.0 + effectiveWidth / 500.0)));
        }

        var intensity = Math.Clamp(profile.Compressor, 0, 60);
        var thresholdDb = -10.0 - intensity * 0.18;
        var ratio = 1.0 + intensity * 0.035;
        var makeupDb = intensity * 0.045;
        updates.Add(new("acompressor@compressor", "threshold", F(DbToLinear(thresholdDb))));
        updates.Add(new("acompressor@compressor", "ratio", F(ratio)));
        updates.Add(new("acompressor@compressor", "makeup", F(DbToLinear(makeupDb))));

        var ceilingDb = Math.Clamp(profile.LimiterCeilingDb, -3.0, -0.3);
        updates.Add(new("alimiter@limiter", "limit", F(DbToLinear(ceilingDb))));

        var eqDescription = string.Join(", ", Bands.Select(b =>
            FormattableString.Invariant($"{b.Frequency}Hz {b.Value(profile):+0;-0;0}dB")));
        var widthDescription = stereo
            ? FormattableString.Invariant($", width m={1.0 + Math.Clamp(profile.Width + (virtualSurround ? 10 : 0), 0, 60) / 500.0:0.000}")
            : ", width=bypass";
        var description = string.Format(CultureInfo.InvariantCulture,
            "pre={0:+0;-0;0} dB, EQ=[{1}], comp={2}% (thr {3:0.0}dB, {4:0.00}:1, makeup {5:0.0}dB), ceiling={6:0.0}dBFS{7}",
            profile.Preamp, eqDescription, intensity, thresholdDb, ratio, makeupDb, ceilingDb, widthDescription);
        return new(updates, description);
    }

    public static ElySoundResolvedValues ResolveBypass(bool stereo)
    {
        var updates = new List<ElySoundRuntimeUpdate>
        {
            new("volume@preamp", "volume", "1")
        };
        updates.AddRange(Bands.Select(b => new ElySoundRuntimeUpdate("equalizer@" + b.Target, "gain", "0")));
        if (stereo) updates.Add(new("extrastereo@width", "m", "1"));
        updates.Add(new("acompressor@compressor", "threshold", "1"));
        updates.Add(new("acompressor@compressor", "ratio", "1"));
        updates.Add(new("acompressor@compressor", "makeup", "1"));
        updates.Add(new("alimiter@limiter", "limit", "1"));
        return new(updates, "pre=0 dB, EQ=0 dB, width=1, comp=1:1, limiter=1.0");
    }

    private static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);
    private static string Db(double value) => F(value) + "dB";
    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

internal sealed record ElySoundBand(string Target, int Frequency, double Q, Func<ElySoundProfile, int> Value);
internal sealed record ElySoundRuntimeUpdate(string Target, string Command, string Value);
internal sealed record ElySoundResolvedValues(IReadOnlyList<ElySoundRuntimeUpdate> RuntimeUpdates, string Description);
