using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MultiWave.Models;

namespace MultiWave.Services;

/// <summary>Measures relative acoustic arrival using a real Windows microphone and selected render endpoints.</summary>
public sealed class AcousticCalibrationService
{
    public async Task<AcousticCalibrationResult> MeasureAndAlignAsync(
        string microphoneId,
        IReadOnlyList<(string Id, string Name, float CurrentDelay)> outputs,
        AudioOutputService outputService,
        CancellationToken cancellationToken = default)
    {
        if (outputs.Count < 2)
            throw new InvalidOperationException("Acoustic alignment requires at least two active selected playback endpoints.");
        var renderFormat = outputService.CalibrationFormat ?? throw new InvalidOperationException("No selected output stream is active.");
        if (renderFormat.Encoding != WaveFormatEncoding.IeeeFloat || renderFormat.BitsPerSample != 32)
            throw new NotSupportedException($"The active render stream is {renderFormat}; calibration requires its float32 processing format.");

        using var enumerator = new MMDeviceEnumerator();
        using var microphone = enumerator.GetDevice(microphoneId);
        if (microphone.DataFlow != DataFlow.Capture || microphone.State != DeviceState.Active)
            throw new InvalidOperationException("Choose an active microphone or line-in capture endpoint.");

        var recorder = new WasapiRecorderBuilder()
            .WithDevice(microphone)
            .WithSharedMode()
            .WithEventSync()
            .Build();
        var chunks = new ConcurrentQueue<CaptureChunk>();
        void OnData(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            var copy = bytes.ToArray();
            if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(copy);
            chunks.Enqueue(new CaptureChunk(Stopwatch.GetTimestamp(), DecodeMono(copy, recorder.WaveFormat)));
        }

        recorder.DataAvailable += OnData;
        try
        {
            recorder.StartRecording();
            await Task.Delay(450, cancellationToken); // ambient reference for noise floor
            var baseline = chunks.ToArray();
            var noiseFloor = baseline.Length == 0 ? 0 : baseline.Average(chunk => Rms(chunk.Samples));
            var probe = MakeProbe(renderFormat.SampleRate, renderFormat.Channels, 0.32);
            var arrivals = new Dictionary<string, (double ArrivalMilliseconds, double Confidence)>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outputService.ClearOutputBuffers();
                await Task.Delay(180, cancellationToken);
                var enqueuedAt = Stopwatch.GetTimestamp();
                await outputService.PlayCalibrationProbeAsync(target.Id, probe, cancellationToken);
                var start = FindFirstProbeArrival(chunks.ToArray(), enqueuedAt, recorder.WaveFormat.SampleRate, noiseFloor);
                if (start is null)
                    throw new InvalidOperationException($"No clear probe was detected from {target.Name}. Check microphone permission, mic placement, output volume, and room noise, then try again.");
                arrivals[target.Id] = start.Value;
                await Task.Delay(220, cancellationToken);
            }

            // Remove the currently configured per-speaker delay before calculating the new target.
            // Otherwise repeated calibration would only ever increase delays and could not undo a
            // previous correction.
            var baselineArrival = outputs.ToDictionary(
                output => output.Id,
                output => arrivals[output.Id].ArrivalMilliseconds - output.CurrentDelay,
                StringComparer.OrdinalIgnoreCase);
            var earliestArrival = baselineArrival.Values.Min();
            var latestArrival = baselineArrival.Values.Max();
            var relativeSpread = latestArrival - earliestArrival;
            if (relativeSpread > 500)
                throw new InvalidOperationException($"Measured speaker arrival spread is {relativeSpread:0} ms, beyond the 500 ms delay range. No delays were changed.");

            // Add delay only to earlier arrivals. Preserve the current common offset as much as the
            // available range allows; measurements are relative to this microphone position.
            var targetArrival = latestArrival;
            var result = new Dictionary<string, AcousticDeviceMeasurement>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in outputs)
            {
                var arrival = arrivals[output.Id].ArrivalMilliseconds;
                var applied = (float)Math.Clamp(targetArrival - baselineArrival[output.Id], 0, 500);
                result[output.Id] = new AcousticDeviceMeasurement(output.Id, output.Name, arrival,
                    arrivals[output.Id].Confidence, applied, applied - output.CurrentDelay);
            }
            return new AcousticCalibrationResult(result);
        }
        finally
        {
            recorder.DataAvailable -= OnData;
            try { recorder.StopRecording(); } catch { }
            recorder.Dispose();
            outputService.ClearOutputBuffers();
        }
    }

    private static float[] MakeProbe(int sampleRate, int channels, double seconds)
    {
        int frames = (int)(sampleRate * seconds);
        var data = new float[frames * channels];
        // A short swept-frequency tone is distinguishable from speech and is robust to room coloration.
        for (var frame = 0; frame < frames; frame++)
        {
            double t = (double)frame / sampleRate;
            double progress = t / seconds;
            double frequency = 500 * Math.Pow(6, progress); // 500 Hz to 3 kHz
            double fade = Math.Min(1, Math.Min(t / 0.015, (seconds - t) / 0.025));
            float value = (float)(0.075 * Math.Clamp(fade, 0, 1) * Math.Sin(2 * Math.PI * frequency * t));
            for (var channel = 0; channel < channels; channel++) data[frame * channels + channel] = value;
        }
        return data;
    }

    private static (double ArrivalMilliseconds, double Confidence)? FindFirstProbeArrival(CaptureChunk[] chunks, long enqueuedAt, int sampleRate, double noiseFloor)
    {
        // WASAPI capture packets are short; use packet RMS against the measured room-noise floor.
        // The result is relative arrival timing, not an absolute Bluetooth latency claim.
        var threshold = Math.Max(0.003, noiseFloor * 3.2);
        foreach (var chunk in chunks)
        {
            if (chunk.Timestamp < enqueuedAt || chunk.Samples.Length == 0) continue;
            // Find the first energetic 2 ms window within the WASAPI packet. A packet-level
            // timestamp alone would quantize measurements to the capture packet duration.
            int window = Math.Max(16, sampleRate / 500);
            for (var offset = 0; offset + window <= chunk.Samples.Length; offset += window)
            {
                double sum = 0;
                for (var i = offset; i < offset + window; i++) sum += chunk.Samples[i] * (double)chunk.Samples[i];
                var rms = Math.Sqrt(sum / window);
                if (rms < threshold) continue;
                var packetStartTicks = chunk.Timestamp - (long)(chunk.Samples.Length * (double)Stopwatch.Frequency / sampleRate);
                var arrivalTicks = packetStartTicks + (long)(offset * (double)Stopwatch.Frequency / sampleRate);
                if (arrivalTicks < enqueuedAt) continue;
                return ((arrivalTicks - enqueuedAt) * 1000d / Stopwatch.Frequency,
                    Math.Clamp(rms / Math.Max(noiseFloor * 6, threshold), 0, 1));
            }
        }
        return null;
    }

    private static float[] DecodeMono(byte[] bytes, WaveFormat format)
    {
        var standard = format.AsStandardWaveFormat();
        int sampleBytes = standard.BitsPerSample / 8;
        if (sampleBytes is < 1 or > 4 || standard.Channels < 1) return [];
        var frames = bytes.Length / Math.Max(1, format.BlockAlign);
        var result = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < standard.Channels; channel++)
            {
                int offset = frame * format.BlockAlign + channel * sampleBytes;
                float sample = standard.Encoding switch
                {
                    WaveFormatEncoding.IeeeFloat when sampleBytes == 4 => BitConverter.ToSingle(bytes, offset),
                    WaveFormatEncoding.Pcm when sampleBytes == 1 => (bytes[offset] - 128) / 128f,
                    WaveFormatEncoding.Pcm when sampleBytes == 2 => BitConverter.ToInt16(bytes, offset) / 32768f,
                    WaveFormatEncoding.Pcm when sampleBytes == 3 => ReadInt24(bytes, offset) / 8388608f,
                    WaveFormatEncoding.Pcm when sampleBytes == 4 => BitConverter.ToInt32(bytes, offset) / 2147483648f,
                    _ => 0
                };
                sum += float.IsFinite(sample) ? sample : 0;
            }
            result[frame] = (float)(sum / standard.Channels);
        }
        return result;
    }

    private static int ReadInt24(byte[] bytes, int offset)
    {
        int value = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static double Rms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * (double)sample;
        return Math.Sqrt(sum / samples.Length);
    }

    private sealed record CaptureChunk(long Timestamp, float[] Samples);
}
