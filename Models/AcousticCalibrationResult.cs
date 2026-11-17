namespace MultiWave.Models;

public sealed record AcousticCalibrationResult(IReadOnlyDictionary<string, AcousticDeviceMeasurement> Devices);

public sealed record AcousticDeviceMeasurement(
    string DeviceId,
    string DeviceName,
    double ArrivalOffsetMilliseconds,
    double Confidence,
    float AppliedDelayMilliseconds,
    double AcousticCorrectionMilliseconds);
