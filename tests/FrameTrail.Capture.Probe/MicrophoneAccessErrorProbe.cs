using System.Runtime.InteropServices;
using FrameTrail.Capture;

internal static class MicrophoneAccessErrorProbe
{
    private const int EAccessDenied = unchecked((int)0x80070005);

    internal static void Run()
    {
        var comFailure = new COMException("WASAPI denied microphone access.", EAccessDenied);
        var mappedCom = ExpectMapped(() => MicrophoneAccessDeniedMapper.Invoke(true, () => { throw comFailure; }));
        if (!ReferenceEquals(mappedCom.InnerException, comFailure))
            throw new InvalidOperationException("Mapped COM failure did not preserve its inner exception.");

        var unauthorizedFailure = new UnauthorizedAccessException("Microphone access denied.");
        if (unauthorizedFailure.HResult != EAccessDenied)
            throw new InvalidOperationException("UnauthorizedAccessException did not carry the expected E_ACCESSDENIED HRESULT.");
        var mappedUnauthorized = ExpectMapped(() => MicrophoneAccessDeniedMapper.Invoke(true, () => { throw unauthorizedFailure; }));
        if (!ReferenceEquals(mappedUnauthorized.InnerException, unauthorizedFailure))
            throw new InvalidOperationException("Mapped UnauthorizedAccessException did not preserve its inner exception.");

        var loopbackFailure = new COMException("Loopback access denied.", EAccessDenied);
        var loopbackResult = ExpectOriginal(() => MicrophoneAccessDeniedMapper.Invoke(false, () => { throw loopbackFailure; }));
        if (!ReferenceEquals(loopbackResult, loopbackFailure))
            throw new InvalidOperationException("Loopback failure was changed.");

        var otherFailure = new COMException("Different COM failure.", unchecked((int)0x80004005));
        var otherResult = ExpectOriginal(() => MicrophoneAccessDeniedMapper.Invoke(true, () => { throw otherFailure; }));
        if (!ReferenceEquals(otherResult, otherFailure))
            throw new InvalidOperationException("A non-E_ACCESSDENIED HRESULT was changed.");

        var cancellationFailure = new AccessDeniedCancellationException();
        var cancellationResult = ExpectOriginal(() => MicrophoneAccessDeniedMapper.Invoke(true, () => { throw cancellationFailure; }));
        if (!ReferenceEquals(cancellationResult, cancellationFailure))
            throw new InvalidOperationException("Cancellation was changed despite its E_ACCESSDENIED HRESULT.");

        var calls = 0;
        var value = MicrophoneAccessDeniedMapper.Invoke(true, () => { calls++; return "started"; });
        if (value != "started" || calls != 1)
            throw new InvalidOperationException("Successful microphone initialization did not return its result exactly once.");

        Console.WriteLine("Microphone access error mapping passed: microphone COM/UnauthorizedAccess denial with inner exception preservation, loopback and unrelated failures unchanged, cancellation excluded, and success preserved; no audio device was opened.");
    }

    private static MicrophoneAccessDeniedException ExpectMapped(Action action)
    {
        try { action(); }
        catch (MicrophoneAccessDeniedException ex) { return ex; }
        throw new InvalidOperationException("Expected a MicrophoneAccessDeniedException.");
    }

    private static Exception ExpectOriginal(Action action)
    {
        try { action(); }
        catch (MicrophoneAccessDeniedException)
        {
            throw new InvalidOperationException("Unexpected microphone access mapping.");
        }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("Expected the original exception.");
    }

    private sealed class AccessDeniedCancellationException : OperationCanceledException
    {
        internal AccessDeniedCancellationException() => HResult = EAccessDenied;
    }
}
