using MultiWave.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiWave.Services;

public sealed class EqualizerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    public EqualizerProcessor Processor { get; }
    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source, EqualizerSettings settings)
    {
        _source = source;
        Processor = new EqualizerProcessor(source.WaveFormat.SampleRate, source.WaveFormat.Channels, settings);
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);
        Processor.Process(buffer[..read]);
        return read;
    }
}
