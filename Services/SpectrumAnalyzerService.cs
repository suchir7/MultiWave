using System.Numerics;

namespace MultiWave.Services;

/// <summary>Collects real post-global-EQ samples lock-free and computes logarithmic FFT bars off the audio thread.</summary>
public sealed class SpectrumAnalyzerService : IAsyncDisposable
{
    private const int FftSize = 4096;
    private const int BarCount = 32;
    private readonly float[] _ring = new float[FftSize * 4];
    private readonly float[] _windowed = new float[FftSize];
    private readonly float[] _hannWindow = new float[FftSize];
    private readonly float[] _decayFrame = new float[BarCount];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _smoothed = new float[BarCount];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private long _writeCursor;
    private int _sampleRate = 48000;
    private long _lastPublishedCursor;
    private long _lastObservedCursor;

    public event Action<float[]>? SpectrumAvailable;

    public SpectrumAnalyzerService()
    {
        for (var i = 0; i < FftSize; i++) _hannWindow[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1));
        _worker = Task.Run(AnalyzeLoopAsync);
    }
    public void SetSampleRate(int sampleRate) => Volatile.Write(ref _sampleRate, sampleRate);

    public void PushSamples(ReadOnlySpan<float> samples, int channels)
    {
        var frameCount = samples.Length / channels;
        var cursor = _writeCursor;
        for (var frame = 0; frame < frameCount; frame++)
        {
            float mono = 0;
            for (var channel = 0; channel < channels; channel++)
                mono += samples[frame * channels + channel];
            _ring[(int)(cursor % _ring.Length)] = mono / channels;
            cursor++;
        }
        Volatile.Write(ref _writeCursor, cursor);
    }

    private async Task AnalyzeLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        try
        {
            while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
            {
                var cursor = Volatile.Read(ref _writeCursor);
                // Keep the analyzer dormant until loopback has supplied real samples. Emitting
                // empty decay frames during app startup only schedules needless UI work.
                if (cursor == 0)
                {
                    _lastObservedCursor = 0;
                    continue;
                }
                if (cursor - _lastPublishedCursor < FftSize / 4)
                {
                    if (cursor == _lastObservedCursor && _lastPublishedCursor > 0)
                    {
                        for (var bar = 0; bar < BarCount; bar++)
                        {
                            _smoothed[bar] *= 0.82f;
                            _decayFrame[bar] = _smoothed[bar];
                        }
                        SpectrumAvailable?.Invoke(_decayFrame);
                    }
                    _lastObservedCursor = cursor;
                    continue;
                }
                _lastPublishedCursor = cursor;
                _lastObservedCursor = cursor;
                for (var i = 0; i < FftSize; i++)
                {
                    var sample = cursor - FftSize + i;
                    var value = sample >= 0 ? _ring[(int)(sample % _ring.Length)] : 0;
                    _windowed[i] = value * _hannWindow[i];
                    _fft[i] = new Complex(_windowed[i], 0);
                }

                Transform(_fft);
                var rate = Volatile.Read(ref _sampleRate);
                var frame = new float[BarCount];
                var minHz = 35d;
                var maxHz = Math.Min(rate * 0.48, 18000);
                for (var bar = 0; bar < BarCount; bar++)
                {
                    var low = minHz * Math.Pow(maxHz / minHz, bar / (double)BarCount);
                    var high = minHz * Math.Pow(maxHz / minHz, (bar + 1d) / BarCount);
                    var firstBin = Math.Max(1, (int)Math.Floor(low * FftSize / rate));
                    var lastBin = Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(high * FftSize / rate));
                    double magnitude = 0;
                    for (var bin = firstBin; bin <= lastBin; bin++)
                        magnitude = Math.Max(magnitude, _fft[bin].Magnitude * 2 / FftSize);
                    var db = 20 * Math.Log10(Math.Max(1e-6, magnitude));
                    var normalized = (float)Math.Clamp((db + 72) / 72, 0, 1);
                    var coefficient = normalized > _smoothed[bar] ? 0.62f : 0.22f;
                    _smoothed[bar] += (normalized - _smoothed[bar]) * coefficient;
                    frame[bar] = _smoothed[bar];
                }
                SpectrumAvailable?.Invoke(frame);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void Transform(Complex[] values)
    {
        var n = values.Length;
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var root = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                var rotation = Complex.One;
                var half = length >> 1;
                for (var i = 0; i < half; i++)
                {
                    var even = values[start + i];
                    var odd = values[start + i + half] * rotation;
                    values[start + i] = even + odd;
                    values[start + i + half] = even - odd;
                    rotation *= root;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }
}
