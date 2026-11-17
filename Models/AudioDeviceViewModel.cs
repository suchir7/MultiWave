using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiWave.Models;

public sealed class AudioDeviceViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private double _volume;
    private double _streamVolume = 100;
    private string _volumeStatus = "Ready";
    private double _delayMilliseconds;
    private bool _hasSavedCalibration;
    private double _bufferMilliseconds = 250;
    private EqualizerViewModel _equalizer = new();
    private string _rendererLatencyLabel = "WASAPI latency: unavailable while stopped";
    private string _queueLabel = "Queue: unavailable while stopped";
    private string _underrunLabel = "Short reads: unavailable while stopped";
    private string _autoCompensationLabel = "Renderer compensation: unavailable";

    public string FriendlyName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string CapabilitiesLabel { get; set; } = "";
    public string StateLabel { get; set; } = "Active";
    public string DefaultLabel { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool CanSetStreamVolume { get; set; } = true;
    public EqualizerViewModel Equalizer
    {
        get => _equalizer;
        set
        {
            _equalizer.SettingsChanged -= Equalizer_SettingsChanged;
            _equalizer = value;
            _equalizer.SettingsChanged += Equalizer_SettingsChanged;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Equalizer)));
        }
    }
    public string RendererLatencyLabel { get => _rendererLatencyLabel; set => SetField(ref _rendererLatencyLabel, value); }
    public string QueueLabel { get => _queueLabel; set => SetField(ref _queueLabel, value); }
    public string UnderrunLabel { get => _underrunLabel; set => SetField(ref _underrunLabel, value); }
    public string AutoCompensationLabel { get => _autoCompensationLabel; set => SetField(ref _autoCompensationLabel, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public double Volume
    {
        get => _volume;
        set => SetField(ref _volume, Math.Clamp(value, 0, 100));
    }

    public double StreamVolume
    {
        get => _streamVolume;
        set => SetField(ref _streamVolume, Math.Clamp(value, 0, 100));
    }

    public string VolumeStatus
    {
        get => _volumeStatus;
        set => SetField(ref _volumeStatus, value);
    }

    public double DelayMilliseconds
    {
        get => _delayMilliseconds;
        set => SetField(ref _delayMilliseconds, Math.Clamp(value, 0, 500));
    }

    public bool HasSavedCalibration
    {
        get => _hasSavedCalibration;
        set
        {
            if (SetField(ref _hasSavedCalibration, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CalibrationLabel)));
        }
    }

    public string CalibrationLabel => HasSavedCalibration ? "SAVED" : "UNSAVED";

    public double BufferMilliseconds
    {
        get => _bufferMilliseconds;
        set => SetField(ref _bufferMilliseconds, Math.Clamp(value, 80, 500));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? EqualizerChanged;

    private void Equalizer_SettingsChanged(object? sender, EventArgs e) => EqualizerChanged?.Invoke(this, EventArgs.Empty);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
