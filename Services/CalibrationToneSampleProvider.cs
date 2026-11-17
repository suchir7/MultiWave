using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiWave.Services;

/// <summary>Adds a bounded 1 kHz tone on the render callback timeline without queueing seconds of audio.</summary>
public sealed class CalibrationToneSampleProvider(ISampleProvider source) : ISampleProvider
{
    private long _remainingFrames;
    private double _phase;

    public WaveFormat WaveFormat => source.WaveFormat;
    public void PlayTwoSeconds() => Interlocked.Exchange(ref _remainingFrames, WaveFormat.SampleRate * 2L);

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        var read = source.Read(buffer);
        var channels = WaveFormat.Channels;
        var frames = read / channels;
        var remaining = Interlocked.Read(ref _remainingFrames);
        var framesToTone = (int)Math.Min(frames, remaining);
        var phaseStep = 2 * Math.PI * 1000 / WaveFormat.SampleRate;
        for (var frame = 0; frame < framesToTone; frame++)
        {
            var tone = 0.08f * (float)Math.Sin(_phase);
            _phase += phaseStep;
            if (_phase >= 2 * Math.PI) _phase -= 2 * Math.PI;
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                buffer[index] = Math.Clamp(buffer[index] + tone, -0.95f, 0.95f);
            }
        }
        if (framesToTone > 0)
            Interlocked.Add(ref _remainingFrames, -framesToTone);
        return read;
    }
}
