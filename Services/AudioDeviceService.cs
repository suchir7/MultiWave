using NAudio.CoreAudioApi;
using MultiWave.Models;
using System.Runtime.InteropServices;

namespace MultiWave.Services;

/// <summary>Enumerates active render endpoints, watches Core Audio changes, and controls endpoint volume.</summary>
public sealed class AudioDeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _notificationEnumerator = new();
    private readonly MMDeviceNotificationClient? _notificationClient;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public AudioDeviceService()
    {
        _notificationClient = null;
    }

    public Task<List<AudioDeviceViewModel>> GetPlaybackDevicesAsync(
        IReadOnlyDictionary<string, DevicePreference> preferences,
        CancellationToken cancellationToken = default)
    {
        var devices = new List<AudioDeviceViewModel>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var enumerator = new MMDeviceEnumerator();

        string? defaultDeviceId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultDeviceId = defaultDevice.ID;
        }
        catch
        {
            // Windows may not have a default playback device.
        }

            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var id = endpoint.ID;
                var hasPreference = preferences.TryGetValue(id, out var preference);
                var volume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                var isDefault = string.Equals(id, defaultDeviceId, StringComparison.OrdinalIgnoreCase);
                string capabilities = "Endpoint format unavailable";
                try
                {
                    using var audioClient = endpoint.CreateAudioClient();
                    var mixFormat = audioClient.MixFormat;
                    capabilities = $"{mixFormat.Channels} ch · {mixFormat.SampleRate} Hz · {mixFormat.BitsPerSample}-bit endpoint mix";
                }
                catch (COMException)
                {
                    // Some virtual or restricted endpoints do not expose an audio client.
                }

                devices.Add(new AudioDeviceViewModel
                {
                    FriendlyName = endpoint.FriendlyName,
                    DeviceId = id,
                    CapabilitiesLabel = capabilities,
                    StateLabel = endpoint.State.ToString(),
                    IsDefault = isDefault,
                    CanSetStreamVolume = !isDefault,
                    DefaultLabel = isDefault ? "DEFAULT" : "",
                    IsSelected = hasPreference && preference!.IsSelected,
                    Volume = hasPreference && preference!.Volume.HasValue ? preference.Volume.Value : volume,
                    StreamVolume = hasPreference && preference!.StreamVolume.HasValue ? preference.StreamVolume.Value : 100,
                    DelayMilliseconds = hasPreference && preference!.DelayMilliseconds.HasValue ? Math.Clamp(preference.DelayMilliseconds.Value, 0, 500) : 0,
                    HasSavedCalibration = hasPreference && preference!.DelayMilliseconds.HasValue,
                    BufferMilliseconds = hasPreference && preference!.BufferMilliseconds.HasValue ? Math.Clamp(preference.BufferMilliseconds.Value, 80, 500) : 250,
                    Equalizer = new EqualizerViewModel(hasPreference ? preference!.Equalizer : null),
                    VolumeStatus = isDefault ? "Windows source; native playback" : "Ready"
                });
            }
            finally
            {
                endpoint.Dispose();
            }
            }
        }
        catch (COMException ex)
        {
            StartupDiagnostics.Log($"Playback endpoint enumeration unavailable; continuing without endpoints: {ex}");
        }

        return Task.FromResult(devices);
    }

    public Task<string?> GetDefaultPlaybackDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Task.FromResult<string?>(endpoint.ID);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<List<AudioInputDeviceViewModel>> GetCaptureDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device =>
            {
                try { return new AudioInputDeviceViewModel(device.FriendlyName, device.ID); }
                finally { device.Dispose(); }
            }).ToList();
        return Task.FromResult(devices);
    }

    public Task<string?> GetDefaultCaptureDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        try { using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); return Task.FromResult<string?>(endpoint.ID); }
        catch { return Task.FromResult<string?>(null); }
    }

    public Task<float> SetVolumeAsync(string deviceId, float volumePercent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(deviceId);
        endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volumePercent / 100f, 0f, 1f);
        return Task.FromResult(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar * 100f);
    }

    public Task<float> GetVolumeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(deviceId);
        return Task.FromResult(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar * 100f);
    }

    private void OnDeviceChanged(object? sender, DeviceNotificationEventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);
    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);
    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow == DataFlow.Render)
            DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notificationClient?.Dispose();
        _notificationEnumerator.Dispose();
    }
}
