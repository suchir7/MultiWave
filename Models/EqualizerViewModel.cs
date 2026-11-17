using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiWave.Models;

public sealed class EqualizerViewModel : INotifyPropertyChanged
{
    private static readonly Dictionary<string, EqualizerSettings> CustomPresetSettings = new(StringComparer.OrdinalIgnoreCase);
    public static ObservableCollection<EqualizerPresetOption> SharedPresetOptions { get; } =
    [
        new("Flat", "Flat"), new("Music", "Music"), new("Cinema", "Cinema"), new("Gaming", "Gaming"),
        new("BassBoost", "Bass Boost"), new("PodcastVoice", "Podcast & Voice"), new("NightMode", "Night Mode"), new("Custom", "Custom")
    ];

    private bool _enabled;
    private bool _use31Band;
    private double _preampDb;
    private double _bassDb;
    private double _trebleDb;
    private EqualizerPreset _preset = EqualizerPreset.Flat;
    private string? _customPresetName;
    private readonly List<EqualizerBandViewModel> _bandSubscriptions = [];

    public ObservableCollection<EqualizerBandViewModel> Bands { get; } = [];
    public ObservableCollection<EqualizerPresetOption> PresetOptions => SharedPresetOptions;
    public bool Enabled { get => _enabled; set { if (SetField(ref _enabled, value)) UserSettingsChanged(); } }
    public bool Use31Band
    {
        get => _use31Band;
        set
        {
            if (_use31Band == value) return;
            var previousGains = Bands.Select(band => (float)band.GainDb).ToArray();
            _use31Band = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Use31Band)));
            RebuildBands(previousGains);
            UserSettingsChanged();
        }
    }
    public string BandModeKey { get => Use31Band ? "31" : "10"; set => Use31Band = value == "31"; }
    public double PreampDb { get => _preampDb; set { if (SetField(ref _preampDb, Math.Clamp(value, -12, 12))) UserSettingsChanged(); } }
    public double BassDb { get => _bassDb; set { if (SetField(ref _bassDb, Math.Clamp(value, -12, 12))) UserSettingsChanged(); } }
    public double TrebleDb { get => _trebleDb; set { if (SetField(ref _trebleDb, Math.Clamp(value, -12, 12))) UserSettingsChanged(); } }
    public EqualizerPreset Preset { get => _preset; private set => SetField(ref _preset, value); }
    public string SelectedPresetKey
    {
        get => Preset == EqualizerPreset.Custom && _customPresetName is not null ? $"custom:{_customPresetName}" : Preset.ToString();
        set
        {
            if (string.Equals(value, SelectedPresetKey, StringComparison.Ordinal))
                return;

            if (value.StartsWith("custom:", StringComparison.Ordinal) && CustomPresetSettings.TryGetValue(value[7..], out var custom))
                Load(custom);
            else if (Enum.TryParse<EqualizerPreset>(value, out var preset))
                SetPreset(preset);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SettingsChanged;

    public EqualizerViewModel(EqualizerSettings? settings = null)
    {
        Load(settings ?? EqualizerSettings.CreatePreset(EqualizerPreset.Flat));
    }

    public void SetPreset(EqualizerPreset preset)
    {
        if (preset == EqualizerPreset.Custom)
        {
            _customPresetName = null;
            Preset = preset;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPresetKey)));
            NotifySettingsChanged();
            return;
        }
        Load(EqualizerSettings.CreatePreset(preset, Use31Band));
    }

    public void OptimizeHeadroom()
    {
        var settings = Export();
        var maximumBandBoost = settings.BandGainsDb.Length == 0 ? 0 : settings.BandGainsDb.Max();
        var conservativePeakBoost = maximumBandBoost + Math.Max(0, settings.BassDb) + Math.Max(0, settings.TrebleDb);
        PreampDb = Math.Clamp(-conservativePeakBoost, -12, 0);
    }

    public void Load(EqualizerSettings settings)
    {
        settings.Normalize();
        _enabled = settings.Enabled;
        _use31Band = settings.Use31Band;
        _preampDb = settings.PreampDb;
        _bassDb = settings.BassDb;
        _trebleDb = settings.TrebleDb;
        _preset = settings.Preset;
        _customPresetName = settings.CustomPresetName;
        RebuildBands(settings.BandGainsDb);
        // WinUI compiled TwoWay bindings can recursively re-evaluate when given a null
        // property name while an ItemsControl's band collection is being rebuilt. Notify
        // each scalar binding explicitly after the collection is stable.
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Use31Band));
        OnPropertyChanged(nameof(BandModeKey));
        OnPropertyChanged(nameof(PreampDb));
        OnPropertyChanged(nameof(BassDb));
        OnPropertyChanged(nameof(TrebleDb));
        OnPropertyChanged(nameof(Preset));
        OnPropertyChanged(nameof(SelectedPresetKey));
        NotifySettingsChanged();
    }

    public EqualizerSettings Export()
    {
        var gains = Bands.Select(band => (float)band.GainDb).ToArray();
        return new EqualizerSettings
        {
            Enabled = Enabled,
            Use31Band = Use31Band,
            PreampDb = (float)PreampDb,
            BassDb = (float)BassDb,
            TrebleDb = (float)TrebleDb,
            Preset = Preset,
            CustomPresetName = _customPresetName,
            BandGainsDb = gains
        };
    }

    public static void SetCustomPresets(IReadOnlyDictionary<string, EqualizerSettings> presets)
    {
        CustomPresetSettings.Clear();
        foreach (var item in presets)
            CustomPresetSettings[item.Key] = item.Value.Copy();
        for (var i = SharedPresetOptions.Count - 1; i >= 0; i--)
            if (SharedPresetOptions[i].Key.StartsWith("custom:", StringComparison.Ordinal))
                SharedPresetOptions.RemoveAt(i);
        foreach (var name in CustomPresetSettings.Keys.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            SharedPresetOptions.Add(new EqualizerPresetOption($"custom:{name}", name));
    }

    private void RebuildBands(float[]? existing = null)
    {
        var frequencies = EqualizerBands.ForMode(Use31Band);
        var gains = existing is null ? new float[frequencies.Length] :
            existing.Length == frequencies.Length ? existing : EqualizerSettings.ResizeGains(existing, Use31Band);

        // Loading a saved curve with the same band count is common at startup. Update those
        // existing view models in place instead of resetting the bound ItemsControl collection.
        if (Bands.Count == frequencies.Length && Bands.Select((band, index) => band.Frequency == frequencies[index]).All(matches => matches))
        {
            for (var i = 0; i < Bands.Count; i++) Bands[i].LoadGain(gains[i]);
            return;
        }

        foreach (var band in _bandSubscriptions) band.Changed -= Band_Changed;
        _bandSubscriptions.Clear();
        Bands.Clear();
        for (var i = 0; i < frequencies.Length; i++)
        {
            var band = new EqualizerBandViewModel(frequencies[i], gains[i]);
            band.Changed += Band_Changed;
            _bandSubscriptions.Add(band);
            Bands.Add(band);
        }
    }

    private void Band_Changed(object? sender, EventArgs e) => UserSettingsChanged();
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private void NotifySettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    private void UserSettingsChanged()
    {
        if (Preset != EqualizerPreset.Custom || _customPresetName is not null)
        {
            Preset = EqualizerPreset.Custom;
            _customPresetName = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPresetKey)));
        }
        NotifySettingsChanged();
    }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        return true;
    }
}
