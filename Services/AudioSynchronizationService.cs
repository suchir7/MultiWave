namespace MultiWave.Services;

/// <summary>Primes independent output buffers before starting their shared playback phase.</summary>
public sealed class AudioSynchronizationService
{
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    public TimeSpan BufferPrimeTime { get; init; } = TimeSpan.FromMilliseconds(120);
    public event Action<IReadOnlyList<MultiWave.Models.AudioOutputDiagnostics>>? DiagnosticsUpdated;

    public async Task StartOutputsAsync(AudioOutputService outputs, CancellationToken cancellationToken = default)
    {
        // Let loopback fill each per-endpoint buffer before any renderer begins consuming it.
        await Task.Delay(BufferPrimeTime, cancellationToken).ConfigureAwait(false);
        outputs.StartAll();
        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = MonitorAsync(outputs, _monitorCancellation.Token);
    }

    public async Task StopMonitoringAsync()
    {
        var cancellation = _monitorCancellation;
        if (cancellation is null) return;
        _monitorCancellation = null;
        cancellation.Cancel();
        try { if (_monitorTask is not null) await _monitorTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _monitorTask = null;
            cancellation.Dispose();
        }
    }

    private async Task MonitorAsync(AudioOutputService outputs, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = outputs.GetTimingSnapshot();
            DiagnosticsUpdated?.Invoke(snapshot);
            foreach (var device in snapshot)
                System.Diagnostics.Debug.WriteLine($"Audio sync [{device.DeviceName}] id={device.DeviceId}: WASAPI latency={device.ReportedLatencyMs}ms, queue={device.BufferedMilliseconds:0.0}/{device.BufferCapacityMilliseconds:0.0}ms, underrunSamples={device.UnderrunSamples}, overflowBytes={device.OverflowBytes}, reportedRendererCompensation={device.RendererCompensationMs}ms. Bluetooth acoustic latency/clock drift are not exposed by this API.");
        }
    }
}
