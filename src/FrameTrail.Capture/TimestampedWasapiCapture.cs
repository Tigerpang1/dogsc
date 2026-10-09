using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FrameTrail.Capture;

internal sealed class TimestampedWasapiCapture : IDisposable
{
    private readonly AudioClient _audioClient;
    private readonly AudioCaptureClient _captureClient;
    private readonly AutoResetEvent _packetReady = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _clientStarted;
    private int _stoppedRaised;
    private int _disposed;

    internal TimestampedWasapiCapture(MMDevice endpoint, bool loopback)
    {
        AudioClient? audioClient = null;
        AudioCaptureClient? captureClient = null;
        try
        {
            audioClient = endpoint.AudioClient;
            var waveFormat = audioClient.MixFormat;
            var flags = AudioClientStreamFlags.EventCallback;
            if (loopback) flags |= AudioClientStreamFlags.Loopback;
            audioClient.Initialize(AudioClientShareMode.Shared, flags, 1_000_000, 0, waveFormat, Guid.Empty);
            audioClient.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());
            captureClient = audioClient.AudioCaptureClient;
            _audioClient = audioClient;
            _captureClient = captureClient;
            WaveFormat = waveFormat;
            _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "FrameTrail WASAPI timestamped capture" };
        }
        catch
        {
            try { captureClient?.Dispose(); } catch { }
            try { audioClient?.Dispose(); } catch { }
            _packetReady.Dispose();
            throw;
        }
    }

    internal WaveFormat WaveFormat { get; }
    internal event Action<byte[], int, long, long, long, AudioClientBufferFlags>? PacketAvailable;
    internal event Action<Exception?>? RecordingStopped;

    internal void StartRecording()
    {
        _audioClient.Start();
        _clientStarted = true;
        try { _thread.Start(); }
        catch
        {
            try { _audioClient.Stop(); } finally { _clientStarted = false; }
            throw;
        }
    }

    internal void StopRecording()
    {
        _stopping = true;
        _packetReady.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join();
        else if (_clientStarted)
        {
            Exception? failure = null;
            try { _audioClient.Stop(); } catch (Exception ex) { failure = ex; }
            _clientStarted = false;
            RaiseStopped(failure);
        }
    }

    private void CaptureLoop()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                _packetReady.WaitOne(100);
                ReadAvailablePackets();
                if (_stopping) break;
            }
            ReadAvailablePackets();
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (_clientStarted)
            {
                try { _audioClient.Stop(); } catch (Exception ex) { failure ??= ex; }
                _clientStarted = false;
            }
            RaiseStopped(failure);
        }
    }

    private void RaiseStopped(Exception? failure)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) != 0) return;
        try { RecordingStopped?.Invoke(failure); } catch { }
    }

    private void ReadAvailablePackets()
    {
        while (true)
        {
            var frames = _captureClient.GetNextPacketSize();
            if (frames <= 0) return;
            var flags = AudioClientBufferFlags.None;
            var packetFrames = 0;
            long devicePosition = 0;
            long qpcPosition = 0;
            var pointer = _captureClient.GetBuffer(out packetFrames, out flags, out devicePosition, out qpcPosition);
            byte[] packet;
            long arrivalTicks;
            try
            {
                var bytes = checked(packetFrames * WaveFormat.BlockAlign);
                packet = new byte[bytes];
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                {
                    if (WaveFormat.Encoding == WaveFormatEncoding.Pcm && WaveFormat.BitsPerSample == 8)
                        Array.Fill(packet, (byte)128);
                }
                else if (bytes > 0)
                {
                    Marshal.Copy(pointer, packet, 0, bytes);
                }
                arrivalTicks = Stopwatch.GetTimestamp();
            }
            finally { _captureClient.ReleaseBuffer(packetFrames); }
            PacketAvailable?.Invoke(packet, packetFrames, devicePosition, qpcPosition, arrivalTicks, flags);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopRecording();
        _captureClient.Dispose();
        _audioClient.Dispose();
        _packetReady.Dispose();
    }
}
