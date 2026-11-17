using MultiWave.Models;

namespace MultiWave.Services;

/// <summary>
/// Duplicates the current Windows default render mix to selected non-source endpoints.
/// WASAPI loopback is a tap; it cannot replace or mute Windows' original default route.
/// </summary>
public sealed class SystemAudioDuplicationService : IAsyncDisposable
{
    private readonly AudioOutputService _outputs = new();
    private readonly AcousticCalibrationService _acousticCalibration = new();
    private readonly AudioSynchronizationService _synchronization = new();
    private readonly SpectrumAnalyzerService _spectrumAnalyzer = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private SystemAudioCaptureService? _capture;
    private AudioMixerService? _mixer;
    private EqualizerSettings _globalEqualizer = EqualizerSettings.CreatePreset(EqualizerPreset.Flat);
    private bool _autoSyncEnabled = true;
    private bool _disposed;

    public SystemAudioDuplicationService()
    {
        _outputs.OutputFailed += Outputs_OutputFailed;
        _spectrumAnalyzer.SpectrumAvailable += SpectrumAnalyzer_SpectrumAvailable;
        _synchronization.DiagnosticsUpdated += Synchronization_DiagnosticsUpdated;
    }

    public bool IsRunning { get; private set; }
    public string? CaptureSourceId => _capture?.SourceDeviceId;
    public int ActiveOutputCount => _outputs.OutputDeviceIds.Count;
    public event Action<string>? StatusChanged;
    public event Action<float[]>? SpectrumAvailable;
    public event Action<IReadOnlyList<AudioOutputDiagnostics>>? DiagnosticsUpdated;

    public async Task StartAsync(
        IEnumerable<string> selectedDeviceIds,
        string captureSourceDeviceId,
        IReadOnlyDictionary<string, float>? streamVolumes = null,
        AudioPreset preset = AudioPreset.Music,
        IReadOnlyDictionary<string, float>? delays = null,
        IReadOnlyDictionary<string, float>? bufferDurations = null,
        IReadOnlyDictionary<string, EqualizerSettings>? deviceEqualizers = null,
        bool? autoSyncEnabled = null,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync();

            var selected = selectedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (autoSyncEnabled.HasValue) _autoSyncEnabled = autoSyncEnabled.Value;
            if (selected.Length == 0)
                throw new InvalidOperationException("Select at least one active playback endpoint first.");

            var capture = new SystemAudioCaptureService(captureSourceDeviceId);
            var captureSourceName = capture.SourceDeviceName;
            try
            {
                var mixer = new AudioMixerService(_outputs, capture.WaveFormat, _globalEqualizer, _spectrumAnalyzer);
                await Task.Run(() => _outputs.PrepareOutputs(selected, capture.SourceDeviceId, mixer.MixerFormat, preset, delays, bufferDurations, deviceEqualizers, _autoSyncEnabled), cancellationToken);
                if (streamVolumes is not null)
                {
                    foreach (var pair in streamVolumes)
                        _outputs.SetStreamVolume(pair.Key, pair.Value / 100f);
                }
                _capture = capture;
                _mixer = mixer;
                _capture.AudioCaptured += Capture_AudioCaptured;
                _capture.CaptureStopped += Capture_CaptureStopped;

                if (_outputs.OutputDeviceIds.Count == 0)
                {
                    await Task.Run(_capture.Dispose);
                    _capture = null;
                    _mixer = null;
                    StatusChanged?.Invoke($"Capture source: {captureSourceName}. No other selected output endpoint is available.");
                    return;
                }

                await Task.Run(_capture.Start, cancellationToken);
                await _synchronization.StartOutputsAsync(_outputs, cancellationToken);
                IsRunning = true;
                StatusChanged?.Invoke($"Capturing from {captureSourceName}; sending audio to {_outputs.OutputDeviceIds.Count} selected endpoint(s). Route Windows and each app to a virtual capture endpoint for selected-only playback; MultiWave does not change Windows routing.");
            }
            catch
            {
                await Task.Run(capture.Dispose);
                _capture = null;
                _mixer = null;
                await Task.Run(_outputs.Reset);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    public Task SetOutputVolumeAsync(string deviceId, float volumePercent) =>
        Task.Run(() => _outputs.SetStreamVolume(deviceId, volumePercent / 100f));

    public void SetPreset(AudioPreset preset) => _outputs.SetPreset(preset);

    public void SetGlobalEqualizer(EqualizerSettings settings)
    {
        _globalEqualizer = settings.Copy();
        _mixer?.UpdateGlobalEqualizer(_globalEqualizer);
    }

    public Task SetOutputEqualizerAsync(string deviceId, EqualizerSettings settings) =>
        Task.Run(() => _outputs.SetOutputEqualizer(deviceId, settings));

    public void SetMasterVolume(float volume) => _outputs.SetMasterVolume(volume);
    public void SetMasterMuted(bool muted) => _outputs.SetMasterMuted(muted);
    public void SetAutoSyncEnabled(bool enabled)
    {
        _autoSyncEnabled = enabled;
        _outputs.SetAutoSyncEnabled(enabled);
    }

    public Task SetOutputDelayAsync(string deviceId, float milliseconds) =>
        Task.Run(() => _outputs.SetOutputDelay(deviceId, milliseconds));

    public Task PlayCalibrationToneAsync() => Task.Run(() =>
    {
        if (!IsRunning)
            throw new InvalidOperationException("Start system audio before playing the calibration tone.");
        _outputs.PlayCalibrationTone();
    });

    public async Task<AcousticCalibrationResult> CalibrateAcousticallyAsync(
        string microphoneId,
        IReadOnlyList<(string Id, string Name, float CurrentDelay)> selectedOutputs,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (!IsRunning || _capture is null)
                throw new InvalidOperationException("Start system audio before acoustic calibration.");
            _capture.AudioCaptured -= Capture_AudioCaptured;
            _outputs.ClearOutputBuffers();
            try
            {
                return await _acousticCalibration.MeasureAndAlignAsync(microphoneId, selectedOutputs, _outputs, cancellationToken);
            }
            finally
            {
                _outputs.ClearOutputBuffers();
                if (_capture is not null && IsRunning)
                    _capture.AudioCaptured += Capture_AudioCaptured;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        IsRunning = false;
        await _synchronization.StopMonitoringAsync();
        if (_capture is not null)
        {
            _capture.AudioCaptured -= Capture_AudioCaptured;
            _capture.CaptureStopped -= Capture_CaptureStopped;
            var capture = _capture;
            _capture = null;
            await Task.Run(capture.Dispose);
        }
        // Dispose capture before touching the output collection so its callback cannot race
        // an output restart while packets are being fanned out.
        await Task.Run(_outputs.StopAll);
        DiagnosticsUpdated?.Invoke([]);
        _mixer = null;
        StatusChanged?.Invoke("System audio duplication stopped.");
    }

    private void Capture_AudioCaptured(byte[] audio, int count) => _mixer?.MixAndDistribute(audio, count);

    private void SpectrumAnalyzer_SpectrumAvailable(float[] levels) => SpectrumAvailable?.Invoke(levels);
    private void Synchronization_DiagnosticsUpdated(IReadOnlyList<AudioOutputDiagnostics> snapshot) => DiagnosticsUpdated?.Invoke(snapshot);

    private void Capture_CaptureStopped(Exception? error)
    {
        if (error is not null)
        {
            IsRunning = false;
            StatusChanged?.Invoke($"Loopback capture stopped: {error.Message}");
        }
    }

    private void Outputs_OutputFailed(string deviceId, Exception error)
    {
        IsRunning = false;
        StatusChanged?.Invoke($"Playback endpoint failed ({deviceId}): {error.Message}. Refresh devices or restart audio duplication.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed)
                return;
            await StopCoreAsync();
            await Task.Run(_outputs.Dispose);
            _outputs.OutputFailed -= Outputs_OutputFailed;
            _synchronization.DiagnosticsUpdated -= Synchronization_DiagnosticsUpdated;
            _spectrumAnalyzer.SpectrumAvailable -= SpectrumAnalyzer_SpectrumAvailable;
            await _spectrumAnalyzer.DisposeAsync();
            _disposed = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
