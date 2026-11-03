using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Services.Export;

public enum ClipExportOutcome { Completed, Cancelled, Failed }

public sealed record ClipExportResult(ClipExportOutcome Outcome, string Message, string? Encoder = null);

/// <param name="Fraction">0..1 of the requested range (or of the whole recording).</param>
/// <param name="Speed">Encoded media seconds per wall-clock second.</param>
public sealed record ClipExportProgress(double Fraction, double Speed, string Encoder);

/// <summary>
/// Renders a clip offline through a dedicated libmpv instance in encoding mode.
/// It never touches the playback backend: the picture goes through mpv's GPU
/// renderer (<c>vf=gpu</c>) with the options from <see cref="ClipExportPlanner"/>
/// and is encoded to H.264/AAC MP4. Hardware encoders are tried first; an
/// encoder that cannot open falls through to the next one, ending at libx264.
/// </summary>
public sealed class ClipExporter
{
    private const int EventShutdown = 1;
    private const int EventLogMessage = 2;
    private const int EventEndFile = 7;
    private const int EventFileLoaded = 8;
    private const int EndReasonError = 4;

    // Encoders that already failed to open on this machine are skipped for the
    // rest of the session.
    private static readonly HashSet<string> UnavailableEncoders = new(StringComparer.Ordinal);
    private static readonly object EncoderLock = new();

    public Task<ClipExportResult> ExportAsync(ClipExportRequest request, Settings settings,
        IProgress<ClipExportProgress>? progress, CancellationToken cancellationToken)
    {
        // Options are built on the caller's thread: Settings belongs to the UI.
        var attempts = Encoders()
            .Select(encoder => (Encoder: encoder, Options: ClipExportPlanner.BuildOptions(request, settings, encoder)))
            .ToList();
        return Task.Factory.StartNew(() => Run(request, attempts, progress, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static IEnumerable<string> Encoders()
    {
        lock (EncoderLock)
            return ClipExportPlanner.EncoderPreference.Where(e => !UnavailableEncoders.Contains(e)).ToList();
    }

    private static ClipExportResult Run(ClipExportRequest request,
        IReadOnlyList<(string Encoder, IReadOnlyList<KeyValuePair<string, string>> Options)> attempts,
        IProgress<ClipExportProgress>? progress, CancellationToken token)
    {
        if (!EnsureLibMpv())
            return new(ClipExportOutcome.Failed, "libmpv is not installed. Install mpv from the playback settings.");

        var folder = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        foreach (var (encoder, options) in attempts)
        {
            token.ThrowIfCancellationRequested();
            DebugConsole.Step($"Clip export: encoding with {encoder} ({request.OutputWidth}x{request.OutputHeight})...");
            var attempt = Encode(request, encoder, options, progress, token);
            if (attempt.Outcome == AttemptOutcome.EncoderUnavailable)
            {
                DebugConsole.Warn($"Clip export: {encoder} unavailable on this machine, trying the next encoder.");
                lock (EncoderLock) UnavailableEncoders.Add(encoder);
                TryDelete(request.OutputPath);
                continue;
            }
            if (attempt.Outcome == AttemptOutcome.Cancelled)
            {
                TryDelete(request.OutputPath);
                return new(ClipExportOutcome.Cancelled, "Export cancelled.", encoder);
            }
            if (attempt.Outcome == AttemptOutcome.Failed)
            {
                TryDelete(request.OutputPath);
                return new(ClipExportOutcome.Failed, attempt.Message, encoder);
            }

            var info = new FileInfo(request.OutputPath);
            if (!info.Exists || info.Length < 1024)
            {
                TryDelete(request.OutputPath);
                return new(ClipExportOutcome.Failed, "The encoder produced no video.", encoder);
            }
            DebugConsole.Success($"Clip export: {info.Length / 1048576.0:0.0} MB written with {encoder}.");
            return new(ClipExportOutcome.Completed, "Export complete.", encoder);
        }
        return new(ClipExportOutcome.Failed, "No video encoder could be opened.");
    }

    private enum AttemptOutcome { Completed, Cancelled, Failed, EncoderUnavailable }

    private readonly record struct Attempt(AttemptOutcome Outcome, string Message = "");

    private static Attempt Encode(ClipExportRequest request, string encoder,
        IReadOnlyList<KeyValuePair<string, string>> options, IProgress<ClipExportProgress>? progress, CancellationToken token)
    {
        var handle = mpv_create();
        if (handle == IntPtr.Zero) return new(AttemptOutcome.Failed, "libmpv could not be started.");
        try
        {
            foreach (var (name, value) in options)
            {
                var code = mpv_set_option_string(handle, name, value);
                if (code < 0)
                {
                    // Optional quality options may be missing on an older libmpv;
                    // the encoding options themselves are mandatory.
                    if (name is "o" or "ovc" or "vf") return new(AttemptOutcome.Failed, "libmpv rejected the export settings.");
                    DebugConsole.Warn($"Clip export: option {name} rejected (code {code}).");
                }
            }
            mpv_request_log_messages(handle, "error");
            if (mpv_initialize(handle) < 0) return new(AttemptOutcome.Failed, "libmpv could not be started.");
            if (MpvInterop.Command(handle, "loadfile", request.Source) < 0)
                return new(AttemptOutcome.Failed, "Could not open the source.");

            var clock = Stopwatch.StartNew();
            var start = request.StartSeconds ?? 0;
            double length = request.EndSeconds is { } end ? Math.Max(0.1, end - start) : 0;
            var quitSent = false;
            var encoderFailed = false;
            var sourceFailed = false;
            var lastError = "";
            var lastReport = TimeSpan.Zero;

            while (true)
            {
                if (token.IsCancellationRequested && !quitSent)
                {
                    MpvInterop.Command(handle, "quit");
                    quitSent = true;
                }

                var ev = Marshal.PtrToStructure<MpvEvent>(mpv_wait_event(handle, 0.2));
                switch (ev.EventId)
                {
                    case EventShutdown:
                        return Finish();
                    case EventFileLoaded when length <= 0:
                        length = Math.Max(0, MpvInterop.GetDouble(handle, "duration") - start);
                        break;
                    case EventLogMessage when ev.Data != IntPtr.Zero:
                    {
                        var log = Marshal.PtrToStructure<MpvLogMessage>(ev.Data);
                        var text = (Marshal.PtrToStringUTF8(log.Text) ?? "").Trim();
                        var prefix = Marshal.PtrToStringUTF8(log.Prefix) ?? "";
                        if (prefix.StartsWith("vo/lavc", StringComparison.Ordinal) ||
                            text.Contains("Could not initialize encoder", StringComparison.Ordinal) ||
                            text.Contains("hardware device context", StringComparison.Ordinal))
                            encoderFailed = true;
                        // Error lines can quote the source (and its credentials).
                        lastError = Redact(text);
                        DebugConsole.Warn($"Clip export [{prefix}]: {lastError}");
                        if (encoderFailed && !quitSent) { MpvInterop.Command(handle, "quit"); quitSent = true; }
                        break;
                    }
                    case EventEndFile when ev.Data != IntPtr.Zero:
                    {
                        var endFile = Marshal.PtrToStructure<MpvEndFile>(ev.Data);
                        // An error end is a source failure (refused connection,
                        // unreadable file) unless the encoder already failed.
                        if (endFile.Reason == EndReasonError && !encoderFailed) sourceFailed = true;
                        if (!quitSent) { MpvInterop.Command(handle, "quit"); quitSent = true; }
                        break;
                    }
                }

                if (progress != null && clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    lastReport = clock.Elapsed;
                    var done = Math.Max(0, MpvInterop.GetDouble(handle, "time-pos") - start);
                    var fraction = length > 0 ? Math.Clamp(done / length, 0, 1) : 0;
                    progress.Report(new(fraction, done / Math.Max(0.001, clock.Elapsed.TotalSeconds), encoder));
                }
            }

            Attempt Finish()
            {
                if (token.IsCancellationRequested) return new(AttemptOutcome.Cancelled);
                if (encoderFailed) return new(AttemptOutcome.EncoderUnavailable, lastError);
                if (sourceFailed) return new(AttemptOutcome.Failed, "Could not open the source.");
                if (!string.IsNullOrEmpty(lastError) && !File.Exists(request.OutputPath))
                    return new(AttemptOutcome.Failed, lastError);
                return new(AttemptOutcome.Completed);
            }
        }
        finally
        {
            // Finalizes the muxer (moov atom) before the file is inspected.
            mpv_terminate_destroy(handle);
        }
    }

    private static string Redact(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"[a-zA-Z][a-zA-Z0-9+.-]*://\S+", "<source>");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { DebugConsole.Warn("Clip export: could not delete the partial file: " + ex.Message); }
    }

    private static bool _libMpvLoaded;

    // DllImport resolves "libmpv-2" by module name. When the active backend is
    // VLC, libmpv was never loaded: load it from its install location first.
    private static bool EnsureLibMpv()
    {
        if (_libMpvLoaded) return true;
        var path = MpvBackend.LocateNative();
        _libMpvLoaded = path != null && NativeLibrary.TryLoad(path, out _);
        return _libMpvLoaded;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEndFile
    {
        public int Reason;
        public int Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvLogMessage
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_create();

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_initialize(IntPtr ctx);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_request_log_messages(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
}
