namespace MultiWave.Models;

public sealed class EqualizerPresetOption(string key, string name)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
}
