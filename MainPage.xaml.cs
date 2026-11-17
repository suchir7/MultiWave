using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MultiWave.Models;
using MultiWave.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MultiWave;

public sealed partial class MainPage : Page
{
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly DevicePreferencesService _preferencesService = new();
    private readonly EqualizerPreferencesService _equalizerPreferencesService = new();
    private readonly SystemAudioDuplicationService _duplicationService = new();
    private readonly Dictionary<string, DevicePreference> _preferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _volumeUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _calibrationUpdates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _equalizerSaveUpdate;
    private EqualizerPreferencesDocument _equalizerPreferences = new();
    private bool _loadingEqualizer;
    private bool _loadingAudioPreferences;
    private bool _autoSyncEnabled = true;
    private bool _masterMuted;
    private bool _isRefreshing;
    private bool _isDisposed;
    private bool _isDuplicationRequested;
    private bool _isUpdatingCaptureSource;
    private string? _captureSourceId;
    private AudioPreset _selectedPreset = AudioPreset.Music;

    public ObservableCollection<AudioDeviceViewModel> Devices { get; } = new();
    public ObservableCollection<AudioDeviceViewModel> SelectedOutputDevices { get; } = new();
    public ObservableCollection<AudioInputDeviceViewModel> CalibrationMicrophones { get; } = new();
    public EqualizerViewModel GlobalEqualizer { get; } = new();
    public ObservableCollection<SpectrumBarViewModel> SpectrumBars { get; } = new(Enumerable.Range(0, 32).Select(index => new SpectrumBarViewModel(index)));

    public MainPage()
    {
        StartupDiagnostics.Log("MainPage: before InitializeComponent");
        InitializeComponent();
        StartupDiagnostics.Log("MainPage: after InitializeComponent");
        DeviceList.ItemsSource = Devices;
        SynchronizationList.ItemsSource = SelectedOutputDevices;
        DeviceEqualizerList.ItemsSource = SelectedOutputDevices;
        GlobalEqualizer.SettingsChanged += GlobalEqualizer_SettingsChanged;
        _audioDeviceService.DevicesChanged += AudioDeviceService_DevicesChanged;
        _duplicationService.StatusChanged += DuplicationService_StatusChanged;
        _duplicationService.SpectrumAvailable += DuplicationService_SpectrumAvailable;
        _duplicationService.DiagnosticsUpdated += DuplicationService_DiagnosticsUpdated;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        StartupDiagnostics.Log("MainPage Loaded: loading EQ preferences");
        try
        {
        _equalizerPreferences = _equalizerPreferencesService.Load();
        StartupDiagnostics.Log("MainPage Loaded: EQ preferences loaded");
        _loadingAudioPreferences = true;
        EqualizerViewModel.SetCustomPresets(_equalizerPreferences.CustomPresets);
        StartupDiagnostics.Log("MainPage Loaded: custom presets applied");
        _loadingEqualizer = true;
        GlobalEqualizer.Load(_equalizerPreferences.Global);
        _loadingEqualizer = false;
        StartupDiagnostics.Log("MainPage Loaded: global EQ applied");
        _duplicationService.SetGlobalEqualizer(GlobalEqualizer.Export());
        StartupDiagnostics.Log("MainPage Loaded: global DSP settings applied");
        _autoSyncEnabled = _equalizerPreferences.AutoSyncEnabled;
        AutoSyncToggle.IsOn = _autoSyncEnabled;
        StartupDiagnostics.Log("MainPage Loaded: Auto Sync toggle applied");
        _duplicationService.SetAutoSyncEnabled(_autoSyncEnabled);
        AutoSyncSummaryText.Text = _autoSyncEnabled
            ? "Offsets reported WASAPI latency only"
            : "Renderer latency alignment disabled";
        _masterMuted = _equalizerPreferences.IsMuted;
        MasterVolumeSlider.Value = Math.Clamp(_equalizerPreferences.MasterVolume, 0, 100);
        StartupDiagnostics.Log("MainPage Loaded: master volume applied");
        MasterVolumeText.Text = $"{MasterVolumeSlider.Value:0}%";
        MasterMuteButton.Content = _masterMuted ? "Unmute" : "Mute";
        _duplicationService.SetMasterVolume((float)MasterVolumeSlider.Value / 100);
        _duplicationService.SetMasterMuted(_masterMuted);
        _loadingAudioPreferences = false;
        UpdateResponseCurve();
        StartupDiagnostics.Log("MainPage Loaded: response curve updated");

        var saved = _preferencesService.Load();
        StartupDiagnostics.Log("MainPage Loaded: device preferences loaded");
        _preferences.Clear();
        foreach (var entry in saved)
            _preferences[entry.Key] = entry.Value;

        await RefreshDevicesAsync();
        StartupDiagnostics.Log("MainPage Loaded: playback endpoints refreshed");
        await RefreshCalibrationMicrophonesAsync();
        StartupDiagnostics.Log("MainPage Loaded: microphone endpoints refreshed");
        StartupDiagnostics.Log("MainPage Loaded: initialization complete");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"MainPage Loaded failed: {ex}");
            StatusText.Text = $"Startup setup failed: {ex.Message}";
        }
    }

    private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        DisposeServices();
        await _duplicationService.DisposeAsync();
    }

    private void AudioDeviceService_DevicesChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => _ = HandleDeviceChangeAsync());
    }

    private async Task HandleDeviceChangeAsync()
    {
        await Task.Delay(350);
        await RefreshDevicesAsync();
        if (_isDuplicationRequested)
        {
            var selectedIds = GetSelectedOutputIds();
            try { await StartEngineAsync(selectedIds); }
            catch (Exception ex)
            {
                StatusText.Text = $"Audio output restart failed: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    private void DuplicationService_StatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = status;
            SidebarEngineStatus.Text = _duplicationService.IsRunning ? "Running" : "Stopped";
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void StartDuplicationButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = GetSelectedOutputIds();
        StartDuplicationButton.IsEnabled = false;
        try
        {
            await StartEngineAsync(selectedIds);
            _isDuplicationRequested = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start audio duplication: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { StartDuplicationButton.IsEnabled = true; }
    }

    private async void StopDuplicationButton_Click(object sender, RoutedEventArgs e)
    {
        _isDuplicationRequested = false;
        await _duplicationService.StopAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_isRefreshing || _isDisposed)
            return;

        _isRefreshing = true;
        StatusText.Text = "Scanning playback endpoints…";
        try
        {
            StartupDiagnostics.Log("Playback endpoint refresh started");
            var detectedDevices = await _audioDeviceService.GetPlaybackDevicesAsync(_preferences);
            StartupDiagnostics.Log($"Playback endpoint enumeration returned {detectedDevices.Count} devices");
            foreach (var oldDevice in Devices)
            {
                oldDevice.PropertyChanged -= Device_PropertyChanged;
                oldDevice.EqualizerChanged -= Device_EqualizerChanged;
            }

            Devices.Clear();
            StartupDiagnostics.Log("Playback device collection cleared");
            foreach (var device in detectedDevices)
            {
                device.PropertyChanged += Device_PropertyChanged;
                device.EqualizerChanged += Device_EqualizerChanged;
                Devices.Add(device);
            }
            StartupDiagnostics.Log("Playback device collection populated");

            _isUpdatingCaptureSource = true;
            CaptureSourceComboBox.ItemsSource = Devices;
            StartupDiagnostics.Log("Capture source items assigned");
            if (Devices.All(device => device.DeviceId != _captureSourceId))
                _captureSourceId = await _audioDeviceService.GetDefaultPlaybackDeviceIdAsync();
            if (_captureSourceId is null || Devices.All(device => device.DeviceId != _captureSourceId))
                _captureSourceId = Devices.FirstOrDefault()?.DeviceId;
            CaptureSourceComboBox.SelectedValue = _captureSourceId;
            _isUpdatingCaptureSource = false;
            StartupDiagnostics.Log("Capture source selected");

            UpdateCounts();
            StatusText.Text = Devices.Count == 0 ? "No active playback devices" : "Connected devices updated";

            // Reapply a saved endpoint volume when that endpoint returns.
            foreach (var device in Devices)
            {
                if (_preferences.TryGetValue(device.DeviceId, out var preference) && preference.Volume.HasValue)
                    await ApplyVolumeAsync(device, preference.Volume.Value, persist: false);
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"Playback endpoint refresh failed: {ex}");
            StatusText.Text = "Device scan failed";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task RefreshCalibrationMicrophonesAsync()
    {
        try
        {
            StartupDiagnostics.Log("Microphone endpoint refresh started");
            var savedId = _equalizerPreferences.CalibrationMicrophoneId;
            var inputs = await _audioDeviceService.GetCaptureDevicesAsync();
            StartupDiagnostics.Log($"Microphone endpoint enumeration returned {inputs.Count} devices");
            CalibrationMicrophones.Clear();
            foreach (var input in inputs) CalibrationMicrophones.Add(input);
            CalibrationMicrophoneComboBox.ItemsSource = CalibrationMicrophones;
            var defaultId = await _audioDeviceService.GetDefaultCaptureDeviceIdAsync();
            var selectedId = CalibrationMicrophones.Any(input => input.DeviceId == savedId) ? savedId : defaultId;
            if (selectedId is not null && CalibrationMicrophones.Any(input => input.DeviceId == selectedId))
            {
                _isUpdatingCaptureSource = true;
                CalibrationMicrophoneComboBox.SelectedValue = selectedId;
                _isUpdatingCaptureSource = false;
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"Microphone endpoint refresh failed: {ex}");
            StatusText.Text = $"Could not enumerate microphone inputs: {ex.Message}";
        }
    }

    private void CalibrationMicrophoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCaptureSource) return;
        if (CalibrationMicrophoneComboBox.SelectedValue is not string selectedId) return;
        _equalizerPreferences.CalibrationMicrophoneId = selectedId;
        _ = DebounceEqualizerPreferencesSaveAsync();
    }

    private async void AutomaticCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        var microphoneId = CalibrationMicrophoneComboBox.SelectedValue as string;
        var selected = Devices.Where(device => device.IsSelected && !string.Equals(device.DeviceId, _captureSourceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (microphoneId is null)
        {
            StatusText.Text = "Select an active microphone for acoustic calibration.";
            return;
        }
        if (selected.Length < 2)
        {
            StatusText.Text = "Select at least two playback speakers to calibrate their relative arrival.";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Measure speaker arrival",
            Content = "MultiWave will briefly pause system-audio duplication and play a low-level swept tone through each selected speaker, one at a time. Place the selected microphone at the listening position, keep the room quiet, and ensure microphone access is allowed. The measured delays are relative to this microphone position.",
            PrimaryButtonText = "Start measurement",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        bool engineWasRunning = _duplicationService.IsRunning;
        AutomaticCalibrationButton.IsEnabled = false;
        try
        {
            if (!engineWasRunning)
                await StartEngineAsync(GetSelectedOutputIds());
            var outputs = selected.Select(device => (device.DeviceId, device.FriendlyName, (float)device.DelayMilliseconds)).ToArray();
            var result = await _duplicationService.CalibrateAcousticallyAsync(microphoneId, outputs);
            foreach (var device in selected)
            {
                if (!result.Devices.TryGetValue(device.DeviceId, out var measurement)) continue;
                device.DelayMilliseconds = measurement.AppliedDelayMilliseconds;
                var preference = GetOrCreatePreference(device.DeviceId);
                preference.DelayMilliseconds = measurement.AppliedDelayMilliseconds;
            }
            await PersistPreferencesAsync();
            foreach (var device in selected) device.HasSavedCalibration = true;
            var spread = result.Devices.Values.Max(item => item.ArrivalOffsetMilliseconds) - result.Devices.Values.Min(item => item.ArrivalOffsetMilliseconds);
            StatusText.Text = $"Acoustic measurement complete: {spread:0} ms relative arrival spread. Delay adjustments saved for {result.Devices.Count} speakers. Verify by listening at the measured microphone position.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Automatic calibration could not complete: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            if (!engineWasRunning)
                await _duplicationService.StopAsync();
            AutomaticCalibrationButton.IsEnabled = true;
        }
    }

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AudioDeviceViewModel device)
            return;

        if (e.PropertyName == nameof(AudioDeviceViewModel.IsSelected))
            UpdateCounts();

        if (e.PropertyName is nameof(AudioDeviceViewModel.IsSelected) or nameof(AudioDeviceViewModel.Volume) or nameof(AudioDeviceViewModel.StreamVolume) or nameof(AudioDeviceViewModel.DelayMilliseconds) or nameof(AudioDeviceViewModel.BufferMilliseconds))
        {
            var preference = GetOrCreatePreference(device.DeviceId);
            preference.IsSelected = device.IsSelected;
            preference.Volume = (float)device.Volume;
            preference.StreamVolume = (float)device.StreamVolume;
            preference.DelayMilliseconds = (float)device.DelayMilliseconds;
            preference.BufferMilliseconds = (float)device.BufferMilliseconds;
            if (e.PropertyName is nameof(AudioDeviceViewModel.DelayMilliseconds) or nameof(AudioDeviceViewModel.BufferMilliseconds))
                device.HasSavedCalibration = false;
            if (e.PropertyName is nameof(AudioDeviceViewModel.DelayMilliseconds) or nameof(AudioDeviceViewModel.BufferMilliseconds))
                _ = DebounceCalibrationSaveAsync(device);
            else
                _ = PersistPreferencesAsync();

            if (e.PropertyName == nameof(AudioDeviceViewModel.StreamVolume) && !device.IsDefault)
                _ = _duplicationService.SetOutputVolumeAsync(device.DeviceId, (float)device.StreamVolume);
            if (e.PropertyName == nameof(AudioDeviceViewModel.DelayMilliseconds))
                _ = _duplicationService.SetOutputDelayAsync(device.DeviceId, (float)device.DelayMilliseconds);
            if (e.PropertyName == nameof(AudioDeviceViewModel.IsSelected) && _isDuplicationRequested)
                _ = RestartEngineAsync();
        }
    }

    private void Device_EqualizerChanged(object? sender, EventArgs e)
    {
        if (sender is not AudioDeviceViewModel device) return;
        var settings = device.Equalizer.Export();
        GetOrCreatePreference(device.DeviceId).Equalizer = settings.Copy();
        _ = DebounceCalibrationSaveAsync(device);
        _ = _duplicationService.SetOutputEqualizerAsync(device.DeviceId, settings);
    }

    private void GlobalEqualizer_SettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingEqualizer) return;
        var settings = GlobalEqualizer.Export();
        _equalizerPreferences.Global = settings.Copy();
        _duplicationService.SetGlobalEqualizer(settings);
        UpdateResponseCurve();
        _ = DebounceEqualizerPreferencesSaveAsync();
    }

    private Dictionary<string, float> GetStreamVolumes() => Devices.ToDictionary(
        device => device.DeviceId,
        device => (float)device.StreamVolume,
        StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, float> GetOutputDelays() => Devices.ToDictionary(
        device => device.DeviceId,
        device => (float)device.DelayMilliseconds,
        StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, float> GetOutputBuffers() => Devices.ToDictionary(
        device => device.DeviceId,
        device => (float)device.BufferMilliseconds,
        StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, EqualizerSettings> GetOutputEqualizers() => Devices.ToDictionary(
        device => device.DeviceId,
        device => device.Equalizer.Export(),
        StringComparer.OrdinalIgnoreCase);

    private string[] GetSelectedOutputIds() => Devices
        .Where(device => device.IsSelected && !string.Equals(device.DeviceId, _captureSourceId, StringComparison.OrdinalIgnoreCase))
        .Select(device => device.DeviceId).ToArray();

    private Task StartEngineAsync(string[] selectedIds) => _duplicationService.StartAsync(
        selectedIds,
        _captureSourceId ?? throw new InvalidOperationException("Select an active capture source first."),
        GetStreamVolumes(),
        _selectedPreset,
        GetOutputDelays(),
        GetOutputBuffers(),
        GetOutputEqualizers(),
        _autoSyncEnabled);

    private void CaptureSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCaptureSource || CaptureSourceComboBox.SelectedValue is not string selected)
            return;
        _captureSourceId = selected;
        UpdateCounts();
        if (_isDuplicationRequested)
            _ = RestartEngineAsync();
    }

    private void AudioPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioPresetComboBox.SelectedItem is not ComboBoxItem item || !Enum.TryParse<AudioPreset>(item.Tag?.ToString(), out var preset))
            return;
        _selectedPreset = preset;
        _duplicationService.SetPreset(preset);
    }

    private void EqualizerPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string presetName } && Enum.TryParse<EqualizerPreset>(presetName, out var preset))
            GlobalEqualizer.SetPreset(preset);
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingAudioPreferences || e.NewValue == e.OldValue) return;
        if (MasterVolumeText is not null) MasterVolumeText.Text = $"{e.NewValue:0}%";
        _equalizerPreferences.MasterVolume = (float)e.NewValue;
        _duplicationService.SetMasterVolume((float)e.NewValue / 100);
        _ = DebounceEqualizerPreferencesSaveAsync();
    }

    private void MasterMuteButton_Click(object sender, RoutedEventArgs e)
    {
        _masterMuted = !_masterMuted;
        MasterMuteButton.Content = _masterMuted ? "Unmute" : "Mute";
        _equalizerPreferences.IsMuted = _masterMuted;
        _duplicationService.SetMasterMuted(_masterMuted);
        _ = DebounceEqualizerPreferencesSaveAsync();
    }

    private void AutoSyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingAudioPreferences) return;
        _autoSyncEnabled = AutoSyncToggle.IsOn;
        _equalizerPreferences.AutoSyncEnabled = _autoSyncEnabled;
        _duplicationService.SetAutoSyncEnabled(_autoSyncEnabled);
        if (AutoSyncSummaryText is not null)
            AutoSyncSummaryText.Text = _autoSyncEnabled
                ? "Offsets reported WASAPI latency only"
                : "Renderer latency alignment disabled";
        _ = DebounceEqualizerPreferencesSaveAsync();
    }

    private void AutoOptimizeButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalEqualizer.OptimizeHeadroom();
        foreach (var device in SelectedOutputDevices)
            device.Equalizer.OptimizeHeadroom();
        StatusText.Text = "Safe preamp headroom applied to the current global and selected-speaker EQ curves. No speaker model or acoustic measurement is inferred.";
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string destination }) return;
        foreach (var child in NavigationMenu.Children.OfType<Button>())
            child.Background = (string?)child.Tag == destination
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(70, 255, 145, 50))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        switch (destination)
        {
            case "Dashboard": MainScrollViewer.ChangeView(null, 0, null); break;
            case "Equalizer": MainFeatureTabs.SelectedIndex = 0; EqualizerSection.StartBringIntoView(); break;
            case "Effects": MainFeatureTabs.SelectedIndex = 2; EqualizerSection.StartBringIntoView(); break;
            case "Spectrum Analyzer": MainFeatureTabs.SelectedIndex = 1; EqualizerSection.StartBringIntoView(); break;
            case "Synchronization": SynchronizationSection.StartBringIntoView(); break;
            case "Settings": SettingsSection.StartBringIntoView(); break;
            case "About": AboutSection.StartBringIntoView(); break;
        }
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e) => DiagnosticsSection.StartBringIntoView();
    private void CalibrationQuickAction_Click(object sender, RoutedEventArgs e) => NavigationButton_Click(NavigationSyncButton, e);

    private void ResetAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalEqualizer.SetPreset(EqualizerPreset.Flat);
        foreach (var device in Devices)
        {
            device.Equalizer.SetPreset(EqualizerPreset.Flat);
            device.StreamVolume = 100;
            device.DelayMilliseconds = 0;
        }
        MasterVolumeSlider.Value = 100;
        _equalizerPreferences.MasterVolume = 100;
        _masterMuted = false;
        MasterMuteButton.Content = "Mute";
        _equalizerPreferences.IsMuted = false;
        _duplicationService.SetMasterMuted(false);
        StatusText.Text = "MultiWave EQ, trims, delays and master level reset. Windows endpoint volume was not changed.";
    }

    private void DuplicationService_DiagnosticsUpdated(IReadOnlyList<AudioOutputDiagnostics> snapshot)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var device in Devices)
            {
                var measurement = snapshot.FirstOrDefault(item => string.Equals(item.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase));
                if (measurement is null)
                {
                    device.RendererLatencyLabel = "WASAPI renderer latency: unavailable while stopped";
                    device.QueueLabel = "Audio queue: unavailable while stopped";
                    device.UnderrunLabel = "Short reads / overflow: unavailable while stopped";
                    device.AutoCompensationLabel = "Renderer compensation: unavailable";
                    continue;
                }
                device.RendererLatencyLabel = $"WASAPI renderer latency: {measurement.ReportedLatencyMs} ms (reported)";
                device.QueueLabel = $"Audio queue: {measurement.BufferedMilliseconds:0.0} / {measurement.BufferCapacityMilliseconds:0.0} ms";
                device.UnderrunLabel = $"Short-read samples: {measurement.UnderrunSamples:N0} · overflow bytes: {measurement.OverflowBytes:N0}";
                device.AutoCompensationLabel = _autoSyncEnabled
                    ? $"Renderer offset: {measurement.RendererCompensationMs} ms (reported latency)"
                    : "Renderer offset: disabled";
            }

            if (snapshot.Count == 0)
            {
                DiagnosticsEngineText.Text = "Audio engine stopped · no live stream measurements";
                DiagnosticsLatencyText.Text = "WASAPI latency: unavailable";
                DiagnosticsQueueText.Text = "Output queues: unavailable";
                DiagnosticsUnderrunText.Text = "Underruns / overflows: unavailable";
                return;
            }
            DiagnosticsEngineText.Text = $"Audio engine running · {snapshot.Count} selected output stream(s)";
            DiagnosticsLatencyText.Text = $"Mean reported WASAPI latency: {snapshot.Average(item => item.ReportedLatencyMs):0.0} ms";
            DiagnosticsQueueText.Text = $"Mean output queue: {snapshot.Average(item => item.BufferedMilliseconds):0.0} / {snapshot.Average(item => item.BufferCapacityMilliseconds):0.0} ms";
            DiagnosticsUnderrunText.Text = $"Short-read samples: {snapshot.Sum(item => item.UnderrunSamples):N0} · overflow bytes: {snapshot.Sum(item => item.OverflowBytes):N0}";
        });
    }

    private async Task RestartEngineAsync()
    {
        try { await StartEngineAsync(GetSelectedOutputIds()); }
        catch (Exception ex) { StatusText.Text = $"Audio restart failed: {ex.Message}"; }
    }

    private void DelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider { DataContext: AudioDeviceViewModel device } && e.NewValue != e.OldValue)
            device.DelayMilliseconds = e.NewValue;
    }

    private void BufferSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider { DataContext: AudioDeviceViewModel device } && e.NewValue != e.OldValue)
            device.BufferMilliseconds = e.NewValue;
    }

    private async void SaveCustomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "Preset name", MaxLength = 32 };
        var dialog = new ContentDialog
        {
            Title = "Save custom equalizer preset",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        if (name.Length == 0) return;

        var settings = GlobalEqualizer.Export();
        settings.Preset = EqualizerPreset.Custom;
        settings.CustomPresetName = name;
        _equalizerPreferences.CustomPresets[name] = settings.Copy();
        EqualizerViewModel.SetCustomPresets(_equalizerPreferences.CustomPresets);
        _loadingEqualizer = true;
        GlobalEqualizer.Load(settings);
        _loadingEqualizer = false;
        _equalizerPreferences.Global = settings.Copy();
        _duplicationService.SetGlobalEqualizer(settings);
        UpdateResponseCurve();
        await SaveEqualizerPreferencesAsync();
    }

    private async Task DebounceEqualizerPreferencesSaveAsync()
    {
        _equalizerSaveUpdate?.Cancel();
        _equalizerSaveUpdate?.Dispose();
        var update = new CancellationTokenSource();
        _equalizerSaveUpdate = update;
        try
        {
            await Task.Delay(250, update.Token);
            await SaveEqualizerPreferencesAsync(update.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_equalizerSaveUpdate, update)) _equalizerSaveUpdate = null;
            update.Dispose();
        }
    }

    private async Task SaveEqualizerPreferencesAsync(CancellationToken cancellationToken = default)
    {
        try { await _equalizerPreferencesService.SaveAsync(_equalizerPreferences, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save equalizer preferences";
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void DuplicationService_SpectrumAvailable(float[] levels)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var count = Math.Min(levels.Length, SpectrumBars.Count);
            for (var i = 0; i < count; i++) SpectrumBars[i].Level = levels[i];
        });
    }

    private void GlobalResponseCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponseCurve();

    private void UpdateResponseCurve()
    {
        if (GlobalResponseCanvas is null || GlobalResponseCurve is null || GlobalResponseCanvas.ActualWidth <= 1 || GlobalResponseCanvas.ActualHeight <= 1)
            return;
        var settings = GlobalEqualizer.Export();
        var width = GlobalResponseCanvas.ActualWidth;
        var height = GlobalResponseCanvas.ActualHeight;
        var points = new Microsoft.UI.Xaml.Media.PointCollection();
        const int pointCount = 96;
        for (var i = 0; i < pointCount; i++)
        {
            var portion = i / (double)(pointCount - 1);
            var frequency = (float)(20 * Math.Pow(20000d / 20, portion));
            var db = EqualizerProcessor.GetResponseDb(settings, frequency, 48000);
            var x = portion * width;
            var y = height / 2 - db / 36 * height;
            points.Add(new Windows.Foundation.Point(x, Math.Clamp(y, 0, height)));
        }
        GlobalResponseCurve.Points = points;
    }

    private async void CalibrationToneButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _duplicationService.PlayCalibrationToneAsync();
            StatusText.Text = "Calibration tone queued to selected output streams";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not play calibration tone: {ex.Message}";
        }
    }

    private void ResetDelaysButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in Devices.Where(device => device.IsSelected))
            device.DelayMilliseconds = 0;
    }

    private void DeviceVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider { DataContext: AudioDeviceViewModel device } && e.NewValue != e.OldValue)
            _ = DebounceVolumeUpdateAsync(device, e.NewValue);
    }

    private async Task DebounceVolumeUpdateAsync(AudioDeviceViewModel device, double volume)
    {
        if (_volumeUpdates.Remove(device.DeviceId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var update = new CancellationTokenSource();
        _volumeUpdates[device.DeviceId] = update;
        try
        {
            await Task.Delay(140, update.Token);
            await ApplyVolumeAsync(device, (float)volume, persist: true, update.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_volumeUpdates.TryGetValue(device.DeviceId, out var current) && ReferenceEquals(current, update))
                _volumeUpdates.Remove(device.DeviceId);
            update.Dispose();
        }
    }

    private async Task DebounceCalibrationSaveAsync(AudioDeviceViewModel device)
    {
        if (_calibrationUpdates.Remove(device.DeviceId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        var update = new CancellationTokenSource();
        _calibrationUpdates[device.DeviceId] = update;
        try
        {
            await Task.Delay(250, update.Token);
            if (_preferences.TryGetValue(device.DeviceId, out var preference))
            {
                preference.DelayMilliseconds = (float)device.DelayMilliseconds;
                preference.BufferMilliseconds = (float)device.BufferMilliseconds;
            }
            await PersistPreferencesAsync(device);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_calibrationUpdates.TryGetValue(device.DeviceId, out var current) && ReferenceEquals(current, update))
                _calibrationUpdates.Remove(device.DeviceId);
            update.Dispose();
        }
    }

    private async Task ApplyVolumeAsync(AudioDeviceViewModel device, float volume, bool persist, CancellationToken cancellationToken = default)
    {
        try
        {
            var actualVolume = await _audioDeviceService.SetVolumeAsync(device.DeviceId, volume, cancellationToken);
            device.VolumeStatus = "Endpoint volume active";
            if (Math.Abs(device.Volume - actualVolume) > 0.1)
                device.Volume = actualVolume;

            if (persist)
            {
                var preference = GetOrCreatePreference(device.DeviceId);
                preference.IsSelected = device.IsSelected;
                preference.Volume = actualVolume;
                await PersistPreferencesAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            device.VolumeStatus = $"Volume change rejected: {ex.Message}";
            StatusText.Text = $"{device.FriendlyName} rejected its volume change";
            try
            {
                device.Volume = await _audioDeviceService.GetVolumeAsync(device.DeviceId, cancellationToken);
            }
            catch (Exception readException)
            {
                device.VolumeStatus = $"Volume unavailable: {readException.Message}";
            }
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private DevicePreference GetOrCreatePreference(string deviceId)
    {
        if (!_preferences.TryGetValue(deviceId, out var preference))
        {
            preference = new DevicePreference();
            _preferences[deviceId] = preference;
        }
        return preference;
    }

    private async Task PersistPreferencesAsync(AudioDeviceViewModel? calibratedDevice = null)
    {
        try
        {
            await _preferencesService.SaveAsync(_preferences);
            if (calibratedDevice is not null)
                calibratedDevice.HasSavedCalibration = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save device preferences";
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void UpdateCounts()
    {
        var selectedCount = GetSelectedOutputIds().Length;
        SelectedOutputDevices.Clear();
        foreach (var device in Devices.Where(device => device.IsSelected && !string.Equals(device.DeviceId, _captureSourceId, StringComparison.OrdinalIgnoreCase)))
            SelectedOutputDevices.Add(device);
        CountText.Text = $"{Devices.Count} active output devices";
        SelectedCountText.Text = $"{selectedCount} selected output(s) · capture source excluded";
    }

    private void DisposeServices()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _audioDeviceService.DevicesChanged -= AudioDeviceService_DevicesChanged;
        _audioDeviceService.Dispose();
        _duplicationService.StatusChanged -= DuplicationService_StatusChanged;
        _duplicationService.SpectrumAvailable -= DuplicationService_SpectrumAvailable;
        _duplicationService.DiagnosticsUpdated -= DuplicationService_DiagnosticsUpdated;
        GlobalEqualizer.SettingsChanged -= GlobalEqualizer_SettingsChanged;
        _equalizerSaveUpdate?.Cancel();
        _equalizerSaveUpdate?.Dispose();
        _equalizerSaveUpdate = null;
        foreach (var pending in _volumeUpdates.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }
        _volumeUpdates.Clear();
        foreach (var pending in _calibrationUpdates.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }
        _calibrationUpdates.Clear();
    }
}
