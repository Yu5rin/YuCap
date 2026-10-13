using System;
using System.Runtime.InteropServices;

namespace YuCap;

/// <summary>
/// The kernel-mode vertical-blank wait (<c>D3DKMTWaitForVerticalBlankEvent</c>) of one GDI display
/// (<c>\\.\DISPLAY2</c>). Unlike <c>IDXGIOutput.WaitForVBlank</c>, which some drivers answer from the
/// primary display's vblank on a mixed-refresh multi-monitor setup, this waits on the video present source
/// that really drives the named display. One instance per display; <see cref="Dispose"/> closes the adapter.
/// Only <c>--vblank-probe</c> uses it, as a cross-check: on the NVIDIA setup it was written for (144 Hz TV beside a 59.94 Hz
/// monitor) this call reports 60 Hz for the TV while the panel really scans out at 144 Hz, so it is NOT used for pacing.
/// </summary>
internal sealed class KmtVBlank : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAdapterFromGdiDisplayName
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public uint hAdapter;
        public long AdapterLuid;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaitForVerticalBlankEvent { public uint hAdapter, hDevice, VidPnSourceId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint hAdapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromGdiDisplayName(ref OpenAdapterFromGdiDisplayName d);
    [DllImport("gdi32.dll")] private static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitForVerticalBlankEvent d);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter d);

    private uint _adapter, _source;

    public string DeviceName { get; }
    public int LastStatus { get; private set; }

    private KmtVBlank(string deviceName) { DeviceName = deviceName; }

    /// <summary>Opens the adapter behind the display; null (with the NTSTATUS in <paramref name="status"/>) on failure.</summary>
    public static KmtVBlank? Open(string gdiDeviceName, out int status)
    {
        status = 0;
        try
        {
            var d = new OpenAdapterFromGdiDisplayName { DeviceName = gdiDeviceName };
            status = D3DKMTOpenAdapterFromGdiDisplayName(ref d);
            if (status != 0 || d.hAdapter == 0) return null;
            return new KmtVBlank(gdiDeviceName) { _adapter = d.hAdapter, _source = d.VidPnSourceId };
        }
        catch (Exception) { status = unchecked((int)0xC0000001); return null; }
    }

    /// <summary>Blocks until the next vblank. False when the kernel call failed (status in <see cref="LastStatus"/>).</summary>
    public bool Wait()
    {
        if (_adapter == 0) return false;
        var w = new WaitForVerticalBlankEvent { hAdapter = _adapter, hDevice = 0, VidPnSourceId = _source };
        LastStatus = D3DKMTWaitForVerticalBlankEvent(ref w);
        return LastStatus == 0;
    }

    public void Dispose()
    {
        uint a = _adapter;
        _adapter = 0;
        if (a == 0) return;
        try { var c = new CloseAdapter { hAdapter = a }; D3DKMTCloseAdapter(ref c); } catch { }
    }
}
