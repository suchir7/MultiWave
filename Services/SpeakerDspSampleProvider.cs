using MultiWave.Models;
using NAudio.Wave;

namespace MultiWave.Services;

/// <summary>Per-output tone preset, gain, headroom, and soft limiting in floating point.</summary>
public sealed class SpeakerDspSampleProvider : ISampleProvider
{
    private const float Headroom = 0.70710678f; // -3 dB before EQ and final limiting.
    private const float LimitThreshold = 0.92f;
    private readonly ISampleProvider _source;
    private readonly FilterBank[] _banks;
    private readonly int _fadeFrames;
    private float _volume = 1f;
    private int _requestedPreset = (int)AudioPreset.Music;
    private AudioPreset _activePreset = AudioPreset.Music;
    private AudioPreset _fadeFromPreset = AudioPreset.Music;
    private int _fadeRemaining;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0f, 1f));
    }

    public AudioPreset Preset
    {
        get => (AudioPreset)Volatile.Read(ref _requestedPreset);
        set => Volatile.Write(ref _requestedPreset, (int)value);
    }

    public SpeakerDspSampleProvider(ISampleProvider source, AudioPreset preset = AudioPreset.Music)
    {
        _source = source;
        _requestedPreset = (int)preset;
        _activePreset = preset;
        _fadeFromPreset = preset;
        _fadeFrames = Math.Max(1, source.WaveFormat.SampleRate / 30); // Smooth preset changes over ~33 ms.
        _banks =
        [
            new FilterBank(source.WaveFormat.SampleRate, source.WaveFormat.Channels, bassDb: 0.5f, vocalDb: 2.5f, trebleDb: 0.25f),
            new FilterBank(source.WaveFormat.SampleRate, source.WaveFormat.Channels, bassDb: 1.0f, vocalDb: 0f, trebleDb: 1.0f)
        ];
    }

    public int Read(Span<float> buffer)
    {
        var samplesRead = _source.Read(buffer);
        var channels = WaveFormat.Channels;
        var framesRead = samplesRead / channels;
        var gain = Volume * Headroom;
        var requestedPreset = Preset;

        if (requestedPreset != _activePreset)
        {
            _fadeFromPreset = _activePreset;
            _activePreset = requestedPreset;
            _fadeRemaining = _fadeFrames;
        }

        for (var frame = 0; frame < framesRead; frame++)
        {
            var blend = _fadeRemaining > 0 ? 1f - (float)_fadeRemaining / _fadeFrames : 1f;
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                var input = buffer[index];
                var from = _banks[(int)_fadeFromPreset].Process(channel, input);
                var to = _banks[(int)_activePreset].Process(channel, input);
                var sample = (from + (to - from) * blend) * gain;
                buffer[index] = SoftLimit(sample);
            }

            if (_fadeRemaining > 0)
                _fadeRemaining--;
        }

        return samplesRead;
    }

    private static float SoftLimit(float sample)
    {
        var magnitude = MathF.Abs(sample);
        if (magnitude <= LimitThreshold)
            return sample;

        var compressed = LimitThreshold + (1f - LimitThreshold) * MathF.Tanh((magnitude - LimitThreshold) / (1f - LimitThreshold));
        return MathF.CopySign(compressed, sample);
    }

    private sealed class FilterBank
    {
        private readonly BiquadState[][] _channelFilters;

        public FilterBank(int sampleRate, int channels, float bassDb, float vocalDb, float trebleDb)
        {
            var coefficients = new[]
            {
                BiquadCoefficients.Peaking(sampleRate, 115f, 0.8f, bassDb),
                BiquadCoefficients.Peaking(sampleRate, 2400f, 0.9f, vocalDb),
                BiquadCoefficients.Peaking(sampleRate, 8500f, 0.8f, trebleDb)
            };
            _channelFilters = Enumerable.Range(0, channels)
                .Select(_ => coefficients.Select(c => new BiquadState(c)).ToArray())
                .ToArray();
        }

        public float Process(int channel, float sample)
        {
            foreach (var filter in _channelFilters[channel])
                sample = filter.Process(sample);
            return sample;
        }
    }

    private readonly record struct BiquadCoefficients(float B0, float B1, float B2, float A1, float A2)
    {
        public static BiquadCoefficients Peaking(int sampleRate, float frequency, float q, float gainDb)
        {
            var a = MathF.Pow(10f, gainDb / 40f);
            var omega = 2f * MathF.PI * Math.Min(frequency, sampleRate * 0.45f) / sampleRate;
            var alpha = MathF.Sin(omega) / (2f * q);
            var cos = MathF.Cos(omega);
            var a0 = 1f + alpha / a;
            return new BiquadCoefficients(
                (1f + alpha * a) / a0,
                (-2f * cos) / a0,
                (1f - alpha * a) / a0,
                (-2f * cos) / a0,
                (1f - alpha / a) / a0);
        }
    }

    private sealed class BiquadState(BiquadCoefficients coefficients)
    {
        private float _x1;
        private float _x2;
        private float _y1;
        private float _y2;

        public float Process(float input)
        {
            var output = coefficients.B0 * input + coefficients.B1 * _x1 + coefficients.B2 * _x2
                - coefficients.A1 * _y1 - coefficients.A2 * _y2;
            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = output;
            return output;
        }
    }
}
