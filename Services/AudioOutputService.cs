using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using MultiWave.Models;

namespace MultiWave.Services;

/// <summary>Creates one independent WASAPI render stream and conversion buffer per target endpoint.</summary>
public sealed class AudioOutputService : IDisposable
{
    private readonly Dictionary<string, OutputStream> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;
    private float _masterVolume = 0.7f;
    private bool _masterMuted;
    private bool _autoSyncEnabled = true;

    public IReadOnlyCollection<string> OutputDeviceIds
    {
        get { lock (_gate) return _outputs.Keys.ToArray(); }
    }
    public event Action<string, Exception>? OutputFailed;

    public void PrepareOutputs(IEnumerable<string> targetDeviceIds, string captureSourceId, WaveFormat mixerFormat, AudioPreset preset, IReadOnlyDictionary<string, float>? delays = null, IReadOnlyDictionary<string, float>? bufferDurations = null, IReadOnlyDictionary<string, EqualizerSettings>? equalizers = null, bool autoSyncEnabled = true)
    {
        DisposeOutputs();
        _autoSyncEnabled = autoSyncEnabled;
        using var enumerator = new MMDeviceEnumerator();

        foreach (var deviceId in targetDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Loopback is a tap, not an intercept. Rendering this captured mix back into its own
            // source endpoint would capture the copy again and create an audio feedback loop.
            if (string.Equals(deviceId, captureSourceId, StringComparison.OrdinalIgnoreCase))
                continue;

            MMDevice? device = null;
            WasapiPlayer? output = null;
            try
            {
                device = enumerator.GetDevice(deviceId);
                if (device.State != DeviceState.Active)
                    throw new InvalidOperationException("The playback endpoint is no longer active.");

                using var audioClient = device.CreateAudioClient();
                var endpointFormat = audioClient.MixFormat;
                var requestedBufferMs = bufferDurations is not null && bufferDurations.TryGetValue(deviceId, out var bufferMs)
                    ? Math.Clamp(bufferMs, 80, 500)
                    : 250;
                var buffer = new BufferedWaveProvider(mixerFormat, TimeSpan.FromMilliseconds(requestedBufferMs))
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = false
                };

                // Mixer buffers are explicitly IEEE float32. WaveToSampleProvider only accepts
                // that format; PCM loopback bytes are converted in AudioMixerService first.
                ISampleProvider samples = new WaveToSampleProvider(buffer);
                if (samples.WaveFormat.Channels != endpointFormat.Channels)
                {
                    if (samples.WaveFormat.Channels == 1 && endpointFormat.Channels == 2)
                        samples = new MonoToStereoSampleProvider(samples);
                    else if (samples.WaveFormat.Channels == 2 && endpointFormat.Channels == 1)
                        samples = new StereoToMonoSampleProvider(samples);
                    else
                        throw new NotSupportedException($"Cannot adapt {samples.WaveFormat.Channels} capture channels to {endpointFormat.Channels} endpoint channels.");
                }

                if (samples.WaveFormat.SampleRate != endpointFormat.SampleRate)
                    samples = new WdlResamplingSampleProvider(samples, endpointFormat.SampleRate);

                // WASAPI shared mode uses the endpoint mix rate; per-endpoint resampling above
                // handles different Bluetooth and wired endpoint rates independently.
                var dsp = new SpeakerDspSampleProvider(samples, preset);
                var equalizerSettings = equalizers is not null && equalizers.TryGetValue(deviceId, out var deviceEq)
                    ? deviceEq : EqualizerSettings.CreatePreset(EqualizerPreset.Flat);
                var equalizer = new EqualizerSampleProvider(dsp, equalizerSettings);
                var tone = new CalibrationToneSampleProvider(equalizer);
                var underrunMonitor = new UnderrunFillingSampleProvider(tone);
                var adjustableDelay = new AdjustableDelaySampleProvider(underrunMonitor);
                if (delays is not null && delays.TryGetValue(deviceId, out var delayMs))
                    adjustableDelay.DelayMilliseconds = delayMs;
                IWaveProvider stream = new SampleToWaveProvider16(adjustableDelay);
                var delayProvider = new InitialDelayWaveProvider(stream);
                output = new WasapiPlayerBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLatency(100)
                    .Build();
                output.PlaybackStopped += (_, args) =>
                {
                    if (args.Exception is not null)
                        OutputFailed?.Invoke(deviceId, args.Exception);
                };
                output.Init(delayProvider);
                output.Volume = 1f;

                Debug.WriteLine($"Audio output [{device.FriendlyName}] id={deviceId}: mixer={FormatDescription(mixerFormat)}; endpointMix={FormatDescription(endpointFormat)}; playerInput={FormatDescription(stream.WaveFormat)}; wasapiOutput={FormatDescription(output.OutputWaveFormat)}; preset={preset}; autoHeadroom=-3dB; reportedLatency={output.LatencyMilliseconds}ms");

                lock (_gate)
                {
                    var streamState = new OutputStream(device, output, buffer, delayProvider, dsp, equalizer, tone, adjustableDelay, underrunMonitor, output.LatencyMilliseconds);
                    ApplyMasterGain(streamState);
                    _outputs.Add(deviceId, streamState);
                }
                device = null;
                output = null;
            }
            catch
            {
                output?.Dispose();
                device?.Dispose();
                throw;
            }
        }

        lock (_gate) ApplyRendererLatencyAlignment();
    }

    private static string FormatDescription(WaveFormat format)
    {
        var subtype = format is WaveFormatExtensible extensible ? $", subtype={extensible.SubFormat}" : "";
        return $"{format.Encoding}{subtype}, {format.Channels} ch, {format.SampleRate} Hz, {format.BitsPerSample}-bit";
    }

    public void Write(byte[] data, int count)
    {
        lock (_gate)
        {
            foreach (var stream in _outputs.Values)
            {
                if (stream.Buffer.BufferedBytes + count > stream.Buffer.BufferLength)
                    stream.OverflowBytes += stream.Buffer.BufferedBytes + count - stream.Buffer.BufferLength;
                stream.Buffer.AddSamples(data, 0, count);
            }
        }
    }

    public void PlayCalibrationTone()
    {
        lock (_gate)
            foreach (var stream in _outputs.Values)
                stream.Tone.PlayTwoSeconds();
    }

    public WaveFormat? CalibrationFormat { get { lock (_gate) return _outputs.Values.FirstOrDefault()?.Buffer.WaveFormat; } }

    public async Task PlayCalibrationProbeAsync(string deviceId, float[] interleavedSamples, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[interleavedSamples.Length * sizeof(float)];
        Buffer.BlockCopy(interleavedSamples, 0, bytes, 0, bytes.Length);
        lock (_gate)
        {
            if (!_outputs.TryGetValue(deviceId, out var stream))
                throw new InvalidOperationException("The selected playback endpoint is not currently active in the audio engine.");
            stream.Buffer.ClearBuffer();
            stream.Buffer.AddSamples(bytes, 0, bytes.Length);
        }
        // Covers the full 0–500 ms configurable software delay plus normal endpoint latency.
        await Task.Delay(1100, cancellationToken);
    }

    public void ClearOutputBuffers()
    {
        lock (_gate) foreach (var stream in _outputs.Values) stream.Buffer.ClearBuffer();
    }

    public void SetStreamVolume(string deviceId, float volume)
    {
        lock (_gate)
        {
            if (_outputs.TryGetValue(deviceId, out var stream))
            {
                stream.TrimVolume = Math.Clamp(volume, 0f, 1f);
                ApplyMasterGain(stream);
            }
        }
    }

    public void SetMasterVolume(float volume)
    {
        lock (_gate)
        {
            _masterVolume = Math.Clamp(volume, 0f, 1f);
            foreach (var stream in _outputs.Values) ApplyMasterGain(stream);
        }
    }

    public void SetMasterMuted(bool muted)
    {
        lock (_gate)
        {
            _masterMuted = muted;
            foreach (var stream in _outputs.Values) ApplyMasterGain(stream);
        }
    }

    public void SetAutoSyncEnabled(bool enabled)
    {
        lock (_gate)
        {
            _autoSyncEnabled = enabled;
            ApplyRendererLatencyAlignment();
        }
    }

    private void ApplyMasterGain(OutputStream stream) =>
        stream.Dsp.Volume = stream.TrimVolume * (_masterMuted ? 0 : _masterVolume);

    private void ApplyRendererLatencyAlignment()
    {
        var slowestReportedLatency = _outputs.Values.Select(stream => stream.ReportedLatencyMs).DefaultIfEmpty(0).Max();
        foreach (var stream in _outputs.Values)
        {
            var compensation = _autoSyncEnabled ? Math.Max(0, slowestReportedLatency - stream.ReportedLatencyMs) : 0;
            stream.Delay.SetAdditionalDelay(compensation);
            stream.RendererCompensationMs = compensation;
        }
    }

    public void SetPreset(AudioPreset preset)
    {
        lock (_gate)
        {
            foreach (var stream in _outputs.Values)
                stream.Dsp.Preset = preset;
        }
    }

    public void SetOutputDelay(string deviceId, float milliseconds)
    {
        lock (_gate)
            if (_outputs.TryGetValue(deviceId, out var stream))
                stream.AdjustableDelay.DelayMilliseconds = milliseconds;
    }

    public void SetOutputEqualizer(string deviceId, EqualizerSettings settings)
    {
        lock (_gate)
            if (_outputs.TryGetValue(deviceId, out var stream))
                stream.Equalizer.Processor.Update(settings);
    }

    public IReadOnlyList<AudioOutputDiagnostics> GetTimingSnapshot()
    {
        lock (_gate)
            return _outputs.Select(pair => new AudioOutputDiagnostics(
                pair.Key,
                pair.Value.Device.FriendlyName,
                pair.Value.Output.LatencyMilliseconds,
                pair.Value.Buffer.BufferedBytes * 1000d / pair.Value.Buffer.WaveFormat.AverageBytesPerSecond,
                pair.Value.Buffer.BufferLength * 1000d / pair.Value.Buffer.WaveFormat.AverageBytesPerSecond,
                pair.Value.UnderrunMonitor.UnderrunSamples,
                pair.Value.OverflowBytes,
                pair.Value.RendererCompensationMs)).ToArray();
    }

    public void StartAll()
    {
        foreach (var stream in _outputs.Values)
            stream.Output.Play();
    }

    public void StopAll()
    {
        foreach (var stream in _outputs.Values)
        {
            try { stream.Output.Stop(); } catch { /* endpoint may have disappeared */ }
        }
    }

    public void Reset() => DisposeOutputs();

    private void DisposeOutputs()
    {
        lock (_gate)
        {
            foreach (var stream in _outputs.Values)
            {
                try { stream.Output.Stop(); } catch { }
                stream.Dispose();
            }
            _outputs.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeOutputs();
    }

    private sealed class OutputStream(MMDevice device, WasapiPlayer output, BufferedWaveProvider buffer, InitialDelayWaveProvider delay, SpeakerDspSampleProvider dsp, EqualizerSampleProvider equalizer, CalibrationToneSampleProvider tone, AdjustableDelaySampleProvider adjustableDelay, UnderrunFillingSampleProvider underrunMonitor, int reportedLatencyMs) : IDisposable
    {
        public MMDevice Device { get; } = device;
        public WasapiPlayer Output { get; } = output;
        public BufferedWaveProvider Buffer { get; } = buffer;
        public InitialDelayWaveProvider Delay { get; } = delay;
        public SpeakerDspSampleProvider Dsp { get; } = dsp;
        public EqualizerSampleProvider Equalizer { get; } = equalizer;
        public CalibrationToneSampleProvider Tone { get; } = tone;
        public AdjustableDelaySampleProvider AdjustableDelay { get; } = adjustableDelay;
        public UnderrunFillingSampleProvider UnderrunMonitor { get; } = underrunMonitor;
        public int ReportedLatencyMs { get; } = reportedLatencyMs;
        public long OverflowBytes;
        public float TrimVolume = 1f;
        public int RendererCompensationMs;

        public void Dispose()
        {
            try { Output.Stop(); } catch { }
            Output.Dispose();
            Device.Dispose();
        }
    }

    private sealed class UnderrunFillingSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private long _underrunSamples;
        public WaveFormat WaveFormat => source.WaveFormat;
        public long UnderrunSamples => Interlocked.Read(ref _underrunSamples);

        public int Read(float[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            var read = source.Read(buffer);
            if (read < buffer.Length)
            {
                buffer[read..].Clear();
                Interlocked.Add(ref _underrunSamples, buffer.Length - read);
                return buffer.Length;
            }
            return read;
        }
    }

    /// <summary>Inserts initial silence to align streams with a slower reported WASAPI renderer.</summary>
    public sealed class InitialDelayWaveProvider(IWaveProvider source) : IWaveProvider
    {
        private int _remainingDelayBytes;
        public WaveFormat WaveFormat => source.WaveFormat;

        public void SetAdditionalDelay(int milliseconds)
        {
            var bytes = Math.Max(0, milliseconds) * WaveFormat.AverageBytesPerSecond / 1000;
            bytes -= bytes % WaveFormat.BlockAlign;
            Interlocked.Exchange(ref _remainingDelayBytes, bytes);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var silenceBytes = TakeDelayBytes(count);
            if (silenceBytes > 0)
            {
                Array.Clear(buffer, offset, silenceBytes);
            }

            if (silenceBytes == count)
                return count;

            var bytesRead = source.Read(buffer.AsSpan(offset + silenceBytes, count - silenceBytes));
            return silenceBytes + bytesRead;
        }

        public int Read(Span<byte> buffer)
        {
            var silenceBytes = TakeDelayBytes(buffer.Length);
            if (silenceBytes > 0)
            {
                buffer[..silenceBytes].Clear();
            }

            if (silenceBytes == buffer.Length)
                return buffer.Length;

            var bytesRead = source.Read(buffer[silenceBytes..]);
            return silenceBytes + bytesRead;
        }

        private int TakeDelayBytes(int maximum)
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _remainingDelayBytes);
                if (remaining <= 0) return 0;
                var silence = Math.Min(maximum, remaining);
                silence -= silence % WaveFormat.BlockAlign;
                if (silence <= 0) return 0;
                if (Interlocked.CompareExchange(ref _remainingDelayBytes, remaining - silence, remaining) == remaining)
                    return silence;
            }
        }
    }
}
