using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameTrail.Media;

/// <summary>Serial, reusable high-resolution deadline waiter for playback frames. Dispose cancels an active wait and closes the timer after that wait cleans up.</summary>
public sealed class PlaybackFrameClock : IDisposable, IAsyncDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;
    private readonly TimerWaitHandle _timer;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycle = new();
    private bool _disposed;
    private bool _waitActive;

    public bool IsHighResolution { get; }

    public PlaybackFrameClock()
    {
        var access = TimerModifyState | Synchronize;
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, access);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, access);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Unable to create a waitable timer.");
            }
            IsHighResolution = false;
        }
        else IsHighResolution = true;

        _timer = new TimerWaitHandle(handle);
    }

    /// <summary>Returns a Stopwatch timestamp deadline after the supplied duration.</summary>
    public static long DeadlineAfter(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var delta = duration.TotalSeconds * Stopwatch.Frequency;
        if (delta >= long.MaxValue - Stopwatch.GetTimestamp()) return long.MaxValue;
        return Stopwatch.GetTimestamp() + (long)Math.Ceiling(delta);
    }

    /// <summary>Waits until a deadline produced by Stopwatch or DeadlineAfter. Calls must be serial.</summary>
    public async ValueTask WaitUntilAsync(long deadlineTimestamp, CancellationToken cancellationToken = default)
    {
        CancellationToken shutdownToken;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            shutdownToken = _shutdown.Token;
        }
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
        var token = linkedCancellation.Token;
        await _serial.WaitAsync(token).ConfigureAwait(false);
        lock (_lifecycle)
        {
            if (_disposed)
            {
                _serial.Release();
                throw new OperationCanceledException("The frame clock was disposed.", token);
            }
            _waitActive = true;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return;

            // SetWaitableTimer uses a negative relative due time in 100 ns units.
            var due100ns = Math.Max(1L, (long)Math.Ceiling(remainingTicks * (10_000_000d / Stopwatch.Frequency)));
            if (!ArmTimer(_timer.SafeWaitHandle, -due100ns))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to arm the waitable timer.");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RegisteredWaitHandle? registration = null;
            using var cancellation = token.Register(() =>
            {
                CancelWaitableTimer(_timer.SafeWaitHandle);
                completion.TrySetCanceled(token);
            });
            registration = ThreadPool.RegisterWaitForSingleObject(_timer, static (state, timedOut) =>
            {
                var tcs = (TaskCompletionSource)state!;
                if (!timedOut) tcs.TrySetResult();
            }, completion, Timeout.InfiniteTimeSpan, executeOnlyOnce: true);
            try { await completion.Task.ConfigureAwait(false); }
            finally
            {
                registration.Unregister(null);
                CancelWaitableTimer(_timer.SafeWaitHandle);
            }
        }
        finally
        {
            lock (_lifecycle)
            {
                _waitActive = false;
                if (_disposed) _timer.Dispose();
            }
            _serial.Release();
        }
    }

    /// <summary>Cancels active and queued waits. An active wait completes as canceled before its timer handle is closed.</summary>
    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_waitActive) _timer.Dispose();
        }
        _shutdown.Cancel();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private static unsafe bool ArmTimer(SafeWaitHandle timer, long dueTime) =>
        SetWaitableTimer(timer, &dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool SetWaitableTimer(SafeWaitHandle timer, long* dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
