using System.Runtime.InteropServices;
using System.IO;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Audio;

namespace Elysium_Cast_IPTV.Services;

/// <summary>
/// Managed control plane for the native Windows SMTC bridge. Keeping the
/// WinRT ABI native avoids coupling the WPF application to a WinRT projection.
/// </summary>
public sealed class WindowsMediaTransportService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NativeCommand(int command, nint context);
    private delegate nint CreateDelegate(nint hwnd, NativeCommand callback, nint context);
    private delegate void DestroyDelegate(nint instance);
    private delegate int SetMediaDelegate(nint instance, [MarshalAs(UnmanagedType.LPWStr)] string title,
        [MarshalAs(UnmanagedType.LPWStr)] string artist, [MarshalAs(UnmanagedType.LPWStr)] string album,
        [MarshalAs(UnmanagedType.LPWStr)] string artworkUri);
    private delegate void SetStateDelegate(nint instance, int hasMedia, int playing);

    private readonly Action _play;
    private readonly Action _pause;
    private readonly Action _next;
    private readonly Action _previous;
    private readonly NativeCommand _nativeCommand;
    private nint _library;
    private nint _instance;
    private DestroyDelegate? _destroy;
    private SetMediaDelegate? _setMedia;
    private SetStateDelegate? _setState;

    public WindowsMediaTransportService(Action play, Action pause, Action next, Action previous)
    {
        _play = play;
        _pause = pause;
        _next = next;
        _previous = previous;
        _nativeCommand = OnNativeCommand;
    }

    public void Initialize(nint hwnd)
    {
        if (_instance != 0 || hwnd == 0) return;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ElyFlow.Native.dll");
            _library = NativeLibrary.Load(path);
            var create = Load<CreateDelegate>("ElyMediaTransport_Create");
            _destroy = Load<DestroyDelegate>("ElyMediaTransport_Destroy");
            _setMedia = Load<SetMediaDelegate>("ElyMediaTransport_SetMedia");
            _setState = Load<SetStateDelegate>("ElyMediaTransport_SetState");
            _instance = create(hwnd, _nativeCommand, 0);
            if (_instance == 0) throw new InvalidOperationException("Windows rejected the SMTC session.");
            DebugConsole.Success("Windows media controls initialized.");
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Windows media controls unavailable: " + ex.Message);
            Dispose();
        }
    }

    public void SetAudio(PlayItem item, AudioMetadata metadata)
    {
        if (_instance == 0 || _setMedia == null) return;
        var artworkUri = CacheArtwork(metadata) ?? string.Empty;
        var hr = _setMedia(_instance, metadata.Title, metadata.Artist ?? string.Empty,
            metadata.Album ?? string.Empty, artworkUri);
        if (hr < 0) DebugConsole.Warn($"Windows metadata rejected (HRESULT 0x{hr:X8}).");
    }

    public void Clear() => SetState(hasMedia: false, playing: false);

    public void SetState(bool hasMedia, bool playing) =>
        _setState?.Invoke(_instance, hasMedia ? 1 : 0, playing ? 1 : 0);

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static string? CacheArtwork(AudioMetadata metadata)
    {
        try
        {
            var bytes = metadata.CoverBytes;
            var extension = metadata.CoverMimeType?.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/bmp" => ".bmp",
                _ => ".jpg"
            };
            string source;
            if (bytes is { Length: > 0 })
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElyCast", "cache");
                Directory.CreateDirectory(folder);
                source = Path.Combine(folder, "smtc-cover" + extension);
                File.WriteAllBytes(source, bytes);
            }
            else
            {
                source = Path.Combine(AppContext.BaseDirectory, "ElyCastLogo.png");
                if (!File.Exists(source)) return null;
            }
            return source;
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Windows artwork could not be prepared: " + ex.Message);
            return null;
        }
    }

    private void OnNativeCommand(int command, nint _)
    {
        switch (command)
        {
            case 0: _play(); break;
            case 1: _pause(); break;
            case 6: _next(); break;
            case 7: _previous(); break;
        }
    }

    public void Dispose()
    {
        if (_instance != 0) _destroy?.Invoke(_instance);
        _instance = 0;
        _destroy = null;
        _setMedia = null;
        _setState = null;
        if (_library != 0) NativeLibrary.Free(_library);
        _library = 0;
    }
}
