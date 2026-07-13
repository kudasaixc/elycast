using Elysium_Cast_IPTV.Services.Video;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services;

if (args is ["--measure-audio", var libMpvDirectory, var outputDirectory])
    return AudioMeasurementRunner.Run(libMpvDirectory, outputDirectory);
if (args.Length >= 4 && args[0] == "--measure-references")
    return AudioMeasurementRunner.RunReferences(args[1], args[2], args.Skip(3).ToArray());
if (args is ["--measure-candidates", var candidateLibMpvDirectory, var candidateOutputDirectory])
    return AudioMeasurementRunner.RunCandidates(candidateLibMpvDirectory, candidateOutputDirectory);
if (args.Length >= 4 && args[0] == "--measure-ebur128")
    return AudioMeasurementRunner.RunEbur128(args[1], args[2], args.Skip(3).ToArray());
if (args is ["--audit-runtime", var auditLibMpvDirectory, var auditOutputDirectory])
    return RuntimeAuditRunner.RunAudit(auditLibMpvDirectory, auditOutputDirectory);
if (args is ["--probe-af-command", var probeLibMpvDirectory, var probeOutputDirectory])
    return RuntimeAuditRunner.ProbeCommands(probeLibMpvDirectory, probeOutputDirectory);
if (args is ["--probe-continuity", var continuityLibMpvDirectory, var continuityOutputDirectory])
    return RuntimeAuditRunner.ProbeContinuity(continuityLibMpvDirectory, continuityOutputDirectory);
if (args is ["--probe-continuity-file", var continuityFileLibMpvDirectory, var continuityFileOutputDirectory, var continuityFile])
    return RuntimeAuditRunner.ProbeContinuity(continuityFileLibMpvDirectory, continuityFileOutputDirectory, continuityFile);
if (args is ["--stress-runtime", var stressLibMpvDirectory, var stressOutputDirectory, var secondsText] &&
    int.TryParse(secondsText, out var stressSeconds))
    return RuntimeAuditRunner.RunStress(stressLibMpvDirectory, stressOutputDirectory, stressSeconds);

var cases = new (string Name, PlaybackTerminationAction Actual, PlaybackTerminationAction Expected)[]
{
    ("natural local audio end", PlaybackTerminationPolicy.ForEnd(PlaybackEndReason.NaturalEnd, isLive: false), PlaybackTerminationAction.CleanFiniteEnd),
    ("natural local video end", PlaybackTerminationPolicy.ForEnd(PlaybackEndReason.NaturalEnd, isLive: false), PlaybackTerminationAction.CleanFiniteEnd),
    ("manual stop", PlaybackTerminationPolicy.ForEnd(PlaybackEndReason.UserStop, isLive: false), PlaybackTerminationAction.ManualStop),
    ("live network error", PlaybackTerminationPolicy.ForFailure(isLive: true, autoReconnect: true), PlaybackTerminationAction.ReconnectLive),
    ("disabled IPTV reconnection", PlaybackTerminationPolicy.ForFailure(isLive: true, autoReconnect: false), PlaybackTerminationAction.ShowError),
    ("live stream end", PlaybackTerminationPolicy.ForEnd(PlaybackEndReason.NaturalEnd, isLive: true), PlaybackTerminationAction.ReconnectLive),
    ("next playback", PlaybackTerminationPolicy.ForEnd(PlaybackEndReason.Replaced, isLive: false), PlaybackTerminationAction.Ignore),
    ("local file error", PlaybackTerminationPolicy.ForFailure(isLive: false, autoReconnect: true), PlaybackTerminationAction.ShowError)
};

var failures = cases.Where(test => test.Actual != test.Expected).ToArray();
foreach (var test in cases)
    Console.WriteLine($"{(test.Actual == test.Expected ? "PASS" : "FAIL")}  {test.Name}: {test.Actual}");

var graphFailures = new List<string>();
var stereoTopology = ElySoundGraph.BuildTopology(stereo: true);
var monoTopology = ElySoundGraph.BuildTopology(stereo: false);
foreach (var forbidden in new[] { "dynaudnorm", "crossfeed", "stereowiden" })
    if (stereoTopology.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
        graphFailures.Add("forbidden filter present: " + forbidden);
if (!stereoTopology.Contains("extrastereo@width=m=1:c=0", StringComparison.Ordinal))
    graphFailures.Add("permanent stereo width is missing");
if (monoTopology.Contains("extrastereo", StringComparison.OrdinalIgnoreCase))
    graphFailures.Add("stereo widening applied to mono");
if (!stereoTopology.Contains("alimiter@limiter=level_in=1:level_out=1:level=0", StringComparison.Ordinal))
    graphFailures.Add("limiter used as an amplifier");

var expectedIds = new[] { "cinema", "music", "anime", "voice", "night", "bass", "horror", "sport" };
if (!ElySoundCatalog.BuiltIn.Select(profile => profile.Id).SequenceEqual(expectedIds))
    graphFailures.Add("preset catalog is incomplete");
foreach (var profile in ElySoundCatalog.BuiltIn)
{
    var resolved = ElySoundGraph.Resolve(profile, virtualSurround: true, stereo: true);
    if (resolved.RuntimeUpdates.Any(update => !update.Target.Contains('@')))
        graphFailures.Add(profile.Id + ": unqualified FFmpeg target");
}
var issuedCommands = new List<string[]>();
var channelCount = "2";
var controller = new ElySoundController(
    property => property == "audio-params/channel-count" ? channelCount : property == "af" ? "@elysound:lavfi=[]" : "",
    command => { issuedCommands.Add(command); return true; });
var firstApply = controller.Apply(ElySoundCatalog.BuiltIn[0], enabled: true, virtualSurround: true);
var secondApply = controller.Apply(ElySoundCatalog.BuiltIn[1], enabled: true, virtualSurround: false);
var graphAddsBeforeBypass = issuedCommands.Count(command => command.Length > 1 && command[0] == "af" && command[1] == "add");
controller.Apply(ElySoundCatalog.BuiltIn[1], enabled: false, virtualSurround: false);
controller.Apply(ElySoundCatalog.BuiltIn[1], enabled: true, virtualSurround: false);
var graphAddsAfterBypass = issuedCommands.Count(command => command.Length > 1 && command[0] == "af" && command[1] == "add");
if (!firstApply.Applied || !secondApply.Applied)
    graphFailures.Add("nominal runtime application was rejected");
if (issuedCommands.Any(command => command.Any(argument => argument is "seek" or "loadfile" or "stop")))
    graphFailures.Add("a DSP transition touched media transport");
if (graphAddsAfterBypass != graphAddsBeforeBypass)
    graphFailures.Add("A/B bypass rebuilds the graph");
var graphAddsBeforeSameLayoutTrack = graphAddsAfterBypass;
controller.MediaChanged();
controller.TryApplyWhenAudioReady();
var graphAddsAfterSameLayoutTrack = issuedCommands.Count(command => command.Length > 1 && command[0] == "af" && command[1] == "add");
if (graphAddsAfterSameLayoutTrack != graphAddsBeforeSameLayoutTrack)
    graphFailures.Add("a track change with the same layout rebuilds the graph");
channelCount = "6";
controller.MediaChanged();
controller.TryApplyWhenAudioReady();
var graphAddsAfterLayoutChange = issuedCommands.Count(command => command.Length > 1 && command[0] == "af" && command[1] == "add");
if (graphAddsAfterLayoutChange != graphAddsAfterSameLayoutTrack + 1)
    graphFailures.Add("a real layout change does not rebuild the topology");

var rejectRuntime = false;
var fallbackCommands = new List<string[]>();
var fallbackController = new ElySoundController(
    property => property == "audio-params/channel-count" ? "2" : property == "af" ? "@elysound:lavfi=[]" : "",
    command =>
    {
        fallbackCommands.Add(command);
        return !(rejectRuntime && command.Length > 0 && command[0] == "af-command");
    });
fallbackController.Apply(ElySoundCatalog.BuiltIn[0], enabled: true, virtualSurround: false);
rejectRuntime = true;
var rejected = fallbackController.Apply(ElySoundCatalog.BuiltIn[1], enabled: true, virtualSurround: false);
if (rejected.Applied || !fallbackCommands.Any(command => command.SequenceEqual(new[] { "af", "remove", "@elysound" })))
    graphFailures.Add("DSP fallback removes more than @elysound");
foreach (var failure in graphFailures)
    Console.WriteLine("FAIL  ELYSOUND+ " + failure);
if (graphFailures.Count == 0)
    Console.WriteLine("PASS  ELYSOUND+ topology, mono bypass, limiter, and presets");

var localizationFailures = new List<string>();
if (!LocalizationCatalog.TryGetFrench("Sign in", out var signIn) || signIn != "Se connecter")
    localizationFailures.Add("exact lookup failed");
if (!LocalizationCatalog.TryGetFrenchTemplate("Track 42", out var track) || track != "Piste 42")
    localizationFailures.Add("formatted lookup failed");
foreach (var failure in localizationFailures)
    Console.WriteLine("FAIL  localization catalog " + failure);
if (localizationFailures.Count == 0)
    Console.WriteLine("PASS  localization catalog exact and formatted lookups");

return failures.Length == 0 && graphFailures.Count == 0 && localizationFailures.Count == 0 ? 0 : 1;
