using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Elysium_Cast_IPTV;

/// <summary>
/// Airspace-free video surface: libVLC renders frames into a managed buffer via
/// CPU callbacks, which we blit into a <see cref="WriteableBitmap"/> shown by a
/// normal WPF <see cref="Image"/>. Because there is no child HWND, WPF content
/// (OSD, panels, fullscreen) composes over the video correctly.
/// </summary>
public sealed class VlcVideo : Image
{
    private MediaPlayer? _mp;
    private WriteableBitmap? _bitmap;
    private IntPtr _buffer = IntPtr.Zero;
    private int _bufferSize;
    private uint _width, _height, _pitch, _lines;
    private readonly object _sync = new();
    private int _displayPending;

    // Keep the delegates alive for the lifetime of the control (GC guard).
    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    public VlcVideo()
    {
        Stretch = Stretch.Uniform;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
    }

    public void Attach(MediaPlayer mp)
    {
        _mp = mp;
        _formatCb = OnFormat; _cleanupCb = OnCleanup;
        _lockCb = OnLock; _unlockCb = OnUnlock; _displayCb = OnDisplay;
        mp.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        mp.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
                          ref uint pitches, ref uint lines)
    {
        SetChroma(chroma, "RV32");           // 32-bit BGRX, matches WPF Bgr32
        if (width == 0 || height == 0 || width > int.MaxValue / 4)
            return 0;

        _width = width; _height = height;
        _pitch = width * 4; _lines = height;
        pitches = _pitch; lines = _lines;

        lock (_sync)
        {
            var required = (long)_pitch * _lines;
            if (required > int.MaxValue) return 0;
            _bufferSize = (int)required;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }

        int w = (int)_width, h = (int)_height;
        void CreateBitmap()
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            Source = _bitmap;
        }
        // libVLC calls this from its decoder thread. Synchronously waiting on the
        // UI dispatcher can deadlock while the UI is stopping/disposal is in progress.
        if (Dispatcher.CheckAccess()) CreateBitmap();
        else _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(CreateBitmap));
        return 1; // one buffer
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_sync)
        {
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_sync);
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(_sync);

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        // Keep one pending UI blit at most. A request for every decoded frame made
        // the Dispatcher queue grow without limit under a slow UI or a 4K stream.
        if (Interlocked.Exchange(ref _displayPending, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            try
            {
                var bmp = _bitmap;
                if (bmp == null) return;
                lock (_sync)
                {
                    if (_buffer == IntPtr.Zero) return;
                    if (bmp.PixelWidth != (int)_width || bmp.PixelHeight != (int)_height) return;
                    try
                    {
                        bmp.Lock();
                        CopyMemory(bmp.BackBuffer, _buffer, (uint)_bufferSize);
                        bmp.AddDirtyRect(new Int32Rect(0, 0, (int)_width, (int)_height));
                    }
                    finally { bmp.Unlock(); }
                }
            }
            finally { Volatile.Write(ref _displayPending, 0); }
        }));
    }

    /// <summary>Clears the surface to black (e.g. on stop).</summary>
    public void Clear()
    {
        void ClearSource() => Source = null;
        if (Dispatcher.CheckAccess()) ClearSource();
        else _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ClearSource));
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

    private static void SetChroma(IntPtr chromaPtr, string chroma)
    {
        for (int i = 0; i < 4; i++)
            Marshal.WriteByte(chromaPtr, i, (byte)(i < chroma.Length ? chroma[i] : 0));
    }
}
