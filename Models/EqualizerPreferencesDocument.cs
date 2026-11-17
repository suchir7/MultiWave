namespace MultiWave.Models;

public sealed class EqualizerPreferencesDocument
{
    public EqualizerSettings Global { get; set; } = EqualizerSettings.CreatePreset(EqualizerPreset.Flat);
    public Dictionary<string, EqualizerSettings> CustomPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public float MasterVolume { get; set; } = 70;
    public bool IsMuted { get; set; }
    public bool AutoSyncEnabled { get; set; } = true;
    public string? CalibrationMicrophoneId { get; set; }

    public void Normalize()
    {
        Global ??= EqualizerSettings.CreatePreset(EqualizerPreset.Flat);
        Global.Normalize();

        var presets = new Dictionary<string, EqualizerSettings>(StringComparer.OrdinalIgnoreCase);
        if (CustomPresets is not null)
        {
            foreach (var pair in CustomPresets)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    continue;

                pair.Value.Normalize();
                presets[pair.Key] = pair.Value;
            }
        }

        CustomPresets = presets;
        MasterVolume = Math.Clamp(MasterVolume, 0, 100);
    }
}
