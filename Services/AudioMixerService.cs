using NAudio.Wave;
using System.Diagnostics;
using System.Buffers;
using MultiWave.Models;

namespace MultiWave.Services;

/// <summary>
/// Converts the Windows loopback mix into one canonical float format and fans it into
/// per-endpoint buffers. WASAPI has already mixed the Windows sessions, so there is one
/// source input here and no MixingSampleProvider is needed.
/// </summary>
public sealed class AudioMixerService
{
    private readonly AudioOutputService _outputs;
    private readonly WaveFormat _sourceFormat;
    private readonly WaveFormat _normalizedSourceFormat;
    private readonly int _bytesPerSourceSample;
    private readonly EqualizerProcessor _globalEqualizer;
    private readonly SpectrumAnalyzerService? _spectrumAnalyzer;
    private long _packetsMixed;

    public WaveFormat MixerFormat { get; }
    public long PacketsMixed => Interlocked.Read(ref _packetsMixed);

    public AudioMixerService(AudioOutputService outputs, WaveFormat sourceFormat, EqualizerSettings? globalEqualizer = null, SpectrumAnalyzerService? spectrumAnalyzer = null)
    {
        _outputs = outputs;
        _globalEqualizer = new EqualizerProcessor(sourceFormat.SampleRate, sourceFormat.Channels, globalEqualizer);
        _spectrumAnalyzer = spectrumAnalyzer;
        _spectrumAnalyzer?.SetSampleRate(sourceFormat.SampleRate);
        _sourceFormat = sourceFormat;
        _normalizedSourceFormat = sourceFormat.AsStandardWaveFormat();
        _bytesPerSourceSample = _normalizedSourceFormat.BitsPerSample / 8;
        if (_normalizedSourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && _normalizedSourceFormat.BitsPerSample != 32)
            throw new NotSupportedException($"Loopback float format must be 32-bit; received {FormatDescription(_normalizedSourceFormat)}.");
        if (_normalizedSourceFormat.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
            throw new NotSupportedException($"Unsupported loopback format: {FormatDescription(_normalizedSourceFormat)}.");
        if (_normalizedSourceFormat.Encoding == WaveFormatEncoding.Pcm && _normalizedSourceFormat.BitsPerSample is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"Unsupported PCM bit depth: {FormatDescription(_normalizedSourceFormat)}.");

        MixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(_normalizedSourceFormat.SampleRate, _normalizedSourceFormat.Channels);
        Debug.WriteLine($"Audio mixer: source={FormatDescription(_sourceFormat)}; source-normalized={FormatDescription(_normalizedSourceFormat)}; mixer={FormatDescription(MixerFormat)}");
    }

    public void MixAndDistribute(byte[] capturedAudio, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
            return;

        var sampleCount = bytesRecorded / _bytesPerSourceSample;
        var samples = ArrayPool<float>.Shared.Rent(sampleCount);
        var byteCount = sampleCount * sizeof(float);
        var floatBytes = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            ConvertSamples(capturedAudio, samples, sampleCount);
            _globalEqualizer.Process(samples.AsSpan(0, sampleCount));
            _spectrumAnalyzer?.PushSamples(samples.AsSpan(0, sampleCount), MixerFormat.Channels);
            Buffer.BlockCopy(samples, 0, floatBytes, 0, byteCount);
            _outputs.Write(floatBytes, byteCount);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
            ArrayPool<byte>.Shared.Return(floatBytes);
        }
        Interlocked.Increment(ref _packetsMixed);
    }

    public void UpdateGlobalEqualizer(EqualizerSettings settings) => _globalEqualizer.Update(settings);

    private void ConvertSamples(byte[] input, float[] output, int count)
    {
        if (_normalizedSourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            Buffer.BlockCopy(input, 0, output, 0, count * sizeof(float));
            return;
        }

        switch (_normalizedSourceFormat.BitsPerSample)
        {
            case 8:
                for (var i = 0; i < count; i++)
                    output[i] = (input[i] - 128) / 128f;
                break;
            case 16:
                for (var i = 0; i < count; i++)
                    output[i] = BitConverter.ToInt16(input, i * 2) / 32768f;
                break;
            case 24:
                for (var i = 0; i < count; i++)
                {
                    var offset = i * 3;
                    var value = input[offset] | (input[offset + 1] << 8) | (input[offset + 2] << 16);
                    if ((value & 0x800000) != 0)
                        value |= unchecked((int)0xFF000000);
                    output[i] = value / 8388608f;
                }
                break;
            case 32:
                for (var i = 0; i < count; i++)
                    output[i] = BitConverter.ToInt32(input, i * 4) / 2147483648f;
                break;
            default:
                throw new NotSupportedException($"Unsupported loopback PCM format: {FormatDescription(_normalizedSourceFormat)}.");
        }
    }

    private static string FormatDescription(WaveFormat format)
    {
        var subtype = format is WaveFormatExtensible extensible ? $", subtype={extensible.SubFormat}" : "";
        return $"{format.Encoding}{subtype}, {format.Channels} ch, {format.SampleRate} Hz, {format.BitsPerSample}-bit, block={format.BlockAlign}";
    }
}
