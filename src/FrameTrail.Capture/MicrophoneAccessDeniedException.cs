using System.Runtime.InteropServices;

namespace FrameTrail.Capture;

/// <summary>Indicates that Windows denied access while opening or starting microphone capture.</summary>
public sealed class MicrophoneAccessDeniedException : Exception
{
    public MicrophoneAccessDeniedException(Exception innerException)
        : base("Windows 拒绝访问麦克风。请检查 Windows 麦克风隐私设置和所选输入设备；桌面应用访问开关可能是原因之一。",
            innerException ?? throw new ArgumentNullException(nameof(innerException))) { }
}

internal static class MicrophoneAccessDeniedMapper
{
    private const int EAccessDenied = unchecked((int)0x80070005);

    internal static T Invoke<T>(bool microphone, Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try { return operation(); }
        catch (Exception ex) when (ShouldMap(microphone, ex))
        {
            throw new MicrophoneAccessDeniedException(ex);
        }
    }

    internal static void Invoke(bool microphone, Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try { operation(); }
        catch (Exception ex) when (ShouldMap(microphone, ex))
        {
            throw new MicrophoneAccessDeniedException(ex);
        }
    }

    private static bool ShouldMap(bool microphone, Exception exception) =>
        microphone
        && exception is not OperationCanceledException
        && exception.HResult == EAccessDenied
        && exception is COMException or UnauthorizedAccessException;
}
