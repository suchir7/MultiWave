using System.Text.Json;
using MultiWave.Models;

namespace MultiWave.Services;

/// <summary>Stores selected endpoint IDs and volume levels in the user's local application data.</summary>
public sealed class DevicePreferencesService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiWave",
        "device-preferences.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Dictionary<string, DevicePreference> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, DevicePreference>>(File.ReadAllText(_filePath))
                ?? new Dictionary<string, DevicePreference>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            StartupDiagnostics.Log($"Device preferences JSON was invalid; using defaults: {ex}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException ex)
        {
            StartupDiagnostics.Log($"Device preferences could not be read; using defaults: {ex}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<Dictionary<string, DevicePreference>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(Load, cancellationToken);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, DevicePreference> preferences, CancellationToken cancellationToken = default)
    {
        var snapshot = preferences.ToDictionary(
            item => item.Key,
            item => new DevicePreference { IsSelected = item.Value.IsSelected, Volume = item.Value.Volume, StreamVolume = item.Value.StreamVolume, DelayMilliseconds = item.Value.DelayMilliseconds, BufferMilliseconds = item.Value.BufferMilliseconds, Equalizer = item.Value.Equalizer?.Copy() },
            StringComparer.OrdinalIgnoreCase);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken);

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
