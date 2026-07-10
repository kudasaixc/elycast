using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// A real Win32 console attached to the WPF process. Shows an animated boot
/// sequence (ASCII banner + spinning 3D cube), streams colour-coded logs for
/// the developer, and runs an interactive command interpreter (type "help").
/// </summary>
public static class DebugConsole
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9;

    private static readonly object _lock = new();
    private static bool _visible = true;
    private static readonly Dictionary<string, (string desc, Func<string[], string?> handler)> _commands = new();

    // Persistent log file so crashes can be diagnosed after the fact even when
    // the Win32 console window is gone (e.g. a native access violation).
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElyCast", "log.txt");
    private static readonly object _fileLock = new();

    public static string LogFilePath => LogPath;

    /// <summary>
    /// File-only, high-frequency trace (no console output). Used to pinpoint the
    /// exact native render call at which a crash occurs without flooding the
    /// console. Includes milliseconds.
    /// </summary>
    public static void Trace(string msg) => AppendToFile($"{DateTime.Now:HH:mm:ss.fff} [TRC ] {msg}");

    private static void AppendToFile(string line)
    {
        try
        {
            lock (_fileLock)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* never let logging crash the app */ }
    }

    // ---------------------------------------------------------------- setup
    public static void Initialize()
    {
        // Start every run with a fresh log file so the latest reproduction is
        // the only thing in it.
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(LogPath, $"=== ElyCast log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch { /* ignore */ }

        AllocConsole();

        // Re-bind the standard streams so Console.ReadLine / WriteLine work in
        // a process that was not launched from a console.
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* ignore */ }

        Console.Title = "ElyCast — Debug Console";
        Console.CursorVisible = false;
        try { Console.SetWindowSize(Math.Min(110, Console.LargestWindowWidth),
                                    Math.Min(36, Console.LargestWindowHeight)); } catch { }

        RegisterBuiltins();
    }

    // ------------------------------------------------------------ boot anim
    /// <summary>
    /// Plays the animated boot screen for roughly <paramref name="seconds"/>
    /// seconds while running the given background work.
    /// </summary>
    public static void RunBootSequence(double seconds, Action initializationWork)
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Magenta;
        foreach (var line in Banner) Console.WriteLine(line);
        Console.ResetColor();

        var tasks = new[]
        {
            "Initialisation du runtime WPF",
            "PrÃ©paration des backends vidÃ©o",
            "Préparation du cache vidéo",
            "Lecture des profils utilisateur",
            "Application du thème (pitch black · violet)",
            "Démarrage de l'interface"
        };

        int bannerHeight = Banner.Length + 1;
        var sw = Stopwatch.StartNew();
        bool workStarted = false, workDone = false;
        double angle = 0;

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double progress = sw.Elapsed.TotalSeconds / seconds;

            // kick off the real init work a moment in
            if (!workStarted && progress > 0.15)
            {
                workStarted = true;
                Task.Run(() => { try { initializationWork(); } catch (Exception ex) { Error(ex.Message); } finally { workDone = true; } });
            }

            RenderFrame(bannerHeight, angle, tasks, progress, workDone);
            angle += 0.13;
            Thread.Sleep(40);
        }

        // make sure init work finished before we hand over to the UI
        while (!workDone && sw.Elapsed.TotalSeconds < seconds + 8) Thread.Sleep(50);

        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Magenta;
        foreach (var line in Banner) Console.WriteLine(line);
        Console.ResetColor();
        Success("Système prêt. L'interface est lancée.");
        Info("Tape 'help' pour la liste des commandes disponibles.");
        WritePrompt();
    }

    private static void RenderFrame(int top, double angle, string[] tasks, double progress, bool workDone)
    {
        lock (_lock)
        {
            const int W = 44, H = 22;
            var buf = RenderCube(W, H, angle);

            Console.SetCursorPosition(0, top);
            int doneCount = (int)Math.Round(progress * tasks.Length);

            for (int y = 0; y < H; y++)
            {
                // cube column
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write("   " + new string(buf, y * W, W));

                // task / log column
                Console.ForegroundColor = ConsoleColor.Gray;
                string side = "";
                if (y >= 2 && y - 2 < tasks.Length)
                {
                    int ti = y - 2;
                    bool done = ti < doneCount;
                    Console.Write("   ");
                    Console.ForegroundColor = done ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.Write(done ? "[ OK ] " : "[ .. ] ");
                    Console.ForegroundColor = done ? ConsoleColor.White : ConsoleColor.DarkGray;
                    Console.Write(tasks[ti].PadRight(40));
                }
                else if (y == tasks.Length + 4)
                {
                    int barLen = 40;
                    int filled = (int)(progress * barLen);
                    Console.Write("   ");
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write("[");
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write(new string('█', filled));
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(new string('░', barLen - filled));
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write($"] {(int)(progress * 100),3}%   ");
                }
                else
                {
                    Console.Write(new string(' ', 50));
                }
                Console.Write(side);
                Console.Write("\n");
            }
            Console.ResetColor();
        }
    }

    /// <summary>Renders a rotating wireframe cube into a flat char buffer.</summary>
    private static char[] RenderCube(int w, int h, double angle)
    {
        var buf = new char[w * h];
        for (int i = 0; i < buf.Length; i++) buf[i] = ' ';

        // 8 cube vertices
        var v = new double[8][];
        int idx = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    v[idx++] = new double[] { x, y, z };

        int[][] edges =
        {
            new[]{0,1}, new[]{0,2}, new[]{0,4}, new[]{1,3}, new[]{1,5}, new[]{2,3},
            new[]{2,6}, new[]{3,7}, new[]{4,5}, new[]{4,6}, new[]{5,7}, new[]{6,7}
        };

        double ax = angle, ay = angle * 0.6;
        double cx = Math.Cos(ax), sx = Math.Sin(ax);
        double cy = Math.Cos(ay), sy = Math.Sin(ay);

        double[] Rot(double[] p)
        {
            // rotate around X then Y
            double y1 = p[1] * cx - p[2] * sx;
            double z1 = p[1] * sx + p[2] * cx;
            double x2 = p[0] * cy + z1 * sy;
            double z2 = -p[0] * sy + z1 * cy;
            return new[] { x2, y1, z2 };
        }

        (int, int) Project(double[] p)
        {
            double dist = 4.0;
            double f = 2.4 / (dist - p[2]);
            int sxp = (int)(w / 2 + p[0] * f * w * 0.42);
            int syp = (int)(h / 2 - p[1] * f * h * 0.42);
            return (sxp, syp);
        }

        void Plot(int px, int py, char c)
        {
            if (px >= 0 && px < w && py >= 0 && py < h) buf[py * w + px] = c;
        }

        foreach (var e in edges)
        {
            var a = Rot(v[e[0]]);
            var b = Rot(v[e[1]]);
            const int steps = 24;
            for (int s = 0; s <= steps; s++)
            {
                double t = s / (double)steps;
                var p = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };
                var (px, py) = Project(p);
                Plot(px, py, p[2] > 0 ? '█' : '▓');
            }
        }
        // vertices
        foreach (var vert in v)
        {
            var (px, py) = Project(Rot(vert));
            Plot(px, py, '◆');
        }
        return buf;
    }

    // -------------------------------------------------------------- logging
    public static void Info(string msg) => Write("INFO", msg, ConsoleColor.Cyan);
    public static void Success(string msg) => Write("OK  ", msg, ConsoleColor.Green);
    public static void Warn(string msg) => Write("WARN", msg, ConsoleColor.Yellow);
    public static void Error(string msg) => Write("ERR ", msg, ConsoleColor.Red);
    public static void Debug(string msg) => Write("DBG ", msg, ConsoleColor.DarkGray);
    public static void Step(string msg) => Write("STEP", msg, ConsoleColor.Magenta);

    /// <summary>
    /// Logs a full exception report: type, message, complete stack trace and the
    /// chain of inner exceptions. Use this so a failure can be diagnosed at a
    /// glance instead of just seeing a one-line message.
    /// </summary>
    public static void Exception(string context, Exception ex)
    {
        AppendToFile($"{DateTime.Now:HH:mm:ss} [EXC ] {context}{Environment.NewLine}{ex}");
        lock (_lock)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\r{DateTime.Now:HH:mm:ss} [EXC ] {context}");

                var current = ex;
                int depth = 0;
                while (current != null)
                {
                    var prefix = depth == 0 ? "  →" : new string(' ', depth * 2) + "  ↳ inner:";
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{prefix} {current.GetType().FullName}: {current.Message}");
                    if (!string.IsNullOrWhiteSpace(current.StackTrace))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine(current.StackTrace);
                    }
                    current = current.InnerException;
                    depth++;
                }

                Console.ResetColor();
                WritePrompt();
            }
            catch { /* console may be gone */ }
        }
    }

    private static void Write(string level, string msg, ConsoleColor color)
    {
        AppendToFile($"{DateTime.Now:HH:mm:ss} [{level}] {msg}");
        lock (_lock)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"\r{DateTime.Now:HH:mm:ss} ");
                Console.ForegroundColor = color;
                Console.Write($"[{level}] ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(msg);
                Console.ResetColor();
                WritePrompt();
            }
            catch { /* console may be gone */ }
        }
    }

    private static void WritePrompt()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write("ely> ");
        Console.ForegroundColor = ConsoleColor.White;
    }

    // ------------------------------------------------------------- commands
    public static void RegisterCommand(string name, string description, Func<string[], string?> handler)
    {
        lock (_lock) _commands[name.ToLowerInvariant()] = (description, handler);
    }

    public static void StartCommandLoop()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                string? line;
                try { line = Console.ReadLine(); }
                catch { break; }
                if (line == null) { Thread.Sleep(200); continue; }

                line = line.Trim();
                if (line.Length == 0) { WritePrompt(); continue; }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0].ToLowerInvariant();
                var args = parts.Skip(1).ToArray();

                (string desc, Func<string[], string?> handler) entry;
                bool found;
                lock (_lock) found = _commands.TryGetValue(cmd, out entry);

                if (!found)
                {
                    Warn($"Commande inconnue : '{cmd}'. Tape 'help'.");
                    continue;
                }

                try
                {
                    var result = entry.handler(args);
                    if (!string.IsNullOrEmpty(result))
                    {
                        lock (_lock)
                        {
                            Console.ForegroundColor = ConsoleColor.Gray;
                            Console.WriteLine(result);
                            Console.ResetColor();
                            WritePrompt();
                        }
                    }
                    else WritePrompt();
                }
                catch (Exception ex)
                {
                    Error("Erreur commande : " + ex.Message);
                }
            }
        }) { IsBackground = true, Name = "ElyConsole" };
        t.Start();
    }

    private static void RegisterBuiltins()
    {
        RegisterCommand("help", "Affiche cette aide", _ =>
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("  Commandes disponibles :");
            lock (_lock)
                foreach (var kv in _commands.OrderBy(k => k.Key))
                    sb.AppendLine($"    {kv.Key,-14} {kv.Value.desc}");
            return sb.ToString();
        });
        RegisterCommand("clear", "Efface la console", _ => { Console.Clear(); return null; });
        RegisterCommand("banner", "Réaffiche la bannière ElyCast", _ =>
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            foreach (var l in Banner) Console.WriteLine(l);
            Console.ResetColor();
            return null;
        });
        RegisterCommand("cube", "Rejoue l'animation du cube 3D (3s)", _ =>
        {
            Console.Clear();
            var sw = Stopwatch.StartNew();
            double a = 0;
            Console.CursorVisible = false;
            while (sw.Elapsed.TotalSeconds < 3)
            {
                lock (_lock)
                {
                    Console.SetCursorPosition(0, 0);
                    var buf = RenderCube(44, 22, a);
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    for (int y = 0; y < 22; y++) Console.WriteLine("   " + new string(buf, y * 44, 44));
                    Console.ResetColor();
                }
                a += 0.13; Thread.Sleep(40);
            }
            return null;
        });
        RegisterCommand("version", "Affiche la version", _ => "ElyCast TV Player v2.1 — WPF / IVideoBackend / mpv+VLC");
        RegisterCommand("time", "Heure courante", _ => DateTime.Now.ToString("F"));
        RegisterCommand("console", "console hide | show | front", args =>
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "front";
            switch (mode)
            {
                case "hide": SetVisible(false); return "Console masquée.";
                case "show": case "front": SetVisible(true); return "Console affichée.";
                default: return "Usage : console hide | show | front";
            }
        });
        RegisterCommand("exit", "Ferme complètement ElyCast", _ =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                System.Windows.Application.Current.Shutdown());
            return null;
        });
    }

    // ----------------------------------------------------------- visibility
    public static void SetVisible(bool visible)
    {
        var h = GetConsoleWindow();
        if (h == IntPtr.Zero) return;
        ShowWindow(h, visible ? SW_RESTORE : SW_HIDE);
        if (visible) { ShowWindow(h, SW_SHOW); SetForegroundWindow(h); }
        _visible = visible;
    }

    public static void Toggle() => SetVisible(!_visible);

    // --------------------------------------------------------------- banner
    private static readonly string[] Banner =
    {
        @"   ███████╗██╗     ██╗   ██╗ ██████╗ █████╗ ███████╗████████╗",
        @"   ██╔════╝██║     ╚██╗ ██╔╝██╔════╝██╔══██╗██╔════╝╚══██╔══╝",
        @"   █████╗  ██║      ╚████╔╝ ██║     ███████║███████╗   ██║   ",
        @"   ██╔══╝  ██║       ╚██╔╝  ██║     ██╔══██║╚════██║   ██║   ",
        @"   ███████╗███████╗   ██║   ╚██████╗██║  ██║███████║   ██║   ",
        @"   ╚══════╝╚══════╝   ╚═╝    ╚═════╝╚═╝  ╚═╝╚══════╝   ╚═╝   ",
        @"            T V   P L A Y E R   ·   debug console            "
    };
}
