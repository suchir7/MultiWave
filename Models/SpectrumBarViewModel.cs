using System.ComponentModel;

namespace MultiWave.Models;

public sealed class SpectrumBarViewModel(int index) : INotifyPropertyChanged
{
    private double _level;
    public int Index { get; } = index;
    public double Level { get => _level; set { if (Math.Abs(_level - value) < 0.1) return; _level = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Level))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BarHeight))); } }
    public double BarHeight => Math.Max(2, Level * 76);
    public event PropertyChangedEventHandler? PropertyChanged;
}
