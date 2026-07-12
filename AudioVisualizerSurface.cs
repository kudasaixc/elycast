using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using Elysium_Cast_IPTV.Services;

namespace Elysium_Cast_IPTV;

/// <summary>
/// Single-pass renderer for the audio visualizer: the mirrored spectrum bars,
/// the orbiting/burst particles and the beat shockwaves are all drawn in one
/// OnRender call using cached frozen pens and brushes — zero allocation per
/// frame. Driven every displayed frame (CompositionTarget.Rendering), so it
/// runs at the monitor's refresh rate. The previous implementation animated
/// 170+ WPF elements with a fresh SolidColorBrush per bar per 30 Hz tick,
/// which trashed the GC and stuttered visibly.
/// </summary>
public sealed class AudioVisualizerSurface : FrameworkElement
{
    public const int BarCount = 112;
    private const double InnerRadius = 198;
    public const int MaxParticleCount = 192;
    public const int MaxShockwaveCount = 6;
    private const int HueSteps = 32;
    private const int AlphaSteps = 8;
    private const int ThicknessSteps = 10;

    // Fixed-size direct caches: no dictionary hashing and no unbounded growth.
    private readonly SolidColorBrush?[,] _brushCache = new SolidColorBrush?[HueSteps, AlphaSteps + 1];
    private readonly Pen?[,,,] _penCache = new Pen?[HueSteps, AlphaSteps + 1, ThicknessSteps, 2];
    private readonly LinePrimitive[] _renderBars = new LinePrimitive[BarCount];
    private readonly EllipsePrimitive[] _renderParticles = new EllipsePrimitive[MaxParticleCount];
    private readonly EllipsePrimitive[] _renderWaves = new EllipsePrimitive[MaxShockwaveCount];
    private static readonly double[] BarCos = Enumerable.Range(0, BarCount).Select(i => Math.Cos(i / (double)BarCount * Math.Tau - Math.PI / 2)).ToArray();
    private static readonly double[] BarSin = Enumerable.Range(0, BarCount).Select(i => Math.Sin(i / (double)BarCount * Math.Tau - Math.PI / 2)).ToArray();

    private struct Particle
    {
        public double X, Y, Vx, Vy, Life;
        public double OrbitAngle, OrbitRadius, OrbitSpeed;
        public double Size, Hue, BaseOpacity;
        public bool Burst;
    }

    private struct Shockwave
    {
        public double Age;
        public double Strength;
        public double Hue;
    }

    /// <summary>
    /// Resolved WPF drawing primitives. AudioCore+ consumes these exact
    /// positions, quantized colours and pen widths instead of maintaining a
    /// second animation simulation that can drift from the classic renderer.
    /// Coordinates are local DIPs in this element.
    /// </summary>
    public struct LinePrimitive
    {
        public double X0, Y0, X1, Y1, Thickness;
        public Color Color;
        public Pen? Pen;
    }

    public struct EllipsePrimitive
    {
        public double X, Y, RadiusX, RadiusY, Thickness;
        public Color Color;
        public SolidColorBrush? Brush;
        public Pen? Pen;
    }

    private readonly double[] _display = new double[AudioVisualEngine.Bands];
    private readonly Particle[] _particles = new Particle[MaxParticleCount];
    private readonly List<Shockwave> _waves = new(8);
    private readonly Random _random = new();

    private double _clock;
    private double _bass, _energy, _beatPulse;
    private bool _particlesInitialized;
    private Color[]? _palette;
    private double _averageRenderTimeMs;

    public int ActiveParticleCount { get; set; } = 96;
    public double ParticleDistance { get; set; } = 1.0;
    public double AverageRenderTimeMs => Volatile.Read(ref _averageRenderTimeMs);

    public void SetPalette(Color[]? colors)
    {
        _palette = colors is { Length: >= 2 } ? colors.ToArray() : null;
        Array.Clear(_brushCache);
        Array.Clear(_penCache);
        InvalidateVisual();
    }

    // Matches the Viewbox scaling of the rings (24 px margin, native 560),
    // so bars, particles and waves shrink with the window instead of
    // overflowing their cell.
    private double _scale = 1.0;

    private void UpdateScale()
    {
        var available = Math.Min(ActualWidth, ActualHeight) - 48;
        _scale = Math.Clamp(available / 560.0, 0.2, 1.0);
    }

    public AudioVisualizerSurface()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        SizeChanged += (_, _) => UpdateScale();
    }

    private void EnsureParticles()
    {
        if (_particlesInitialized) return;
        _particlesInitialized = true;
        for (var i = 0; i < _particles.Length; i++)
        {
            _particles[i] = new Particle
            {
                OrbitAngle = _random.NextDouble() * Math.Tau,
                // Spread far around the rings, almost static at rest — they
                // only pick up speed when a beat pumps the pulse (see below).
                OrbitRadius = 210 + _random.NextDouble() * 360,
                OrbitSpeed = 0.015 + _random.NextDouble() * 0.035,
                Size = 2 + _random.NextDouble() * 4,
                Hue = _random.NextDouble() * 360,
                BaseOpacity = 0.28 + _random.NextDouble() * 0.45
            };
        }
    }

    /// <summary>Clears transient state (bursts, waves) when the layer hides.</summary>
    public void ResetScene()
    {
        _waves.Clear();
        _beatPulse = 0;
        for (var i = 0; i < _particles.Length; i++) _particles[i].Burst = false;
        Array.Clear(_display);
        InvalidateVisual();
    }

    /// <summary>Per-frame update: interpolates towards the engine snapshot.</summary>
    public void Advance(double dt, double[] targetSpectrum, double bass, double energy, double beatPulse)
    {
        EnsureParticles();
        _clock += dt;
        _bass = bass;
        _energy = energy;
        _beatPulse = beatPulse;

        // Frame-rate–independent interpolation between analysis snapshots
        // (the engine updates ~60/s, the display often at 144 Hz).
        var blend = 1.0 - Math.Exp(-26.0 * dt);
        for (var i = 0; i < _display.Length && i < targetSpectrum.Length; i++)
            _display[i] += (targetSpectrum[i] - _display[i]) * blend;

        UpdateParticles(dt);

        for (var i = _waves.Count - 1; i >= 0; i--)
        {
            var w = _waves[i];
            w.Age += dt;
            if (w.Age >= 0.65) _waves.RemoveAt(i);
            else _waves[i] = w;
        }

        InvalidateVisual();
    }

    /// <summary>Bass beat: ejects particles across the player + a shockwave.</summary>
    public void Beat(double strength)
    {
        EnsureParticles();
        var centerX = ActualWidth / 2;
        var centerY = ActualHeight / 2;
        var count = 14 + (int)(strength * 26);

        for (var i = 0; i < Math.Min(ActiveParticleCount, _particles.Length) && count > 0; i++)
        {
            if (_particles[i].Burst) continue;
            count--;

            var angle = _random.NextDouble() * Math.Tau;
            var speed = (320 + _random.NextDouble() * 480 * (0.5 + strength)) * Math.Max(0.45, _scale);
            ref var p = ref _particles[i];
            p.Burst = true;
            p.X = centerX + Math.Cos(angle) * 175 * _scale;
            p.Y = centerY + Math.Sin(angle) * 175 * _scale;
            p.Vx = Math.Cos(angle) * speed;
            p.Vy = Math.Sin(angle) * speed;
            p.Life = 1.0;
            p.Hue = _random.NextDouble() * 360;
        }

        if (_waves.Count < 6)
            _waves.Add(new Shockwave { Strength = strength, Hue = (_clock * 40) % 360 });
    }

    private void UpdateParticles(double dt)
    {
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        var centerX = width / 2;
        var centerY = height / 2;

        for (var i = 0; i < Math.Min(ActiveParticleCount, _particles.Length); i++)
        {
            ref var p = ref _particles[i];
            if (p.Burst)
            {
                p.X += p.Vx * dt;
                p.Y += p.Vy * dt;
                p.Vx *= 1.0 - 0.55 * dt;
                p.Vy *= 1.0 - 0.55 * dt;
                p.Life -= dt * 0.85;

                if (p.Life <= 0 || p.X < -20 || p.Y < -20 || p.X > width + 20 || p.Y > height + 20)
                {
                    p.Burst = false;
                    p.OrbitAngle = _random.NextDouble() * Math.Tau;
                    p.OrbitRadius = 210 + _random.NextDouble() * 360;
                }
            }
            else
            {
                // Nearly frozen drift at rest; each beat whips the orbit for
                // ~half a second (the pulse decays), then everything settles.
                p.OrbitAngle += p.OrbitSpeed * dt * (1.0 + _beatPulse * 18 + _bass * 1.5);
                var wobble = Math.Sin(_clock * 1.3 + p.OrbitRadius) * 5;
                p.X = centerX + Math.Cos(p.OrbitAngle) * (p.OrbitRadius + wobble) * _scale * ParticleDistance;
                p.Y = centerY + Math.Sin(p.OrbitAngle) * (p.OrbitRadius + wobble) * _scale * ParticleDistance;
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var renderStarted = Stopwatch.GetTimestamp();
        CopyResolvedPrimitives(_renderBars, out var barCount,
            _renderParticles, out var particleCount, _renderWaves, out var waveCount);
        for (var i = 0; i < particleCount; i++)
        {
            ref var particle = ref _renderParticles[i];
            dc.DrawEllipse(particle.Brush, null, new Point(particle.X, particle.Y), particle.RadiusX, particle.RadiusY);
        }
        for (var i = 0; i < waveCount; i++)
        {
            ref var wave = ref _renderWaves[i];
            dc.DrawEllipse(null, wave.Pen, new Point(wave.X, wave.Y), wave.RadiusX, wave.RadiusY);
        }
        for (var i = 0; i < barCount; i++)
        {
            ref var bar = ref _renderBars[i];
            dc.DrawLine(bar.Pen!, new Point(bar.X0, bar.Y0), new Point(bar.X1, bar.Y1));
        }
        var elapsed = Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
        _averageRenderTimeMs = _averageRenderTimeMs <= 0 ? elapsed : _averageRenderTimeMs * 0.94 + elapsed * 0.06;
    }

    /// <summary>
    /// Copies the already-advanced scene using the very same formulas and
    /// cached WPF styles as <see cref="OnRender"/>. The caller owns the arrays;
    /// this method performs no allocation and does not advance animation time.
    /// </summary>
    public void CopyResolvedPrimitives(
        LinePrimitive[] bars, out int barCount,
        EllipsePrimitive[] particles, out int particleCount,
        EllipsePrimitive[] waves, out int waveCount)
    {
        barCount = particleCount = waveCount = 0;
        if (ActualWidth < 1 || ActualHeight < 1) return;

        var centerX = ActualWidth / 2;
        var centerY = ActualHeight / 2;

        var activeParticles = Math.Min(Math.Min(ActiveParticleCount, _particles.Length), particles.Length);
        for (var i = 0; i < activeParticles; i++)
        {
            ref var p = ref _particles[i];
            var opacity = p.Burst ? Math.Clamp(p.Life, 0, 1.0) : Math.Min(0.82, p.BaseOpacity * 0.82);
            var brush = CachedBrush(p.Hue, opacity);
            var radius = p.Size / 2;
            particles[particleCount++] = new EllipsePrimitive
            {
                X = p.X,
                Y = p.Y,
                RadiusX = radius,
                RadiusY = radius,
                Thickness = 0,
                Color = brush.Color,
                Brush = brush
            };
        }

        var activeWaves = Math.Min(_waves.Count, waves.Length);
        for (var i = 0; i < activeWaves; i++)
        {
            var w = _waves[i];
            var progress = w.Age / 0.65;
            var eased = 1 - (1 - progress) * (1 - progress);
            var radius = InnerRadius * _scale * (0.86 + eased * (1.04 + w.Strength * 0.5));
            var opacity = (0.55 + w.Strength * 0.35) * (1 - progress);
            var pen = CachedPen(w.Hue, opacity, 2.5 + w.Strength * 3.5, rounded: false);
            waves[waveCount++] = new EllipsePrimitive
            {
                X = centerX,
                Y = centerY,
                RadiusX = radius,
                RadiusY = radius,
                Thickness = pen.Thickness,
                Color = ((SolidColorBrush)pen.Brush).Color,
                Pen = pen
            };
        }

        var count = Math.Min(BarCount, bars.Length);
        var half = _display.Length;
        for (var i = 0; i < count; i++)
        {
            var mirrored = i < BarCount / 2 ? i : BarCount - 1 - i;
            var band = _display[Math.Min(half - 1, mirrored * half / (BarCount / 2))];
            var isBassBar = mirrored < half / 5;
            var kick = isBassBar ? _beatPulse : _beatPulse * 0.35;
            var radius = InnerRadius * _scale;
            var length = (12 + band * 118 + _energy * 18 + kick * 46) * _scale;
            var hue = (i * 360.0 / BarCount + _clock * 26) % 360;
            var opacity = 0.70 + Math.Min(0.30, band * 0.48 + kick * 0.28);
            var requestedThickness = (3.0 + band * 1.6 + kick * 1.6) * Math.Max(0.55, _scale);
            var pen = CachedPen(hue, opacity, requestedThickness, rounded: true);
            var cos = BarCos[i];
            var sin = BarSin[i];
            bars[barCount++] = new LinePrimitive
            {
                X0 = centerX + cos * radius,
                Y0 = centerY + sin * radius,
                X1 = centerX + cos * (radius + length),
                Y1 = centerY + sin * (radius + length),
                Thickness = pen.Thickness,
                Color = ((SolidColorBrush)pen.Brush).Color,
                Pen = pen
            };
        }
    }

    // ---- quantized frozen style caches --------------------------------------

    private SolidColorBrush CachedBrush(double hue, double opacity)
    {
        var hueIdx = ((int)(hue / (360.0 / HueSteps)) % HueSteps + HueSteps) % HueSteps;
        var alphaIdx = Math.Clamp((int)Math.Round(opacity * AlphaSteps), 0, AlphaSteps);
        var brush = _brushCache[hueIdx, alphaIdx];
        if (brush != null) return brush;

        brush = new SolidColorBrush(PaletteColor(hueIdx * (360.0 / HueSteps), alphaIdx / (double)AlphaSteps));
        brush.Freeze();
        _brushCache[hueIdx, alphaIdx] = brush;
        return brush;
    }

    private Pen CachedPen(double hue, double opacity, double thickness, bool rounded)
    {
        var hueIdx = ((int)(hue / (360.0 / HueSteps)) % HueSteps + HueSteps) % HueSteps;
        var alphaIdx = Math.Clamp((int)Math.Round(opacity * AlphaSteps), 0, AlphaSteps);
        var thickIdx = Math.Clamp((int)Math.Round((thickness - 1) / 0.75), 0, ThicknessSteps - 1);
        var roundIdx = rounded ? 1 : 0;
        var pen = _penCache[hueIdx, alphaIdx, thickIdx, roundIdx];
        if (pen != null) return pen;

        pen = new Pen(CachedBrush(hueIdx * (360.0 / HueSteps), alphaIdx / (double)AlphaSteps), 1 + thickIdx * 0.75);
        if (rounded)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        pen.Freeze();
        _penCache[hueIdx, alphaIdx, thickIdx, roundIdx] = pen;
        return pen;
    }

    private Color PaletteColor(double hue, double opacity)
    {
        if (_palette is not { Length: >= 2 }) return HsvColor(hue, 0.9, 1.0, opacity);
        var position = ((hue % 360 + 360) % 360) / 360.0 * _palette.Length;
        var index = (int)position % _palette.Length;
        var next = (index + 1) % _palette.Length;
        var t = position - Math.Floor(position);
        var a = _palette[index]; var b = _palette[next];
        var mixed = Color.FromRgb((byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        var (paletteHue, saturation, value) = RgbToHsv(mixed);
        // Preserve the source hue, but create a luminous accent variant. Raw
        // dominant colours are often as dark as the blurred pixels behind them.
        // Achromatic artwork stays achromatic: forcing its near-zero saturation
        // to the coloured minimum used to turn black/white covers bright blue.
        if (saturation < 0.14)
            return HsvColor(0, 0, Math.Clamp(value * 1.35, 0.68, 1.0), opacity);
        saturation = Math.Clamp(saturation * 1.18, 0.68, 0.94);
        value = Math.Clamp(value * 1.42, 0.84, 1.0);
        return HsvColor(paletteHue, saturation, value, opacity);
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        var r = color.R / 255.0; var g = color.G / 255.0; var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static Color HsvColor(double hue, double saturation, double value, double opacity)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromArgb(
            (byte)Math.Clamp(opacity * 255, 0, 255),
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
