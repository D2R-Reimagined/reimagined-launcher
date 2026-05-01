using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Process-scoped state for the CASC fastload pipeline. Decouples a long-running
/// Extract / Update / Undo / Cross-extract operation from any particular
/// <c>CascFastloadView</c> instance so navigating away from the view does NOT
/// cancel the work, and navigating back into the view re-renders the live
/// status (progress %, ETA, current file, last result).
/// </summary>
/// <remarks>
/// Concurrency model: only one operation runs at a time (enforced by
/// <see cref="IsRunning"/> + <see cref="_gate"/>). All public mutations marshal
/// to the UI thread before raising <see cref="StateChanged"/> so subscribers
/// (the view) can update controls without re-dispatching themselves.
/// </remarks>
public sealed class CascFastloadOperationState
{
    public static CascFastloadOperationState Instance { get; } = new();

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    // EWMA throughput (bytes/sec) — reset at the start of each run. Used
    // for the "current speed" display only; ETA is intentionally derived
    // from cumulative throughput so it doesn't jitter as file sizes vary.
    private double _ewmaBytesPerSec;
    private DateTime _lastProgressUtc;
    private long _lastBytesDone;

    // ETA stabilization: refresh the visible ETA text at most once per
    // second so the user sees a steady value instead of a value that
    // flashes at the 10 Hz progress emission rate.
    private DateTime _lastEtaRefreshUtc;
    private string _lastEtaText = string.Empty;

    private CascFastloadOperationState() { }

    public bool IsRunning => _running;
    public string OperationLabel { get; private set; } = string.Empty;
    public string StatusMessage { get; private set; } = string.Empty;
    public string ProgressDetail { get; private set; } = string.Empty;
    public string ProgressEta { get; private set; } = string.Empty;
    public string CurrentFile { get; private set; } = string.Empty;
    public double ProgressPercent { get; private set; }
    public string LastResultMessage { get; private set; } = string.Empty;

    /// <summary>
    /// Fires on the UI thread whenever any observable state changes. The view
    /// subscribes on <c>Loaded</c> and unsubscribes on <c>DetachedFromVisualTree</c>.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Attempts to start <paramref name="body"/> as the single in-flight
    /// operation. Returns <c>false</c> if another operation is already running.
    /// The body receives a cancellation token (driven by <see cref="Cancel"/>),
    /// a progress sink, and a callback for setting the visible status string.
    /// </summary>
    public async Task<bool> TryRunAsync(
        string label,
        Func<CancellationToken, IProgress<CascProgress>, Action<string>, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        lock (_gate)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _cts = new CancellationTokenSource();
        }

        LaunchDiagnostics.Log($"CASC operation '{label}': starting (managed thread {Environment.CurrentManagedThreadId}).");

        OperationLabel = label;
        StatusMessage = $"{label}: starting...";
        ProgressDetail = string.Empty;
        ProgressEta = string.Empty;
        CurrentFile = string.Empty;
        ProgressPercent = 0;
        LastResultMessage = string.Empty;
        ResetEwma();
        Raise();

        var token = _cts!.Token;
        var progress = new Progress<CascProgress>(OnProgress);
        Action<string> setStatus = SetStatus;

        try
        {
            await body(token, progress, setStatus).ConfigureAwait(false);
            if (string.IsNullOrEmpty(LastResultMessage))
            {
                // Body completed without setting a final message — surface the
                // final status as the persistent "last result" line.
                LastResultMessage = StatusMessage;
            }
            LaunchDiagnostics.Log($"CASC operation '{label}': completed normally.");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{label} cancelled.";
            LastResultMessage = StatusMessage;
            LaunchDiagnostics.Log($"CASC operation '{label}': cancelled.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label} failed: {ex.Message}";
            LastResultMessage = StatusMessage;
            LaunchDiagnostics.LogException($"CASC operation '{label}': faulted", ex);
            Notifications.SendNotification($"CASC {label} failed: {ex.Message}", "Error");
        }
        finally
        {
            lock (_gate)
            {
                _cts?.Dispose();
                _cts = null;
                _running = false;
            }

            Raise();
        }

        return true;
    }

    /// <summary>
    /// Requests cooperative cancellation of the in-flight operation, if any.
    /// </summary>
    public void Cancel()
    {
        try
        {
            CancellationTokenSource? cts;
            bool running;
            lock (_gate)
            {
                cts = _cts;
                running = _running;
            }

            LaunchDiagnostics.Log(
                $"CASC Cancel requested. running={running}, ctsAlive={cts is not null}, alreadyCancelled={cts?.IsCancellationRequested ?? false}, label='{OperationLabel}', lastStatus='{StatusMessage}', lastFile='{CurrentFile}'.");

            cts?.Cancel();
            SetStatus("Cancelling... (waiting for current native CASC call to return)");
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("CASC Cancel: faulted while requesting cancellation", ex);
        }
    }

    /// <summary>
    /// Updates the visible status string (used by operation bodies for
    /// "Indexing CASC...", "Applying delta: X added, Y updated...", etc.).
    /// </summary>
    public void SetStatus(string message)
    {
        StatusMessage = message ?? string.Empty;
        Raise();
    }

    /// <summary>
    /// Updates the visible "current file" strip. Used during the indexing
    /// phase (where there is no extract-progress yet) so the UI has a live
    /// heartbeat instead of appearing hung while CASC is being walked.
    /// </summary>
    public void SetCurrentFile(string? path)
    {
        CurrentFile = path ?? string.Empty;
        Raise();
    }

    /// <summary>
    /// Records a final result line that survives across view rebinds.
    /// </summary>
    public void SetResult(string message)
    {
        LastResultMessage = message ?? string.Empty;
        StatusMessage = LastResultMessage;
        Raise();
    }

    private void OnProgress(CascProgress p)
    {
        var nowUtc = DateTime.UtcNow;
        var deltaSec = (nowUtc - _lastProgressUtc).TotalSeconds;
        var deltaBytes = p.BytesDone - _lastBytesDone;

        // EWMA over the inter-callback rate — used only for the "current
        // speed" readout. Tolerant of jitter; the smoothing factor is low
        // enough to react to genuine slow-downs but high enough to avoid
        // per-file thrash. Skip samples with no byte progress (large file
        // still in flight) so the displayed speed doesn't decay to zero.
        if (_lastProgressUtc != default && deltaSec > 0 && deltaBytes > 0)
        {
            var instant = deltaBytes / deltaSec;
            _ewmaBytesPerSec = _ewmaBytesPerSec <= 0
                ? instant
                : (0.2 * instant) + (0.8 * _ewmaBytesPerSec);
        }

        _lastProgressUtc = nowUtc;
        _lastBytesDone = p.BytesDone;

        ProgressPercent = p.BytesTotal > 0
            ? Math.Clamp(p.BytesDone * 100.0 / p.BytesTotal, 0.0, 100.0)
            : 0.0;

        ProgressDetail =
            $"{p.FilesDone:N0} / {p.FilesTotal:N0} files • {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)} • {FormatBytes((long)_ewmaBytesPerSec)}/s";

        // ETA: use the cumulative average throughput (bytesDone / elapsed)
        // — much steadier than an instantaneous rate over varying file
        // sizes. Only refresh the visible text at most once per second
        // and only after a brief warm-up so the value doesn't appear and
        // disappear at the 10 Hz progress cadence.
        ProgressEta = ResolveEtaText(p, nowUtc);

        CurrentFile = p.CurrentPath ?? CurrentFile;

        Raise();
    }

    private string ResolveEtaText(CascProgress p, DateTime nowUtc)
    {
        // Warm-up: avoid showing an ETA based on too little data.
        if (p.Elapsed.TotalSeconds < 2.0 || p.BytesDone <= 0 || p.BytesTotal <= p.BytesDone)
        {
            return _lastEtaText;
        }

        // Throttle text refresh to ~1 Hz; reuse the prior text otherwise.
        if (_lastEtaRefreshUtc != default && (nowUtc - _lastEtaRefreshUtc).TotalMilliseconds < 1000)
        {
            return _lastEtaText;
        }

        var avgBytesPerSec = p.BytesDone / p.Elapsed.TotalSeconds;
        var text = ComputeEtaText(p, avgBytesPerSec);
        if (string.IsNullOrEmpty(text))
        {
            // Don't blank a previously-good ETA on a transient sample.
            return _lastEtaText;
        }

        _lastEtaText = text;
        _lastEtaRefreshUtc = nowUtc;
        return text;
    }

    private void ResetEwma()
    {
        _ewmaBytesPerSec = 0;
        _lastProgressUtc = default;
        _lastBytesDone = 0;
        _lastEtaRefreshUtc = default;
        _lastEtaText = string.Empty;
    }

    private void Raise()
    {
        // Always marshal to the UI thread so subscribers (Avalonia controls)
        // can update without re-dispatching. The Post is a no-op if no one
        // is currently subscribed.
        if (Dispatcher.UIThread.CheckAccess())
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => StateChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    internal static string ComputeEtaText(CascProgress p, double ewmaBytesPerSec)
    {
        if (ewmaBytesPerSec <= 0 || p.BytesTotal <= p.BytesDone)
        {
            return string.Empty;
        }

        var remaining = p.BytesTotal - p.BytesDone;
        var seconds = remaining / ewmaBytesPerSec;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return string.Empty;
        }

        var eta = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds));
        return $"ETA {FormatElapsed(eta)}";
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        const double KiB = 1024d;
        const double MiB = 1024d * 1024d;
        const double GiB = 1024d * 1024d * 1024d;

        if (bytes >= GiB) return $"{bytes / GiB:F2} GiB";
        if (bytes >= MiB) return $"{bytes / MiB:F1} MiB";
        if (bytes >= KiB) return $"{bytes / KiB:F0} KiB";
        return $"{bytes} B";
    }

    internal static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
