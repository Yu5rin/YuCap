using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace YuCap;

/// <summary>
/// A single audio input device (friendly name + endpoint id).
/// </summary>
public sealed record AudioDeviceInfo(string Name, string Id);

/// <summary>
/// Soft clipper placed after the volume stage. Amplifying past 100% would
/// otherwise hard-clip — samples chopped flat at ±1.0, which is the harsh
/// crackle you hear when a player is turned up too far. Below the threshold
/// this is transparent; above it the curve bends smoothly toward ±1.0, so loud
/// passages saturate gently instead of tearing. This is what makes gain above
/// 200% usable rather than just louder distortion.
/// </summary>
internal sealed class SoftLimitSampleProvider : ISampleProvider
{
    private const float Threshold = 0.75f;   // transparent below this
    private const float Range = 1f - Threshold;

    private readonly ISampleProvider _source;
    public SoftLimitSampleProvider(ISampleProvider source) => _source = source;
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>True once the limiter has had to act — the UI uses it to show
    /// that the level is past the point of clean amplification.</summary>
    public volatile bool Limiting;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        bool limited = false;
        for (int i = 0; i < read; i++)
        {
            float x = buffer[offset + i];
            float a = Math.Abs(x);
            if (a <= Threshold) continue;
            limited = true;
            float shaped = Threshold + Range * MathF.Tanh((a - Threshold) / Range);
            buffer[offset + i] = x < 0 ? -shaped : shaped;
        }
        Limiting = limited;
        return read;
    }
}

/// <summary>An active audio render (output) endpoint.</summary>
public sealed record AudioOutputInfo(string Name, string Id);

/// <summary>
/// WASAPI passthrough: captures from a capture endpoint (the capture card's
/// line-in) and plays it out of the default render device, with software volume.
/// The capture stream is resampled/channel-matched to the render mix format so
/// shared-mode initialization succeeds across device combinations.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private WasapiCapture? _capture;
    private SharedRender? _output;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volumeProvider;
    private SoftLimitSampleProvider? _limiter;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;

    private float _volume = 1.0f; // 0.0 .. 2.0
    private bool _muted;
    private string? _renderDeviceId;
    private string? _requestedOutputId;   // null = follow the Windows default
    private long _lastPacketTick;
    private double _packetMs = 10;
    private double _latencyEma;
    private double _backlogEma;
    private long _latencyTick;
    private volatile bool _gotData;

    /// <summary>Audio kept in YuCap's own queue as a safety margin (ms): the cushion that absorbs timing jitter
    /// between the capture card and the output. It is the only delay YuCap adds on purpose - smaller is less delay,
    /// too small cuts the sound off. The output device's own queue is small and fixed (see SharedRender).</summary>
    public int BufferMilliseconds { get; set; } = DefaultBufferMs;

    /// <summary>Default for <see cref="BufferMilliseconds"/>.</summary>
    public const int DefaultBufferMs = 40;
    /// <summary>The default before v1.9 (the whole output queue was this long); migrated to <see cref="DefaultBufferMs"/> once.</summary>
    public const int LegacyDefaultMs = 120;
    /// <summary>The value many users had after the old default felt too short (also migrated once).</summary>
    public const int LegacyAltMs = 200;
    public const int MaxBufferMs = 500;

    /// <summary>Capture-side buffer length (ms) and whether to wake per engine period (event) instead of polling. Probe-tunable.</summary>
    public static int CaptureBufferMs = 20;
    public static bool CaptureEventSync = true;
    /// <summary>Audio kept queued in the output device, in engine periods.</summary>
    public static double RenderQueuePeriods = 2.0;

    /// <summary>Packet interval of the capture card as seen by YuCap (ms, smoothed).</summary>
    public double PacketMs => _packetMs;
    /// <summary>Smoothed audio waiting in YuCap's own queue when a packet arrives (ms, includes the A/V-sync delay).</summary>
    public double BacklogMs => Volatile.Read(ref _backlogEma);
    /// <summary>Smoothed audio waiting in the output device (queue + stream latency, ms).</summary>
    public double RenderQueueMs => _output == null ? 0 : _output.PaddingMs + _output.StreamLatencyMs;
    /// <summary>How many times the sound ran dry (our queue or the device) since Start().</summary>
    public long Underruns => _output == null ? 0 : _output.BufferStarved + _output.DeviceStarved;
    public double OutputPeriodMs => _output?.PeriodMs ?? 0;
    public bool LowLatencyOutput => _output?.LowLatencyApi == true;

    /// <summary>Largest value <see cref="ExtraDelayMs"/> accepts.</summary>
    public const int MaxExtraDelayMs = 300;

    private int _extraDelayMs;

    /// <summary>
    /// Deliberate extra audio delay (A/V sync: "play the sound later"), 0..<see cref="MaxExtraDelayMs"/>.
    /// It adds to the latency target that OnDataAvailable actively holds, and unlike the
    /// base buffer it has to be CREATED: the backlog only ever shrinks on its own, so a
    /// bigger delay is made by queueing silence and a smaller one by discarding the surplus.
    /// Takes effect immediately, without restarting the device, and survives Start()/Stop().
    /// </summary>
    public int ExtraDelayMs
    {
        get => Volatile.Read(ref _extraDelayMs);
        set
        {
            int v = Math.Clamp(value, 0, MaxExtraDelayMs);
            int old = Interlocked.Exchange(ref _extraDelayMs, v);
            if (v != old) ShiftBacklog(v - old);
        }
    }

    /// <summary>Append (positive) or discard from the front (negative) <paramref name="deltaMs"/>
    /// of the queued audio. BufferedWaveProvider serialises this with the render thread's reads.</summary>
    private void ShiftBacklog(int deltaMs)
    {
        var buffer = _buffer;
        if (buffer == null || deltaMs == 0) return;
        WaveFormat wf = buffer.WaveFormat;
        int bytes = (int)(Math.Abs(deltaMs) * (long)wf.AverageBytesPerSecond / 1000);
        bytes -= bytes % wf.BlockAlign;
        if (bytes <= 0) return;
        try
        {
            if (deltaMs > 0) buffer.AddSamples(new byte[bytes], 0, bytes);
            else buffer.Read(new byte[Math.Min(bytes, buffer.BufferedBytes)], 0, Math.Min(bytes, buffer.BufferedBytes));
        }
        catch { /* the stream is being torn down */ }
    }

    /// <summary>Friendly name of the render device that is actually playing (null when stopped).</summary>
    public string? RenderDeviceName { get; private set; }
    /// <summary>True when playback follows the Windows default output (no device chosen, or the chosen one is missing).</summary>
    public bool FollowingDefault => _requestedOutputId == null || RenderFellBack;
    /// <summary>A specific output was requested but it is not available, so the default is playing instead.</summary>
    public bool RenderFellBack { get; private set; }

    /// <summary>Smoothed time from the capture card handing audio to YuCap until it is played (ms):
    /// capture packet + our backlog (incl. the extra delay) + what is queued in the output device. 0 when unknown.
    /// Does not include the capture card's or the speaker's own delay. Safe to read from any thread.</summary>
    public double LatencyMs
    {
        get
        {
            if (_output == null) return 0;
            long t = Volatile.Read(ref _latencyTick);
            if (t == 0 || Stopwatch.GetTimestamp() - t > 2 * Stopwatch.Frequency) return 0;
            return Volatile.Read(ref _latencyEma);
        }
    }

    public string? CurrentDeviceName { get; private set; }
    public string? CurrentDeviceId { get; private set; }
    public bool IsRunning => _output != null;

    /// <summary>Set when playback died (e.g. the render device was removed);
    /// the owner should restart passthrough on the next device change.</summary>
    public volatile bool IsFaulted;

    /// <summary>Currently buffered audio in milliseconds (the audible delay floor).</summary>
    public double BufferedMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_volumeProvider != null)
                _volumeProvider.Volume = _muted ? 0f : _volume;
        }
    }

    /// <summary>True if the system default render device differs from the one we
    /// opened — playback should be restarted to follow the new default output.</summary>
    public bool DefaultRenderChanged()
    {
        if (_renderDeviceId == null) return false;
        if (!FollowingDefault) return false;   // a chosen output does not care what Windows' default is
        try
        {
            using var e = new MMDeviceEnumerator();
            using MMDevice d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID != _renderDeviceId;
        }
        catch { return false; }
    }

    /// <summary>Enumerate active audio capture endpoints.</summary>
    public static List<AudioDeviceInfo> EnumerateDevices()
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(d.FriendlyName, d.ID));
            d.Dispose();
        }
        return result;
    }

    /// <summary>Enumerate active audio render (output) endpoints.</summary>
    public static List<AudioOutputInfo> EnumerateRenderDevices()
    {
        var result = new List<AudioOutputInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new AudioOutputInfo(d.FriendlyName, d.ID));
            d.Dispose();
        }
        return result;
    }

    /// <summary>Name of the current Windows default output, or null if there is none.</summary>
    public static string? DefaultRenderName()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using MMDevice d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.FriendlyName;
        }
        catch { return null; }
    }

    /// <summary>True when the render endpoint exists and is active.</summary>
    public static bool RenderDeviceActive(string id)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using MMDevice d = e.GetDevice(id);
            return d.State == DeviceState.Active;
        }
        catch { return false; }
    }

    public static AudioDeviceInfo? PickPreferred(IReadOnlyList<AudioDeviceInfo> devices, string keyword)
    {
        if (devices.Count == 0) return null;
        return devices.FirstOrDefault(d =>
                   d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
               ?? devices[0];
    }

    /// <summary>
    /// Start passthrough from the given capture device to the default render device.
    /// Throws on failure; caller catches and reports.
    /// </summary>
    public void Start(AudioDeviceInfo info, string? outputId = null)
    {
        Stop();

        var enumerator = new MMDeviceEnumerator();
        MMDevice captureDevice = enumerator.GetDevice(info.Id);
        MMDevice? renderDevice = null;
        bool fellBack = false;
        if (!string.IsNullOrEmpty(outputId))
        {
            try
            {
                MMDevice d = enumerator.GetDevice(outputId);
                if (d.State == DeviceState.Active) renderDevice = d; else d.Dispose();
            }
            catch { /* removed or unknown id */ }
            if (renderDevice == null)
            {
                fellBack = true;
                Log.Info("Audio: chosen output is not available - using the Windows default");
            }
        }
        renderDevice ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        enumerator.Dispose();
        Log.Info($"Audio: output = {renderDevice.FriendlyName} ({(string.IsNullOrEmpty(outputId) || fellBack ? "Windows default" : "chosen")})");

        var capture = new WasapiCapture(captureDevice, CaptureEventSync, CaptureBufferMs);
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            // Room for the target plus the full A/V-sync delay, with a generous margin.
            BufferDuration = TimeSpan.FromMilliseconds((Math.Max(MinBufferMs, BufferMilliseconds) + MaxExtraDelayMs) * 4),
            DiscardOnBufferOverflow = true,
        };

        SharedRender output;
        try { output = new SharedRender(renderDevice.ID); }
        catch { capture.Dispose(); renderDevice.Dispose(); captureDevice.Dispose(); throw; }

        VolumeSampleProvider volumeProvider;
        SoftLimitSampleProvider limiter;
        try
        {
            // Build the sample chain and match it to the render mix format.
            ISampleProvider sample = buffer.ToSampleProvider();
            int targetRate = output.Rate;
            int targetChannels = output.Channels;

            if (sample.WaveFormat.Channels == 1 && targetChannels >= 2)
                sample = new MonoToStereoSampleProvider(sample);
            else if (sample.WaveFormat.Channels > 2 && targetChannels == 2)
                sample = new MultiplexingSampleProvider(new[] { sample }, 2);

            if (sample.WaveFormat.SampleRate != targetRate)
                sample = new WdlResamplingSampleProvider(sample, targetRate);

            volumeProvider = new VolumeSampleProvider(sample) { Volume = _muted ? 0f : _volume };
            // Gain first, then the limiter catches whatever the gain pushed over.
            limiter = new SoftLimitSampleProvider(volumeProvider);

            output.Init(RenderQueuePeriods);
        }
        catch { output.Dispose(); capture.Dispose(); renderDevice.Dispose(); captureDevice.Dispose(); throw; }

        double srcPerOut = (double)capture.WaveFormat.SampleRate / output.Rate;
        int srcBlock = capture.WaveFormat.BlockAlign;
        output.IsStarving = frames => _gotData && buffer.BufferedBytes < (int)(frames * srcPerOut) * srcBlock;
        output.Failed += _ => IsFaulted = true;
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) IsFaulted = true; };
        capture.DataAvailable += OnDataAvailable;

        _capture = capture;
        _buffer = buffer;
        _volumeProvider = volumeProvider;
        _limiter = limiter;
        _requestedOutputId = string.IsNullOrEmpty(outputId) ? null : outputId;
        RenderFellBack = fellBack;
        RenderDeviceName = renderDevice.FriendlyName;
        _lastPacketTick = 0; _packetMs = 10; _latencyEma = 0; _backlogEma = 0; _gotData = false; Volatile.Write(ref _latencyTick, 0);
        _output = output;
        _captureDevice = captureDevice;
        _renderDevice = renderDevice;
        _renderDeviceId = renderDevice.ID;
        IsFaulted = false;
        CurrentDeviceName = info.Name;
        CurrentDeviceId = info.Id;

        capture.StartRecording();
        output.Start(limiter);
    }

    /// <summary>Smallest safety margin offered. Below this the card's packet timing alone cuts the sound off.</summary>
    public const int MinBufferMs = 20;

    private byte[]? _dropScratch;

    /// <summary>Capture thread: update the smoothed latency (packet interval + backlog + device queue).</summary>
    private void UpdateLatency(BufferedWaveProvider buffer)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPacketTick != 0)
        {
            double iv = (now - _lastPacketTick) * 1000.0 / Stopwatch.Frequency;
            if (iv > 0 && iv < 200) _packetMs = _packetMs * 0.9 + iv * 0.1;
        }
        _lastPacketTick = now;

        double queued = _output == null ? 0 : _output.PaddingMs + _output.StreamLatencyMs;
        double backlog = buffer.BufferedDuration.TotalMilliseconds;
        _backlogEma = Volatile.Read(ref _latencyTick) == 0 ? backlog : _backlogEma * 0.9 + backlog * 0.1;
        double total = _packetMs + backlog + queued;
        double prev = _latencyEma;
        _latencyEma = Volatile.Read(ref _latencyTick) == 0 ? total : prev * 0.9 + total * 0.1;
        Volatile.Write(ref _latencyTick, now);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var buffer = _buffer;
        if (buffer == null) return;
        try { UpdateLatency(buffer); } catch { /* measurement only */ }

        // Capture and render run on independent clocks, so the backlog drifts —
        // and nothing pulls it back down on its own. The setting is therefore
        // treated as a LATENCY TARGET that is actively held, not just an upper
        // bound: previously the delay was free to wander up to 2x the setting
        // (400ms at the default) before anything trimmed it, which is the lag
        // you can hear. Trim early, and only the excess (oldest samples) —
        // ClearBuffer() would leave a whole buffer of silence instead.
        // The A/V-sync delay is part of that target, so it is held, not trimmed away.
        double extra = ExtraDelayMs;
        double target = Math.Max(MinBufferMs, BufferMilliseconds) + extra;
        double buffered = buffer.BufferedDuration.TotalMilliseconds;
        // Hysteresis so normal jitter doesn't cause constant micro-trims.
        double allowance = target + Math.Max(20.0, target * 0.25);
        if (buffered > allowance)
        {
            WaveFormat wf = buffer.WaveFormat;
            int excess = (int)((buffered - target) * wf.AverageBytesPerSecond / 1000.0);
            excess -= excess % wf.BlockAlign;          // keep frame alignment
            if (excess > 0)
            {
                if (_dropScratch == null || _dropScratch.Length < excess)
                    _dropScratch = new byte[excess];
                buffer.Read(_dropScratch, 0, excess);  // discards from the front
            }
        }

        // The cushion (margin + A/V-sync delay) is queued audio, and an under-run (render clock a touch
        // faster than capture, or the very first packet) eats into it. Top it back up with silence once
        // it has really been lost; a hysteresis avoids a gap every packet. The silence is the cushion.
        double lost = target - buffered;
        if (!_gotData || lost > Math.Max(10.0, target * 0.3))
        {
            WaveFormat wf = buffer.WaveFormat;
            int pad = (int)(lost * wf.AverageBytesPerSecond / 1000.0);
            pad -= pad % wf.BlockAlign;
            if (pad > 0) buffer.AddSamples(new byte[pad], 0, pad);
        }
        _gotData = true;

        buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    /// <summary>Volume as a percentage. Above 100% is software gain; the soft
    /// limiter keeps it from hard-clipping, which is what makes this range
    /// usable rather than merely loud.</summary>
    public const int MaxVolumePercent = 500;

    /// <summary>True when the limiter is currently shaving peaks — i.e. the
    /// volume is beyond what the signal can take cleanly.</summary>
    public bool IsLimiting => _limiter?.Limiting == true;

    /// <summary>
    /// The capture endpoint's own level (0..100), i.e. Windows' recording-device
    /// volume for the capture card. Raising this amplifies BEFORE our software
    /// gain, so it is the cleanest headroom available — free loudness with no
    /// added distortion. Returns -1 when the device exposes no volume control.
    /// </summary>
    public int CaptureLevelPercent
    {
        get
        {
            try
            {
                MMDevice? d = _captureDevice;
                if (d == null) return -1;
                return (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100f);
            }
            catch { return -1; }
        }
        set
        {
            try
            {
                MMDevice? d = _captureDevice;
                if (d == null) return;
                d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 100) / 100f;
            }
            catch { /* device without a volume control */ }
        }
    }

    public int VolumePercent
    {
        get => (int)Math.Round(_volume * 100f);
        set
        {
            int clamped = Math.Clamp(value, 0, MaxVolumePercent);
            _volume = clamped / 100f;
            if (_volumeProvider != null)
                _volumeProvider.Volume = _muted ? 0f : _volume;
        }
    }

    public void Stop()
    {
        if (_capture != null)
            _capture.DataAvailable -= OnDataAvailable;

        try { _capture?.StopRecording(); } catch { /* ignore */ }

        try { _output?.Dispose(); } catch { /* ignore */ }
        try { _capture?.Dispose(); } catch { /* ignore */ }
        // Dispose the device objects only after the client/output that use them.
        try { _renderDevice?.Dispose(); } catch { /* ignore */ }
        try { _captureDevice?.Dispose(); } catch { /* ignore */ }

        _output = null;
        _capture = null;
        _buffer = null;
        _volumeProvider = null;
        _limiter = null;
        RenderDeviceName = null;
        RenderFellBack = false;
        _requestedOutputId = null;
        _renderDevice = null;
        _captureDevice = null;
        _renderDeviceId = null;
        IsFaulted = false;
        CurrentDeviceName = null;
        CurrentDeviceId = null;
    }

    public void Dispose() => Stop();
}
