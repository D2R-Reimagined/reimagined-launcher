using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

internal sealed class LauncherInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _activationGate = new();
    private readonly Task? _listener;
    private Action? _activate;
    private bool _pendingActivation;
    internal bool IsPrimary { get; }

    internal static string DefaultIdentity => $"{SettingsManager.AppDirectoryPath}|{Environment.UserName}|{(LauncherUpdateService.IsPreviewBuild ? "preview" : "release")}";

    internal LauncherInstance(string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        _pipeName = "D2RReimagined-" + hash;
        _mutex = new Mutex(false, (OperatingSystem.IsWindows() ? "Local\\" : "") + _pipeName);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (IsPrimary) _listener = ListenAsync();
    }

    internal void SetActivationHandler(Action activate)
    {
        bool pending;
        lock (_activationGate)
        {
            _activate = activate;
            pending = _pendingActivation;
            _pendingActivation = false;
        }
        if (pending) activate();
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(3));
                await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), request.Token).ConfigureAwait(false);
                var command = new byte[1];
                await pipe.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                if (command[0] != 1) continue;
                Action? activate;
                lock (_activationGate)
                {
                    activate = _activate;
                    if (activate is null) _pendingActivation = true;
                }
                activate?.Invoke();
                await pipe.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException exception) { Debug.WriteLine(exception); }
        }
    }

    internal async Task<bool> ActivateExistingAsync(TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource(timeout);
        while (!limit.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
                var processId = new byte[4];
                await pipe.ReadExactlyAsync(processId, limit.Token).ConfigureAwait(false);
                if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(BitConverter.ToInt32(processId));
                await pipe.WriteAsync(new byte[] { 1 }, limit.Token).ConfigureAwait(false);
                var reply = new byte[1];
                await pipe.ReadExactlyAsync(reply, limit.Token).ConfigureAwait(false);
                return reply[0] == 1;
            }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { }
            try { await Task.Delay(50, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.GetAwaiter().GetResult();
        _stop.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
