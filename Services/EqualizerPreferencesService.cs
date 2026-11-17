using System.Text.Json;
using MultiWave.Models;

namespace MultiWave.Services;

public sealed class EqualizerPreferencesService
{
    private readonly string _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiWave", "equalizer-preferences.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EqualizerPreferencesDocument Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new();
            var document = JsonSerializer.Deserialize<EqualizerPreferencesDocument>(File.ReadAllText(_filePath)) ?? new();
            document.Normalize();
            return document;
        }
        catch (JsonException ex)
        {
            StartupDiagnostics.Log($"EQ preferences JSON was invalid; using defaults: {ex}");
            return new();
        }
        catch (IOException ex)
        {
            StartupDiagnostics.Log($"EQ preferences could not be read; using defaults: {ex}");
            return new();
        }
    }

    public async Task<EqualizerPreferencesDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try { return await Task.Run(Load, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(EqualizerPreferencesDocument document, CancellationToken cancellationToken = default)
    {
        document.Normalize();
        var snapshot = new EqualizerPreferencesDocument
        {
            Global = document.Global.Copy(),
            CustomPresets = document.CustomPresets.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.OrdinalIgnoreCase),
            MasterVolume = document.MasterVolume,
            IsMuted = document.IsMuted,
            AutoSyncEnabled = document.AutoSyncEnabled,
            CalibrationMicrophoneId = document.CalibrationMicrophoneId
        };
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
        finally { _gate.Release(); }
    }
}
