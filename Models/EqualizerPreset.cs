namespace MultiWave.Models;

public enum EqualizerPreset
{
    Flat,
    Music,
    Cinema,
    Gaming,
    BassBoost,
    PodcastVoice,
    NightMode,
    Custom
}

public static class EqualizerBands
{
    public static readonly float[] TenBandFrequencies = [31.5f, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public static readonly float[] ThirtyOneBandFrequencies = [20, 25, 31.5f, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000];

    public static float[] ForMode(bool thirtyOneBand) => thirtyOneBand ? ThirtyOneBandFrequencies : TenBandFrequencies;
}

public sealed class EqualizerSettings
{
    public bool Enabled { get; set; }
    public bool Use31Band { get; set; }
    public float PreampDb { get; set; }
    public float BassDb { get; set; }
    public float TrebleDb { get; set; }
    public EqualizerPreset Preset { get; set; } = EqualizerPreset.Flat;
    public string? CustomPresetName { get; set; }
    public float[] BandGainsDb { get; set; } = new float[10];

    public void Normalize()
    {
        BandGainsDb ??= new float[10];
        PreampDb = Math.Clamp(PreampDb, -12, 12);
        BassDb = Math.Clamp(BassDb, -12, 12);
        TrebleDb = Math.Clamp(TrebleDb, -12, 12);
    }

    public EqualizerSettings Copy() => new()
    {
        Enabled = Enabled,
        Use31Band = Use31Band,
        PreampDb = PreampDb,
        BassDb = BassDb,
        TrebleDb = TrebleDb,
        Preset = Preset,
        CustomPresetName = CustomPresetName,
        BandGainsDb = (BandGainsDb ?? Array.Empty<float>()).ToArray()
    };

    public static EqualizerSettings CreatePreset(EqualizerPreset preset, bool use31Band = false)
    {
        var result = new EqualizerSettings { Enabled = true, Use31Band = use31Band, Preset = preset };
        var ten = preset switch
        {
            EqualizerPreset.Music => new float[] { 2, 1.5f, 0.5f, 0, -0.5f, 0, 0.5f, 1, 1.5f, 1 },
            EqualizerPreset.Cinema => new float[] { 2.5f, 2, 0.5f, -1, -0.5f, 1, 2, 1.5f, 1, 0.5f },
            EqualizerPreset.Gaming => new float[] { 1, 0, -1, -1, 0, 1, 2, 2.5f, 1.5f, 0.5f },
            EqualizerPreset.BassBoost => new float[] { 5, 4, 3, 2, 1, 0, 0, 0.5f, 0.5f, 0 },
            EqualizerPreset.PodcastVoice => new float[] { -2, -1, 0, 0.5f, 1, 2, 3, 2.5f, 1, -1 },
            EqualizerPreset.NightMode => new float[] { -3, -2, -1, 0, 1, 1, 0.5f, 0, -1, -2 },
            _ => new float[10]
        };
        result.BandGainsDb = use31Band ? ResampleGains(EqualizerBands.TenBandFrequencies, ten, EqualizerBands.ThirtyOneBandFrequencies) : ten;
        if (preset == EqualizerPreset.BassBoost) result.BassDb = 2;
        if (preset is EqualizerPreset.Cinema or EqualizerPreset.Music) result.TrebleDb = 0.5f;
        result.PreampDb = Math.Min(0, -result.BandGainsDb.Max() - Math.Max(0, result.BassDb) - Math.Max(0, result.TrebleDb));
        return result;
    }

    public static float[] ResizeGains(float[] oldGains, bool new31Band)
    {
        var old31Band = oldGains.Length == 31;
        var oldFreqs = EqualizerBands.ForMode(old31Band);
        var newFreqs = EqualizerBands.ForMode(new31Band);
        var normalized = oldGains.Length == oldFreqs.Length ? oldGains : new float[oldFreqs.Length];
        return ResampleGains(oldFreqs, normalized, newFreqs);
    }

    private static float[] ResampleGains(float[] sourceFrequencies, float[] sourceGains, float[] targetFrequencies)
    {
        var result = new float[targetFrequencies.Length];
        for (var i = 0; i < targetFrequencies.Length; i++)
        {
            var frequency = targetFrequencies[i];
            var upper = Array.FindIndex(sourceFrequencies, f => f >= frequency);
            if (upper < 0) result[i] = sourceGains[^1];
            else if (upper == 0) result[i] = sourceGains[0];
            else
            {
                var low = upper - 1;
                var amount = (MathF.Log2(frequency) - MathF.Log2(sourceFrequencies[low])) /
                    (MathF.Log2(sourceFrequencies[upper]) - MathF.Log2(sourceFrequencies[low]));
                result[i] = sourceGains[low] + (sourceGains[upper] - sourceGains[low]) * amount;
            }
        }
        return result;
    }
}
