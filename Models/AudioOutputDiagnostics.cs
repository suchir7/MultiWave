namespace MultiWave.Models;

public sealed record AudioOutputDiagnostics(
    string DeviceId,
    string DeviceName,
    int ReportedLatencyMs,
    double BufferedMilliseconds,
    double BufferCapacityMilliseconds,
    long UnderrunSamples,
    long OverflowBytes,
    int RendererCompensationMs);
