using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Background analysis loop for the audio visualizer: streams the file,
/// computes a Hann-windowed FFT folded into log-spaced bands, tracks bass /
/// energy and detects beats - all OFF the UI thread, so the visual rendering
/// can run at monitor refresh rate without ever waiting on disk I/O or DSP.
/// The player position/state is fed from the UI thread (which owns the video
/// backend); this thread never touches mpv.
/// </summary>
public sealed class AudioVisualEngine : IDisposable
{
    public const int Bands = 56;
    private const int FftSize = 2048;
    private const double AnalysisHz = 120.0;
    private static readonly float[] Hann = Enumerable.Range(0, FftSize)
        .Select(i => (float)NAudio.Dsp.FastFourierTransform.HannWindow(i, FftSize)).ToArray();

    private readonly object _sync = new();
    private readonly double[] _spectrum = new double[Bands];
    private readonly ConcurrentQueue<double> _beats = new();

    private AudioFileReader? _reader;
    private Thread? _thread;
    private volatile bool _running;
    private long _sessionId;

    private long _playerPositionMs;
    private volatile bool _playerIsPlaying;

    private double _bass, _energy, _bassAverage, _bpm, _lastBeatSeconds;
    private double _analysisRateHz;
    private readonly int[] _bandStart = new int[Bands];
    private readonly int[] _bandEnd = new int[Bands];
    private readonly double[] _bandUpperHz = new double[Bands];
    private int _mappedSampleRate;

    /// <summary>False when the format cannot be decoded (idle animation).</summary>
    public bool HasAnalysis { get; private set; }

    public double EstimatedBpm { get { lock (_sync) return _bpm; } }
    public double ActualAnalysisRateHz => Volatile.Read(ref _analysisRateHz);

    public void Start(string path)
    {
        Stop();

        var sessionId = Interlocked.Increment(ref _sessionId);
        AudioFileReader? reader = null;
        var readBuffer = Array.Empty<float>();

        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                reader = new AudioFileReader(path);
                // Up to 250 ms of interleaved samples per tick.
                readBuffer = new float[Math.Max(2048,
                    reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 4)];
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Audio visualizer analysis unavailable: " + ex.Message);
            reader?.Dispose();
            reader = null;
        }

        _reader = reader;
        HasAnalysis = reader != null;
        // Play() has already been issued before the visualizer starts. Begin
        // analysis optimistically instead of waiting for the asynchronous
        // backend Playing event; subsequent UI snapshots remain authoritative.
        _playerIsPlaying = true;
        Volatile.Write(ref _analysisRateHz, 0);
        Interlocked.Exchange(ref _playerPositionMs, 0);
        _running = true;
        lock (_sync)
        {
            _bass = _energy = _bassAverage = _bpm = _lastBeatSeconds = 0;
            Array.Clear(_spectrum);
        }
        while (_beats.TryDequeue(out _)) { }

        _thread = new Thread(() => Loop(sessionId, reader, readBuffer, new float[FftSize], new NAudio.Dsp.Complex[FftSize]))
        {
            IsBackground = true,
            Name = "AudioVisualEngine",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Stop()
    {
        Interlocked.Increment(ref _sessionId);
        _running = false;
        var thread = _thread;
        var reader = _reader;
        try { if (thread != null) thread.Join(500); } catch { }
        _thread = null;
        _reader = null;

        // A stalled decoder owns its reader until its loop observes the cancelled
        // session. Disposing it from a new playback session caused two analyzers
        // to race over the same decoder and could throw ObjectDisposedException.
        if (thread == null) reader?.Dispose();
        HasAnalysis = false;
        Volatile.Write(ref _analysisRateHz, 0);

        lock (_sync)
        {
            Array.Clear(_spectrum);
            _bass = _energy = _bassAverage = _bpm = _lastBeatSeconds = 0;
        }
        while (_beats.TryDequeue(out _)) { }
    }

    public void Dispose() => Stop();

    /// <summary>Called from the UI thread every rendered frame.</summary>
    public void UpdatePlayerState(long positionMs, bool isPlaying)
    {
        Interlocked.Exchange(ref _playerPositionMs, positionMs);
        _playerIsPlaying = isPlaying;
    }

    public void ReadSnapshot(double[] spectrumOut, out double bass, out double energy)
    {
        // Never stall the UI behind the FFT publisher. Keeping the previous
        // 8 ms-old spectrum for one frame is invisible; blocking is not.
        if (Monitor.TryEnter(_sync))
        {
            try
            {
                Array.Copy(_spectrum, spectrumOut, Math.Min(spectrumOut.Length, Bands));
                bass = _bass;
                energy = _energy;
                return;
            }
            finally { Monitor.Exit(_sync); }
        }
        bass = Volatile.Read(ref _bass);
        energy = Volatile.Read(ref _energy);
    }

    public bool TryDequeueBeat(out double strength) => _beats.TryDequeue(out strength);

    // ---- analysis thread ----------------------------------------------------

    private void Loop(long sessionId, AudioFileReader? reader, float[] readBuffer,
        float[] window, NAudio.Dsp.Complex[] fft)
    {
        var clock = Stopwatch.StartNew();
        var last = 0.0;
        var nextTick = 0.0;
        var rateWindowStart = 0.0;
        var rateFrames = 0;

        try
        {
            while (_running && sessionId == Volatile.Read(ref _sessionId))
            {
                var now = clock.Elapsed.TotalSeconds;
                if (now < nextTick)
                {
                    var remainingMs = (nextTick - now) * 1000;
                    if (remainingMs >= 2) Thread.Sleep((int)remainingMs - 1);
                    else Thread.Yield();
                    continue;
                }
                nextTick += 1.0 / AnalysisHz;
                if (now - nextTick > 0.1) nextTick = now;
                var dt = Math.Clamp(now - last, 0.001, 0.25);
                last = now;

                try { Tick(sessionId, reader, readBuffer, window, fft, dt, now); }
                catch (Exception ex)
                {
                    DebugConsole.Warn("Audio visualizer analysis interrupted: " + ex.Message);
                    if (sessionId == Volatile.Read(ref _sessionId)) HasAnalysis = false;
                    break;
                }

                rateFrames++;
                if (now - rateWindowStart >= 0.75)
                {
                    Volatile.Write(ref _analysisRateHz, rateFrames / Math.Max(0.001, now - rateWindowStart));
                    rateFrames = 0;
                    rateWindowStart = now;
                }
            }
        }
        finally { reader?.Dispose(); }
    }

    private void Tick(long sessionId, AudioFileReader? reader, float[] readBuffer,
        float[] window, NAudio.Dsp.Complex[] fft, double dt, double now)
    {
        if (sessionId != Volatile.Read(ref _sessionId)) return;
        if (reader == null)
        {
            IdleWaves(sessionId, now);
            return;
        }

        if (!_playerIsPlaying)
        {
            // Paused: let everything settle instead of seek-storming the file.
            Decay(sessionId, dt);
            return;
        }

        if (!PumpSamples(reader, readBuffer, window, dt))
        {
            // The decoder reached EOF before the player state was refreshed.
            // Let the scene settle rather than repeatedly rendering the last FFT.
            Decay(sessionId, dt);
            return;
        }
        ComputeSpectrum(sessionId, reader, window, fft, now);
    }

    private void IdleWaves(long sessionId, double now)
    {
        if (sessionId != Volatile.Read(ref _sessionId)) return;
        lock (_sync)
        {
            for (var band = 0; band < Bands; band++)
                _spectrum[band] = (Math.Sin(now * 2.4 + band * 0.31) + 1) * 0.16 + 0.06;
            _bass = Math.Pow((Math.Sin(now * 2.1) + 1) * 0.5, 4) * 0.5;
            _energy = 0.25;
        }
    }

    private void Decay(long sessionId, double dt)
    {
        if (sessionId != Volatile.Read(ref _sessionId)) return;
        var k = Math.Pow(0.10, dt);
        lock (_sync)
        {
            for (var band = 0; band < Bands; band++) _spectrum[band] *= k;
            _bass *= k;
            _energy *= k;
        }
    }

    // Sequential streaming into the rolling mono window; only re-sync to the
    // player position on real drift (seek, lag) - re-seeking every tick is
    // what made the old implementation stutter on MP3.
    private bool PumpSamples(AudioFileReader reader, float[] readBuffer, float[] window, double dt)
    {
        var playerTime = TimeSpan.FromMilliseconds(Math.Max(0, Interlocked.Read(ref _playerPositionMs)));
        if (Math.Abs((reader.CurrentTime - playerTime).TotalMilliseconds) > 350 && playerTime < reader.TotalTime)
            reader.CurrentTime = playerTime;

        var channels = Math.Max(1, reader.WaveFormat.Channels);
        var wanted = Math.Min(readBuffer.Length,
            Math.Max(channels, (int)(dt * reader.WaveFormat.SampleRate) * channels));
        var read = reader.Read(readBuffer, 0, wanted);
        if (read < channels) return false;

        var frames = Math.Min(read / channels, FftSize);
        Array.Copy(window, frames, window, 0, FftSize - frames);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (read / channels - frames + frame) * channels;
            var mixed = 0f;
            for (var ch = 0; ch < channels; ch++) mixed += readBuffer[offset + ch];
            window[FftSize - frames + frame] = mixed / channels;
        }
        return true;
    }

    private void ComputeSpectrum(long sessionId, AudioFileReader reader, float[] window,
        NAudio.Dsp.Complex[] fft, double now)
    {
        var sampleRate = Math.Max(8000, reader.WaveFormat.SampleRate);

        for (var i = 0; i < FftSize; i++)
        {
            fft[i].X = window[i] * Hann[i];
            fft[i].Y = 0;
        }
        NAudio.Dsp.FastFourierTransform.FFT(true, 11 /* 2^11 = 2048 */, fft);

        const double fMin = 35.0;
        var fMax = Math.Min(16000.0, sampleRate * 0.45);
        EnsureBandMap(sampleRate, fMin, fMax);
        var bassRaw = 0.0;

        Span<double> values = stackalloc double[Bands];
        for (var band = 0; band < Bands; band++)
        {
            var b0 = _bandStart[band];
            var b1 = _bandEnd[band];

            var magnitude = 0.0;
            for (var bin = b0; bin < b1; bin++)
            {
                var c = fft[bin];
                magnitude = Math.Max(magnitude, Math.Sqrt(c.X * c.X + c.Y * c.Y));
            }

            var db = 20.0 * Math.Log10(magnitude + 1e-9);
            values[band] = Math.Pow(Math.Clamp((db + 63.0) / 54.0, 0, 1), 1.35);
        }

        var rms = 0.0;
        for (var i = 0; i < FftSize; i++) rms += window[i] * window[i];
        rms = Math.Clamp(Math.Sqrt(rms / FftSize) * 3.4, 0, 1);

        if (sessionId != Volatile.Read(ref _sessionId)) return;
        lock (_sync)
        {
            for (var band = 0; band < Bands; band++)
            {
                // Fast attack, slower release - punchy but stable.
                var current = _spectrum[band];
                _spectrum[band] = values[band] > current
                    ? current + (values[band] - current) * 0.62
                    : current * 0.74 + values[band] * 0.26;

                if (_bandUpperHz[band] <= 160) bassRaw = Math.Max(bassRaw, _spectrum[band]);
            }
            _bass = bassRaw;
            _energy = rms;
        }

        DetectBeat(sessionId, now, bassRaw);
    }

    private void EnsureBandMap(int sampleRate, double fMin, double fMax)
    {
        if (_mappedSampleRate == sampleRate) return;
        _mappedSampleRate = sampleRate;
        var binWidth = sampleRate / (double)FftSize;
        for (var band = 0; band < Bands; band++)
        {
            var f0 = fMin * Math.Pow(fMax / fMin, band / (double)Bands);
            var f1 = fMin * Math.Pow(fMax / fMin, (band + 1) / (double)Bands);
            _bandStart[band] = Math.Clamp((int)(f0 / binWidth), 1, FftSize / 2 - 1);
            _bandEnd[band] = Math.Clamp((int)Math.Ceiling(f1 / binWidth), _bandStart[band] + 1, FftSize / 2);
            _bandUpperHz[band] = f1;
        }
    }

    // Bass onset against a slow-moving average; beats are queued for the UI.
    private void DetectBeat(long sessionId, double now, double bass)
    {
        if (sessionId != Volatile.Read(ref _sessionId)) return;
        _bassAverage = _bassAverage * 0.94 + bass * 0.06;

        var isBeat = bass > 0.28
                     && bass > _bassAverage * 1.32 + 0.03
                     && now - _lastBeatSeconds > 0.24;
        if (!isBeat) return;

        var interval = now - _lastBeatSeconds;
        _lastBeatSeconds = now;
        if (interval is > 0.28 and < 1.1)
        {
            var bpm = 60.0 / interval;
            lock (_sync) _bpm = _bpm <= 0 ? bpm : _bpm * 0.72 + bpm * 0.28;
        }

        if (_beats.Count < 8) _beats.Enqueue(bass);
    }
}
