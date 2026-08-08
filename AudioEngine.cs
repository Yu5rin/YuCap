using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// WASAPI passthrough: captures from a capture endpoint (the capture card's
/// line-in) and plays it out of the default render device, with software volume.
/// The capture stream is resampled/channel-matched to the render mix format so
/// shared-mode initialization succeeds across device combinations.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volumeProvider;
    private SoftLimitSampleProvider? _limiter;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;

    private float _volume = 1.0f; // 0.0 .. 2.0
    private bool _muted;
    private string? _renderDeviceId;

    /// <summary>Target capture-to-playback latency in milliseconds. Actively
    /// held (see OnDataAvailable), so this is roughly the delay you hear.</summary>
    public int BufferMilliseconds { get; set; } = 120;

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
    public void Start(AudioDeviceInfo info)
    {
        Stop();

        var enumerator = new MMDeviceEnumerator();
        MMDevice captureDevice = enumerator.GetDevice(info.Id);
        MMDevice renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        enumerator.Dispose();

        var capture = new WasapiCapture(captureDevice);
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(Math.Max(MinBufferMs, BufferMilliseconds) * 4),
            DiscardOnBufferOverflow = true,
        };

        // Build the sample chain and match it to the render mix format.
        ISampleProvider sample = buffer.ToSampleProvider();

        int targetRate = renderDevice.AudioClient.MixFormat.SampleRate;
        int targetChannels = renderDevice.AudioClient.MixFormat.Channels;

        if (sample.WaveFormat.Channels == 1 && targetChannels >= 2)
            sample = new MonoToStereoSampleProvider(sample);
        else if (sample.WaveFormat.Channels > 2 && targetChannels == 2)
            sample = new MultiplexingSampleProvider(new[] { sample }, 2);

        if (sample.WaveFormat.SampleRate != targetRate)
            sample = new WdlResamplingSampleProvider(sample, targetRate);

        var volumeProvider = new VolumeSampleProvider(sample) { Volume = _muted ? 0f : _volume };
        // Gain first, then the limiter catches whatever the gain pushed over.
        var limiter = new SoftLimitSampleProvider(volumeProvider);

        var output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, true,
            Math.Max(MinBufferMs, BufferMilliseconds));
        output.Init(limiter);
        output.PlaybackStopped += (_, e) => { if (e.Exception != null) IsFaulted = true; };

        capture.DataAvailable += OnDataAvailable;

        _capture = capture;
        _buffer = buffer;
        _volumeProvider = volumeProvider;
        _limiter = limiter;
        _output = output;
        _captureDevice = captureDevice;
        _renderDevice = renderDevice;
        _renderDeviceId = renderDevice.ID;
        IsFaulted = false;
        CurrentDeviceName = info.Name;
        CurrentDeviceId = info.Id;

        capture.StartRecording();
        output.Play();
    }

    /// <summary>Smallest buffer target that WASAPI shared mode can realistically
    /// sustain: the audio engine period and the capture packet size are both
    /// ~10ms, so anything under a handful of periods just underruns.</summary>
    public const int MinBufferMs = 50;

    private byte[]? _dropScratch;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var buffer = _buffer;
        if (buffer == null) return;

        // Capture and render run on independent clocks, so the backlog drifts —
        // and nothing pulls it back down on its own. The setting is therefore
        // treated as a LATENCY TARGET that is actively held, not just an upper
        // bound: previously the delay was free to wander up to 2x the setting
        // (400ms at the default) before anything trimmed it, which is the lag
        // you can hear. Trim early, and only the excess (oldest samples) —
        // ClearBuffer() would leave a whole buffer of silence instead.
        double target = Math.Max(MinBufferMs, BufferMilliseconds);
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

        try { _output?.Stop(); } catch { /* ignore */ }
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
        _renderDevice = null;
        _captureDevice = null;
        _renderDeviceId = null;
        IsFaulted = false;
        CurrentDeviceName = null;
        CurrentDeviceId = null;
    }

    public void Dispose() => Stop();
}
