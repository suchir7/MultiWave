using MultiWave.Models;

namespace MultiWave.Services;

/// <summary>Preallocated RBJ peaking/shelf biquads with immutable UI-published coefficients.</summary>
public sealed class EqualizerProcessor
{
    private const float Limit = 0.96f;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly BiquadState[,] _states;
    private readonly BiquadCoefficients[] _workingCoefficients = new BiquadCoefficients[33];
    private readonly BiquadCoefficients[] _startCoefficients = new BiquadCoefficients[33];
    private Configuration _configuration;
    private Configuration _transitionTarget;
    private int _transitionRemaining;
    private readonly int _transitionFrames;

    public EqualizerProcessor(int sampleRate, int channels, EqualizerSettings? initial = null)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _states = new BiquadState[channels, 33];
        for (var channel = 0; channel < channels; channel++)
            for (var band = 0; band < 33; band++)
                _states[channel, band] = new BiquadState();
        _transitionFrames = Math.Max(1, sampleRate / 25);
        _configuration = _transitionTarget = Build(initial ?? new EqualizerSettings());
        Array.Copy(_configuration.Coefficients, _workingCoefficients, 33);
        Array.Copy(_configuration.Coefficients, _startCoefficients, 33);
    }

    public void Update(EqualizerSettings settings) => Volatile.Write(ref _configuration, Build(settings));

    public void Process(Span<float> samples)
    {
        var target = Volatile.Read(ref _configuration);
        if (!ReferenceEquals(target, _transitionTarget))
        {
            Array.Copy(_workingCoefficients, _startCoefficients, 33);
            _transitionTarget = target;
            _transitionRemaining = _transitionFrames;
        }

        var frames = samples.Length / _channels;
        var coefficients = target.Coefficients;
        if (_transitionRemaining > 0)
        {
            var blend = 1f - _transitionRemaining / (float)_transitionFrames;
            for (var i = 0; i < 33; i++)
                _workingCoefficients[i] = BiquadCoefficients.Lerp(_startCoefficients[i], coefficients[i], blend);
            _transitionRemaining = Math.Max(0, _transitionRemaining - frames);
            if (_transitionRemaining == 0)
                Array.Copy(coefficients, _workingCoefficients, 33);
        }
        else if (!ReferenceEquals(_configuration, _transitionTarget))
        {
            Array.Copy(coefficients, _workingCoefficients, 33);
        }

        var gain = target.PreampLinear;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < _channels; channel++)
            {
                var index = frame * _channels + channel;
                var value = samples[index] * gain;
                for (var band = 0; band < 33; band++)
                    value = _states[channel, band].Process(value, _workingCoefficients[band]);
                samples[index] = target.Enabled ? SoftLimit(value) : value;
            }
        }
    }

    public static float GetResponseDb(EqualizerSettings settings, float frequency, int sampleRate)
    {
        if (!settings.Enabled) return 0;
        var total = settings.PreampDb;
        var frequencies = EqualizerBands.ForMode(settings.Use31Band);
        for (var i = 0; i < frequencies.Length && i < settings.BandGainsDb.Length; i++)
            total += BiquadCoefficients.Peaking(sampleRate, frequencies[i], settings.Use31Band ? 4.318f : 1f, settings.BandGainsDb[i]).MagnitudeDb(frequency, sampleRate);
        total += BiquadCoefficients.LowShelf(sampleRate, 120, settings.BassDb).MagnitudeDb(frequency, sampleRate);
        total += BiquadCoefficients.HighShelf(sampleRate, 6000, settings.TrebleDb).MagnitudeDb(frequency, sampleRate);
        return Math.Clamp(total, -18, 18);
    }

    private Configuration Build(EqualizerSettings settings)
    {
        var coefficients = new BiquadCoefficients[33];
        if (!settings.Enabled)
        {
            Array.Fill(coefficients, BiquadCoefficients.Identity);
            return new Configuration(false, 1, coefficients);
        }
        Array.Fill(coefficients, BiquadCoefficients.Identity);
        var frequencies = EqualizerBands.ForMode(settings.Use31Band);
        for (var i = 0; i < frequencies.Length; i++)
        {
            var gain = i < settings.BandGainsDb.Length ? Math.Clamp(settings.BandGainsDb[i], -12, 12) : 0;
            coefficients[i] = BiquadCoefficients.Peaking(_sampleRate, frequencies[i], settings.Use31Band ? 4.318f : 1f, gain);
        }
        coefficients[31] = BiquadCoefficients.LowShelf(_sampleRate, 120, Math.Clamp(settings.BassDb, -12, 12));
        coefficients[32] = BiquadCoefficients.HighShelf(_sampleRate, 6000, Math.Clamp(settings.TrebleDb, -12, 12));
        return new Configuration(settings.Enabled, MathF.Pow(10, Math.Clamp(settings.PreampDb, -12, 12) / 20), coefficients);
    }

    private static float SoftLimit(float value)
    {
        var magnitude = MathF.Abs(value);
        if (magnitude <= Limit) return value;
        return MathF.CopySign(Limit + (1 - Limit) * MathF.Tanh((magnitude - Limit) / (1 - Limit)), value);
    }

    private sealed record Configuration(bool Enabled, float PreampLinear, BiquadCoefficients[] Coefficients);

    private struct BiquadState
    {
        private float _x1, _x2, _y1, _y2;
        public float Process(float x, BiquadCoefficients c)
        {
            var y = c.B0 * x + c.B1 * _x1 + c.B2 * _x2 - c.A1 * _y1 - c.A2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    private readonly record struct BiquadCoefficients(float B0, float B1, float B2, float A0, float A1, float A2)
    {
        public static BiquadCoefficients Peaking(int rate, float frequency, float q, float db)
        {
            if (Math.Abs(db) < 0.0001f) return Identity;
            var w = 2 * MathF.PI * Math.Min(frequency, rate * 0.45f) / rate;
            var alpha = MathF.Sin(w) / (2 * q);
            var a = MathF.Pow(10, db / 40);
            return Normalize(1 + alpha * a, -2 * MathF.Cos(w), 1 - alpha * a,
                1 + alpha / a, -2 * MathF.Cos(w), 1 - alpha / a);
        }

        public static BiquadCoefficients LowShelf(int rate, float frequency, float db) => Shelf(rate, frequency, db, low: true);
        public static BiquadCoefficients HighShelf(int rate, float frequency, float db) => Shelf(rate, frequency, db, low: false);
        public static BiquadCoefficients Identity => new(1, 0, 0, 1, 0, 0);

        private static BiquadCoefficients Shelf(int rate, float frequency, float db, bool low)
        {
            if (Math.Abs(db) < 0.0001f) return Identity;
            var w = 2 * MathF.PI * Math.Min(frequency, rate * 0.45f) / rate;
            var cos = MathF.Cos(w);
            var sin = MathF.Sin(w);
            var a = MathF.Pow(10, db / 40);
            var alpha = sin / 2 * MathF.Sqrt(2);
            var root = 2 * MathF.Sqrt(a) * alpha;
            if (low)
                return Normalize(a * ((a + 1) - (a - 1) * cos + root), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - root),
                    (a + 1) + (a - 1) * cos + root, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - root);
            return Normalize(a * ((a + 1) + (a - 1) * cos + root), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - root),
                (a + 1) - (a - 1) * cos + root, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - root);
        }

        private static BiquadCoefficients Normalize(float b0, float b1, float b2, float a0, float a1, float a2) => new(b0 / a0, b1 / a0, b2 / a0, 1, a1 / a0, a2 / a0);
        public static BiquadCoefficients Lerp(BiquadCoefficients a, BiquadCoefficients b, float t) => new(
            a.B0 + (b.B0 - a.B0) * t, a.B1 + (b.B1 - a.B1) * t, a.B2 + (b.B2 - a.B2) * t, 1,
            a.A1 + (b.A1 - a.A1) * t, a.A2 + (b.A2 - a.A2) * t);

        public float MagnitudeDb(float frequency, int rate)
        {
            var w = 2 * Math.PI * frequency / rate;
            var cos1 = Math.Cos(w); var sin1 = Math.Sin(w);
            var cos2 = Math.Cos(2 * w); var sin2 = Math.Sin(2 * w);
            var nr = B0 + B1 * cos1 + B2 * cos2; var ni = -B1 * sin1 - B2 * sin2;
            var dr = 1 + A1 * cos1 + A2 * cos2; var di = -A1 * sin1 - A2 * sin2;
            return (float)(20 * Math.Log10(Math.Max(1e-6, Math.Sqrt(nr * nr + ni * ni) / Math.Sqrt(dr * dr + di * di))));
        }
    }
}
