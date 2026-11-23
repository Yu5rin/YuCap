using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace YuCap;

/// <summary>
/// Finds the audio capture endpoint that belongs to a video device (a capture card's HDMI audio).
/// Primary key: the PnP container id, which Windows gives to every function of one physical device.
/// Fallback: the endpoint's audio function and the video interface share one parent devnode (the composite USB device).
/// Every lookup is best-effort: any failure yields null and never throws. Results are cached per id.
/// </summary>
public static class DevicePairing
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(Guid g, uint p) { fmtid = g; pid = p; }
    }

    private static readonly DEVPROPKEY InstanceIdKey = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);   // DEVPKEY_Device_InstanceId
    private static readonly DEVPROPKEY ContainerIdKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);   // DEVPKEY_Device_ContainerId
    private static readonly DEVPROPKEY ParentKey = new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);        // DEVPKEY_Device_Parent
    private static readonly Guid RootContainer = new("00000000-0000-0000-ffff-ffffffffffff");   // virtual devices report this

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_PropertyW(string iface, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);

    private sealed record Key(Guid? Container, string? Parent);

    private static readonly ConcurrentDictionary<string, Key> VideoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Key> AudioCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Container id of a video device (MF symbolic link), or null (unknown / virtual device).</summary>
    public static Guid? ContainerOfVideo(string? link) => VideoKey(link).Container;

    /// <summary>Parent devnode id of a video device (MF symbolic link), or null.</summary>
    public static string? ParentOfVideo(string? link) => VideoKey(link).Parent;

    /// <summary>Container id of an audio capture endpoint, or null.</summary>
    public static Guid? ContainerOfAudio(string? endpointId) => AudioKey(endpointId).Container;

    /// <summary>The audio endpoint that belongs to the video device, or null when it has none of its own.</summary>
    public static AudioDeviceInfo? FindAudioFor(string? videoLink, IReadOnlyList<AudioDeviceInfo> audios)
    {
        try
        {
            if (string.IsNullOrEmpty(videoLink) || audios.Count == 0) return null;
            Key v = VideoKey(videoLink);
            if (v.Container is Guid c)
            {
                var hit = audios.FirstOrDefault(a => AudioKey(a.Id).Container == c);
                if (hit != null) return hit;
            }
            if (!string.IsNullOrEmpty(v.Parent))
                return audios.FirstOrDefault(a => string.Equals(AudioKey(a.Id).Parent, v.Parent, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* best effort */ }
        return null;
    }

    private static Key VideoKey(string? link)
    {
        if (string.IsNullOrEmpty(link)) return new Key(null, null);
        if (VideoCache.TryGetValue(link, out Key? cached)) return cached;
        Key key = new(null, null);
        try
        {
            string? inst = InterfaceString(link, InstanceIdKey);
            if (inst != null) key = new Key(NodeContainer(inst), NodeParent(inst));
        }
        catch { /* ignore */ }
        if (key.Container != null || key.Parent != null) VideoCache[link] = key;
        return key;
    }

    private static Key AudioKey(string? endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return new Key(null, null);
        if (AudioCache.TryGetValue(endpointId, out Key? cached)) return cached;
        Guid? container = null;
        string? parent = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            using MMDevice d = en.GetDevice(endpointId);
            try
            {
                object? v = d.Properties[new PropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2)]?.Value;
                if (v is Guid g) container = Normalize(g);
                else if (v is string s && Guid.TryParse(s, out Guid g2)) container = Normalize(g2);
            }
            catch { /* ignore */ }
            try
            {
                // PKEY_Device_InstanceId of the audio function, e.g. "{1}.USB\VID_...&MI_02\..." -> strip the "{n}." prefix.
                string? fn = d.Properties[new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2)]?.Value as string;
                if (fn != null && fn.StartsWith('{') && fn.IndexOf('}') is int i && i >= 0 && i + 2 <= fn.Length) fn = fn[(i + 2)..];
                if (!string.IsNullOrEmpty(fn)) parent = NodeParent(fn);
            }
            catch { /* ignore */ }
        }
        catch { /* ignore */ }
        Key key = new(container, parent);
        if (key.Container != null || key.Parent != null) AudioCache[endpointId] = key;
        return key;
    }

    private static Guid? Normalize(Guid g) => g == Guid.Empty || g == RootContainer ? null : g;

    private static string? InterfaceString(string link, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(link, ref key, out _, null, ref size, 0);
        if (size == 0 || size > 4096) return null;
        var buf = new byte[size];
        return CM_Get_Device_Interface_PropertyW(link, ref key, out _, buf, ref size, 0) == 0
            ? Encoding.Unicode.GetString(buf).TrimEnd('\0') : null;
    }

    private static byte[]? NodeProp(string instance, DEVPROPKEY key)
    {
        if (CM_Locate_DevNodeW(out uint dn, instance, 0) != 0) return null;
        uint size = 0;
        CM_Get_DevNode_PropertyW(dn, ref key, out _, null, ref size, 0);
        if (size == 0 || size > 4096) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(dn, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    private static Guid? NodeContainer(string instance)
    {
        byte[]? b = NodeProp(instance, ContainerIdKey);
        return b != null && b.Length == 16 ? Normalize(new Guid(b)) : null;
    }

    private static string? NodeParent(string instance)
    {
        // Software-root devices (virtual cameras, virtual microphones) all hang under the shared root node: no real sibling relation.
        if (instance.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return null;
        byte[]? b = NodeProp(instance, ParentKey);
        if (b == null) return null;
        string parent = Encoding.Unicode.GetString(b).TrimEnd('\0');
        return parent.Length == 0 || parent.Equals(@"HTREE\ROOT\0", StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ? null : parent;
    }
}
