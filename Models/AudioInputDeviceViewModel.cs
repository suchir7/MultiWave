namespace MultiWave.Models;

public sealed class AudioInputDeviceViewModel(string friendlyName, string deviceId)
{
    public string FriendlyName { get; } = friendlyName;
    public string DeviceId { get; } = deviceId;
}
