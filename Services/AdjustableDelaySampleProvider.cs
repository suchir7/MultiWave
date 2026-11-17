using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiWave.Services;

/// <summary>Per-output delay line. Delay changes crossfade between taps to avoid clicks.</summary>
public sealed class AdjustableDelaySampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _ring;
    private readonly float[] _scratch = new float[8192];
    private readonly object _gate = new();
    private long _writePosition;
    private double _delayFrames;
    private double _fromDelayFrames;
    private int _crossfadeRemaining;

    public AdjustableDelaySampleProvider(ISampleProvider source)
    {
        _source = source;
        _ring = new float[(source.WaveFormat.SampleRate / 2 + 8192 / source.WaveFormat.Channels + 2) * source.WaveFormat.Channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double DelayMilliseconds
    {
        get { lock (_gate) return _delayFrames * 1000d / WaveFormat.SampleRate; }
        set
        {
            lock (_gate)
            {
                _fromDelayFrames = _delayFrames;
                _delayFrames = Math.Round(Math.Clamp(value, 0, 500) * WaveFormat.SampleRate / 1000d);
                _crossfadeRemaining = Math.Max(1, WaveFormat.SampleRate / 30);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        var totalRead = 0;
        var channels = WaveFormat.Channels;
        while (totalRead < buffer.Length)
        {
            var requested = Math.Min(_scratch.Length, buffer.Length - totalRead);
            requested -= requested % channels;
            if (requested == 0) break;
            var read = _source.Read(_scratch.AsSpan(0, requested));
            if (read <= 0) break;
            read -= read % channels;

            lock (_gate)
            {
                var basePosition = _writePosition;
                for (var i = 0; i < read; i++)
                    _ring[(int)((basePosition + i) % _ring.Length)] = _scratch[i];
                _writePosition += read;

                for (var i = 0; i < read; i++)
                {
                    var samplePosition = _writePosition - read + i;
                    var target = ReadTap(samplePosition, _delayFrames);
                    if (_crossfadeRemaining > 0)
                    {
                        var old = ReadTap(samplePosition, _fromDelayFrames);
                        var blend = 1f - Math.Clamp(_crossfadeRemaining / (float)Math.Max(1, WaveFormat.SampleRate / 30), 0f, 1f);
                        target = old + (target - old) * blend;
                        if (i % channels == channels - 1)
                        {
                            _crossfadeRemaining--;
                            _fromDelayFrames += (_delayFrames - _fromDelayFrames) * 0.08;
                        }
                    }
                    buffer[totalRead + i] = target;
                }
            }

            totalRead += read;
            if (read < requested) break;
        }
        return totalRead;
    }

    private float ReadTap(long currentPosition, double delayFrames)
    {
        var position = currentPosition - delayFrames * WaveFormat.Channels;
        if (position < 0) return 0;
        var lower = (long)position;
        var fraction = (float)(position - lower);
        var first = _ring[(int)(lower % _ring.Length)];
        if (fraction <= 0) return first;
        var second = _ring[(int)((lower + 1) % _ring.Length)];
        return first + (second - first) * fraction;
    }
}
