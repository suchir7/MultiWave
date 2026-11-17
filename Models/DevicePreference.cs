namespace MultiWave.Models;

public sealed class DevicePreference
{
    public bool IsSelected { get; set; }
    public float? Volume { get; set; }
    public float? StreamVolume { get; set; }
    public float? DelayMilliseconds { get; set; }
    public float? BufferMilliseconds { get; set; }
    public EqualizerSettings? Equalizer { get; set; }
}
