using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiWave.Models;

public sealed class EqualizerBandViewModel(float frequency, float gainDb) : INotifyPropertyChanged
{
    private double _gainDb = gainDb;
    public float Frequency { get; } = frequency;
    public string FrequencyLabel => Frequency >= 1000 ? $"{Frequency / 1000:0.#}k" : $"{Frequency:0}";
    public double GainDb
    {
        get => _gainDb;
        set
        {
            var constrained = Math.Clamp(value, -12, 12);
            if (Math.Abs(_gainDb - constrained) < 0.01) return;
            _gainDb = constrained;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainDb)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainBarHeight)));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string GainLabel => $"{GainDb:+0.0;-0.0;0} dB";
    public double GainBarHeight => Math.Clamp((GainDb + 12) / 24 * 68, 2, 68);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;

    internal void LoadGain(double gainDb)
    {
        var constrained = Math.Clamp(gainDb, -12, 12);
        if (Math.Abs(_gainDb - constrained) < 0.01) return;
        _gainDb = constrained;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainDb)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainBarHeight)));
    }
}
