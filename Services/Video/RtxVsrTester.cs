using System.IO;
using System.Windows;

namespace Elysium_Cast_IPTV.Services.Video;

public enum VsrTestOutcome { Passed, Failed, Skipped }

public sealed record VsrTestResult(VsrTestOutcome Outcome, string Message, string HwdecUsed = "");

/// <summary>
/// Functional check of the real mpv + RTX VSR pipeline. A windowless mpv
/// cannot be used here: without a render context, hwdec silently falls back to
/// software decoding and produces false negatives. Instead the test spins up
/// the selected production <see cref="MpvHwndBackend"/> pipeline inside a
/// hidden off-screen window. RTX mode verifies NVDEC+d3d11vpp; ELYCORE verifies
/// the native post-Blt NVIDIA status (`vsrEffective`) through ABI v3.
/// Must run on the UI thread, and never while another backend owns libmpv
/// rendering - the wizard runs it before MainWindow exists.
/// </summary>
public static class RtxVsrTester
{
    // Ships with every Windows 10/11 install; also the playfile fallback.
    private const string SampleVideo =
        @"C:\Windows\SystemApps\Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy\media\oobe-intro.mp4";

    public static async Task<VsrTestResult> RunAsync(bool elyCore = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var native = MpvHwndBackend.LocateNative();
        if (string.IsNullOrWhiteSpace(native))
            return new VsrTestResult(VsrTestOutcome.Skipped, "libmpv missing: RTX VSR test cannot run.");
        if (!File.Exists(SampleVideo))
            return new VsrTestResult(VsrTestOutcome.Skipped, "No local test media found: RTX VSR test skipped.");

        var hardware = await Task.Run(HardwareProbe.Detect, ct);
        if (!HardwareProbe.SupportsRtxVsr(hardware))
            return new VsrTestResult(VsrTestOutcome.Failed,
                "GPU/driver does not meet the RTX VSR specification (RTX 20 series or newer and driver 531+ required).");

        Window? hidden = null;
        MpvHwndBackend? backend = null;
        try
        {
            progress?.Report("RTX VSR test: playing test media in a hidden mini-player (real pipeline)...");
            if (elyCore && (!ElyFlowRendererInterop.Available || ElyFlowRendererInterop.Preflight(out _) != 0))
                return new VsrTestResult(VsrTestOutcome.Failed, "Compatible ELYCORE native renderer/ABI unavailable.");
            backend = new MpvHwndBackend(native, rtxVsr: !elyCore, elyCore: elyCore);
            hidden = new Window
            {
                Width = elyCore ? 1280 : 320,
                Height = elyCore ? 720 : 180,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = backend.View
            };
            hidden.Show();

            backend.SetOption("mute", "yes");
            backend.Volume = 0;
            backend.Play(SampleVideo);
            if (elyCore) backend.ConfigureElyCoreVsr(true);

            // Up to 8 s for the decoder and the d3d11vpp chain to engage.
            var hwdec = "";
            for (var i = 0; i < 40; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(200, ct);
                hwdec = backend.GetOption("hwdec-current");
                if (elyCore && ElyFlowRendererInterop.GetState().VsrEffective != 0)
                    break;
                if (!elyCore && backend.PositionMs > 400 && hwdec.Contains("d3d11va", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            if (elyCore)
            {
                var state = ElyFlowRendererInterop.GetState();
                if (state.VsrEffective != 0)
                    return new VsrTestResult(VsrTestOutcome.Passed,
                        $"Native pipeline validated: RTX VSR effective at level {state.VsrLevel}, " +
                        $"{state.VsrInputWidth}×{state.VsrInputHeight} → {state.VsrContentWidth}×{state.VsrContentHeight}, DXGI {state.VsrInputFormat}.",
                        hwdec);
                if (backend.GetStats().SourceHeight >= state.Height && state.Height > 0)
                    return new VsrTestResult(VsrTestOutcome.Skipped,
                        "The Windows media is not smaller than the target: there is no upscale pass to confirm.", hwdec);
                return new VsrTestResult(VsrTestOutcome.Failed,
                    $"Native VSR not effective (requested={state.VsrRequested}, available={state.VsrAvailable}, " +
                    $"HRESULT=0x{unchecked((uint)state.LastVsrStatus):X8}, vendor=0x{state.AdapterVendorId:X4}).",
                    hwdec);
            }

            var vf = backend.GetOption("vf");
            var vsrChainAlive = vf.Contains("d3d11vpp", StringComparison.OrdinalIgnoreCase);
            if (hwdec.Contains("d3d11va", StringComparison.OrdinalIgnoreCase) && vsrChainAlive)
                return new VsrTestResult(VsrTestOutcome.Passed,
                    "Real pipeline validated: NVDEC (d3d11va) decode + d3d11vpp scaling-mode=nvidia active.",
                    hwdec);

            if (string.IsNullOrWhiteSpace(hwdec) || hwdec == "no")
                return new VsrTestResult(VsrTestOutcome.Failed,
                    "D3D11VA hardware decode did not start (software decode): RTX VSR unavailable.",
                    hwdec);

            return new VsrTestResult(VsrTestOutcome.Failed,
                $"Partial pipeline (hwdec={hwdec}, vf={(string.IsNullOrWhiteSpace(vf) ? "empty" : vf)}): RTX VSR not confirmed.",
                hwdec);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new VsrTestResult(VsrTestOutcome.Failed, "Test RTX VSR interrompu : " + ex.Message);
        }
        finally
        {
            // Same teardown order as RecreateVideoBackend: dispose the backend
            // while its surface is still parented, then drop the surface.
            try { backend?.Dispose(); } catch { }
            if (hidden != null)
            {
                try { hidden.Content = null; } catch { }
                try { hidden.Close(); } catch { }
            }
        }
    }
}
