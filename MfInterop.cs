using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace YuCap;

/// <summary>
/// The Media Foundation Capture Engine COM bits that Vortice does not expose in
/// a usable form (the engine's Initialize takes an internal callback type).
/// Everything else — media types, sources, enumeration — comes from Vortice.
/// </summary>
internal static class Mf
{
    public static readonly Guid ClsidCaptureEngine = new("efce38d3-8914-4674-a7df-ae1b3d654b8a");
    public static readonly Guid IidClassFactory = new("8f02d140-56fc-4302-a705-3a97c78be779");
    public static readonly Guid IidCaptureEngine = new("a6bba433-176b-48b2-b375-53aa03473207");

    // Capture engine event types.
    public static readonly Guid EventInitialized = new("219992bc-cf92-4531-a1ae-96e1e886c8f1");
    public static readonly Guid EventPreviewStarted = new("a416df21-f9d3-4a74-991b-b817298952c4");
    public static readonly Guid EventError = new("46b89fc6-33cc-4399-9dad-784de77d587c");

    public static readonly Guid EventPhotoTaken = new("3c50c445-7304-48eb-8656-51c6b7a4a1a1");

    public const int SinkTypeRecord = 0;
    public const int SinkTypePreview = 1;
    public const int SinkTypePhoto = 2;
    // MF_CAPTURE_ENGINE_PREFERRED_SOURCE_STREAM_FOR_VIDEO_PREVIEW
    public const int PreferredPreviewStream = unchecked((int)0xFFFFFFFA);
    // MF_CAPTURE_ENGINE_PREFERRED_SOURCE_STREAM_FOR_PHOTO. Capture cards
    // generally have no dedicated still-image pin, so the photo sink has to be
    // fed from a video stream instead — see the candidate list in VideoEngine.
    public const int PreferredPhotoStream = unchecked((int)0xFFFFFFFB);
    // MF_CAPTURE_ENGINE_FIRST_SOURCE_VIDEO_STREAM
    public const int FirstVideoStream = unchecked((int)0xFFFFFFFC);

    // Uncompressed 32-bit RGB — what the photo sink is asked to produce so the
    // bytes can be turned into a Bitmap without decoding anything.
    public static readonly Guid MFMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormatRGB32 = new("00000016-0000-0010-8000-00AA00389B71");

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context,
        ref Guid iid, out IntPtr ppv);
}

[ComImport, Guid("a6bba433-176b-48b2-b375-53aa03473207"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCaptureEngine
{
    [PreserveSig] int Initialize(IMFCaptureEngineOnEventCallback callback,
        IntPtr attributes, IntPtr audioSource, IntPtr videoSource);
    [PreserveSig] int StartPreview();
    [PreserveSig] int StopPreview();
    [PreserveSig] int StartRecord();
    [PreserveSig] int StopRecord(int finalize, int flush);
    [PreserveSig] int TakePhoto();
    [PreserveSig] int GetSink(int sinkType, out IntPtr sink);
    [PreserveSig] int GetSource(out IntPtr source);
}

[ComImport, Guid("aeda51c0-9025-4983-9012-de597b88b089"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCaptureEngineOnEventCallback
{
    [PreserveSig] int OnEvent(IntPtr mediaEvent);
}

[ComImport, Guid("77346cfd-5b49-4d73-ace0-5b52a859f2e0"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCapturePreviewSink
{
    [PreserveSig] int GetOutputMediaType(int stream, out IntPtr mediaType);
    [PreserveSig] int GetService(int stream, ref Guid service, ref Guid riid, out IntPtr unknown);
    [PreserveSig] int AddStream(int sourceStream, IntPtr mediaType, IntPtr attributes, out int sinkStream);
    [PreserveSig] int Prepare();
    [PreserveSig] int RemoveAllStreams();
    [PreserveSig] int SetRenderHandle(IntPtr handle);
    [PreserveSig] int SetRenderSurface(IntPtr surface);
    [PreserveSig] int UpdateVideo(
        [In, MarshalAs(UnmanagedType.LPStruct)] MFVideoNormalizedRect? source,
        [In] MfRect? dest,
        [In] int[]? borderColor);
    // vtable slot only — NEVER call this: setting a sample callback on the render
    // stream diverts frames from the window and the preview goes blank (white).
    [PreserveSig] int SetSampleCallback_DoNotUse(int streamSinkIndex, IntPtr callback);
    [PreserveSig] int GetMirrorState(out int mirrored);
    [PreserveSig] int SetMirrorState(int mirrored);
    [PreserveSig] int GetRotation(int streamIndex, out int rotationDegrees);
    [PreserveSig] int SetRotation(int streamIndex, int rotationDegrees);
    [PreserveSig] int SetCustomSink(IntPtr mediaSink);
}

[StructLayout(LayoutKind.Sequential)]
internal sealed class MfRect
{
    public int left, top, right, bottom;
    public MfRect() { }
    public MfRect(int l, int t, int r, int b) { left = l; top = t; right = r; bottom = b; }
}

/// <summary>
/// MFVideoNormalizedRect (0..1 coordinates) — the source-crop argument of
/// UpdateVideo. Declared here rather than taken from a third-party interop
/// library because YuCap always passes null (no cropping); only the type is
/// needed to describe the signature.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class MFVideoNormalizedRect
{
    public float left, top, right, bottom;
}

/// <summary>
/// The photo sink, used for snapshots at the source's own resolution. The
/// preview-window copy that snapshots used before is limited to the on-screen
/// size and picks up anything overlapping the window; this path takes the frame
/// from the capture pipeline itself.
/// </summary>
[ComImport, Guid("d2d43cc8-48bb-4aa7-95db-10c06977e777"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCapturePhotoSink
{
    // --- IMFCaptureSink ---
    [PreserveSig] int GetOutputMediaType(int stream, out IntPtr mediaType);
    [PreserveSig] int GetService(int stream, ref Guid service, ref Guid riid, out IntPtr unknown);
    [PreserveSig] int AddStream(int sourceStream, IntPtr mediaType, IntPtr attributes, out int sinkStream);
    [PreserveSig] int Prepare();
    [PreserveSig] int RemoveAllStreams();
    // --- IMFCapturePhotoSink ---
    [PreserveSig] int SetOutputFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    [PreserveSig] int SetSampleCallback(IMFCaptureEngineOnSampleCallback callback);
    [PreserveSig] int SetOutputByteStream(IntPtr byteStream);
}

/// <summary>Receives the photo frame. Unlike the preview stream — where a
/// sample callback steals frames from the renderer — this is the photo sink's
/// intended delivery mechanism.</summary>
[ComImport, Guid("52150b82-ab39-4467-980f-e48bf0822ecd"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCaptureEngineOnSampleCallback
{
    [PreserveSig] int OnSample(IntPtr sample);
}

/// <summary>
/// Managed implementation of the capture engine event callback. Signals the
/// initialized/preview-started events so the (synchronous) startup code can wait.
/// </summary>
[ClassInterface(ClassInterfaceType.None)]
internal sealed class CaptureEventCallback : IMFCaptureEngineOnEventCallback
{
    public readonly ManualResetEventSlim Initialized = new(false);
    public readonly ManualResetEventSlim PreviewStarted = new(false);
    public readonly ManualResetEventSlim PhotoTaken = new(false);
    public volatile int LastHr;
    public volatile int LastPhotoHr;

    public int OnEvent(IntPtr mediaEvent)
    {
        try
        {
            Marshal.AddRef(mediaEvent); // borrow → own for the Vortice wrapper
            using var ev = (Vortice.MediaFoundation.IMFMediaEvent)mediaEvent;
            Guid ext = ev.ExtendedType;
            int hr = ev.Status.Code;
            if (hr < 0) LastHr = hr;

            if (ext == Mf.EventInitialized) Initialized.Set();
            else if (ext == Mf.EventPreviewStarted) PreviewStarted.Set();
            else if (ext == Mf.EventPhotoTaken)
            {
                LastPhotoHr = hr;
                PhotoTaken.Set();
            }
            else if (ext == Mf.EventError)
            {
                if (LastHr == 0) LastHr = -1;
                Initialized.Set();
                PreviewStarted.Set();
                PhotoTaken.Set();
            }
        }
        catch { /* never throw back into COM */ }
        return 0;
    }
}
