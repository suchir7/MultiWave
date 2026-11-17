using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace MultiWave.Services;

/// <summary>Captures the current Windows render endpoint's system mix using WASAPI loopback.</summary>
public sealed class SystemAudioCaptureService : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiRecorder _capture;
    private bool _disposed;

    public string SourceDeviceId => _device.ID;
    public string SourceDeviceName => _device.FriendlyName;
    public WaveFormat WaveFormat => _capture.WaveFormat;
    public event Action<byte[], int>? AudioCaptured;
    public event Action<Exception?>? CaptureStopped;

    public SystemAudioCaptureService(string sourceDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(sourceDeviceId);
        try
        {
            _capture = new WasapiRecorderBuilder()
                .WithDevice(_device)
                .WithLoopbackCapture()
                .WithSharedMode()
                .WithEventSync()
                .Build();
        }
        catch
        {
            _device.Dispose();
            throw;
        }
        _capture.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += Capture_RecordingStopped;
        Debug.WriteLine($"Loopback source format: {FormatDescription(_capture.WaveFormat)}; normalized: {FormatDescription(_capture.WaveFormat.AsStandardWaveFormat())}");
    }

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        if (!_disposed && _capture.CaptureState != CaptureState.Stopped)
            _capture.StopRecording();
    }

    private void Capture_DataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var copy = buffer.ToArray();
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            if (_capture.WaveFormat.AsStandardWaveFormat().Encoding == WaveFormatEncoding.Pcm && _capture.WaveFormat.BitsPerSample == 8)
                Array.Fill(copy, (byte)128);
            else
                Array.Clear(copy);
        }
        AudioCaptured?.Invoke(copy, copy.Length);
    }

    private static string FormatDescription(WaveFormat format)
    {
        var subtype = format is WaveFormatExtensible extensible ? $", subtype={extensible.SubFormat}" : "";
        return $"{format.Encoding}{subtype}, {format.Channels} ch, {format.SampleRate} Hz, {format.BitsPerSample}-bit, block={format.BlockAlign}";
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e) => CaptureStopped?.Invoke(e.Exception);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _capture.DataAvailable -= Capture_DataAvailable;
        _capture.RecordingStopped -= Capture_RecordingStopped;
        try { _capture.StopRecording(); } catch { /* device may already have disconnected */ }
        _capture.Dispose();
        _device.Dispose();
    }
}
