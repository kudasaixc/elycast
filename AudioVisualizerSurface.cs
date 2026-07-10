using System.Windows;
using System.Windows.Media;
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
    private const int BarCount = 112;
    private const double InnerRadius = 198;
    private const int ParticleCount = 96;

    // Quantized style caches (frozen = shareable across frames, GPU-friendly).
    private static readonly Dictionary<int, Pen> PenCache = new();
    private static readonly Dictionary<int, SolidColorBrush> BrushCache = new();

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

    private readonly double[] _display = new double[AudioVisualEngine.Bands];
    private readonly Particle[] _particles = new Particle[ParticleCount];
    private readonly List<Shockwave> _waves = new(8);
    private readonly Random _random = new();

    private double _clock;
    private double _bass, _energy, _beatPulse;
    private bool _particlesInitialized;

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
        UpdateScale();
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

        for (var i = 0; i < _particles.Length && count > 0; i++)
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

        for (var i = 0; i < _particles.Length; i++)
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
                p.X = centerX + Math.Cos(p.OrbitAngle) * (p.OrbitRadius + wobble) * _scale;
                p.Y = centerY + Math.Sin(p.OrbitAngle) * (p.OrbitRadius + wobble) * _scale;
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        // Particles (under the bars).
        for (var i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            var opacity = p.Burst ? Math.Clamp(p.Life, 0, 0.95) : p.BaseOpacity * 0.55;
            var brush = CachedBrush(p.Hue, opacity);
            var r = p.Size / 2;
            dc.DrawEllipse(brush, null, new Point(p.X, p.Y), r, r);
        }

        // Shockwaves.
        foreach (var w in _waves)
        {
            var progress = w.Age / 0.65;
            var eased = 1 - (1 - progress) * (1 - progress);
            var radius = InnerRadius * _scale * (0.86 + eased * (1.04 + w.Strength * 0.5));
            var opacity = (0.55 + w.Strength * 0.35) * (1 - progress);
            var pen = CachedPen(w.Hue, opacity, 2.5 + w.Strength * 3.5, rounded: false);
            dc.DrawEllipse(null, pen, center, radius, radius);
        }

        // Mirrored circular spectrum: bass at the top, treble at the bottom,
        // identical on both sides — each bar reflects a real frequency band.
        var half = _display.Length;
        for (var i = 0; i < BarCount; i++)
        {
            var mirrored = i < BarCount / 2 ? i : BarCount - 1 - i;
            var band = _display[Math.Min(half - 1, mirrored * half / (BarCount / 2))];
            var isBassBar = mirrored < half / 5;
            var kick = isBassBar ? _beatPulse : _beatPulse * 0.35;
            var radius = InnerRadius * _scale;
            var length = (12 + band * 118 + _energy * 18 + kick * 46) * _scale;

            var angle = i / (double)BarCount * Math.Tau - Math.PI / 2;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var hue = (i * 360.0 / BarCount + _clock * 26) % 360;
            var opacity = 0.5 + Math.Min(0.5, band * 0.55 + kick * 0.3);
            var thickness = (3.0 + band * 1.6 + kick * 1.6) * Math.Max(0.55, _scale);

            var pen = CachedPen(hue, opacity, thickness, rounded: true);
            dc.DrawLine(pen,
                new Point(center.X + cos * radius, center.Y + sin * radius),
                new Point(center.X + cos * (radius + length), center.Y + sin * (radius + length)));
        }
    }

    // ---- quantized frozen style caches --------------------------------------

    private static SolidColorBrush CachedBrush(double hue, double opacity)
    {
        var hueIdx = ((int)(hue / 4) % 90 + 90) % 90;
        var alphaIdx = Math.Clamp((int)(opacity * 12), 0, 12);
        var key = hueIdx * 100 + alphaIdx;
        if (BrushCache.TryGetValue(key, out var brush)) return brush;

        brush = new SolidColorBrush(HsvColor(hueIdx * 4, 0.9, 1.0, alphaIdx / 12.0));
        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    private static Pen CachedPen(double hue, double opacity, double thickness, bool rounded)
    {
        var hueIdx = ((int)(hue / 4) % 90 + 90) % 90;
        var alphaIdx = Math.Clamp((int)(opacity * 12), 0, 12);
        var thickIdx = Math.Clamp((int)(thickness * 2), 2, 24);
        var key = ((hueIdx * 100 + alphaIdx) * 100 + thickIdx) * 2 + (rounded ? 1 : 0);
        if (PenCache.TryGetValue(key, out var pen)) return pen;

        pen = new Pen(CachedBrush(hueIdx * 4, alphaIdx / 12.0), thickIdx / 2.0);
        if (rounded)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        pen.Freeze();
        PenCache[key] = pen;
        return pen;
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
