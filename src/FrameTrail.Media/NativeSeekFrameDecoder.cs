using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace FrameTrail.Media;

/// <summary>Persistent libav decoder for single-frame timeline seeks. Calls must be serialized by the owner.</summary>
internal sealed unsafe class NativeSeekFrameDecoder : IDisposable
{
    private const int ErrorAgain = -11;
    internal static bool EnableD3D11VA { get; set; }
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private bool _packetPending;
    private AVFrame* _frame;
    private AVFrame* _lastFrame;
    private AVFrame* _transfer;
    private AVBufferRef* _hwDevice;
    private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private long _lastFramePts = ffmpeg.AV_NOPTS_VALUE;
    private double _lastRequestedTime = double.NaN;
    private AVPixelFormat _swsSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private int _swsSourceWidth;
    private int _swsSourceHeight;
    private SwsContext* _sws;
    private readonly int _streamIndex;
    private readonly int _width;
    private readonly int _height;
    private readonly double _timeBaseSec;
    private readonly AVIOInterruptCB_callback _interruptCallback;
    private readonly AVCodecContext_get_format _getFormatCallback;
    private readonly byte*[] _destinationData = new byte*[4];
    private readonly int[] _destinationLinesize = new int[4];
    private CancellationToken _activeCancellation;
    private bool _disposed;

    public double LastTimestampSeconds { get; private set; }
    public string BackendName { get; private set; } = "libav/software";
    public int InputWidth { get; }
    public int InputHeight { get; }
    public double FrameRate { get; }
    public double Duration { get; }
    public double StartTimeSeconds { get; }
    public bool HasBFrames { get; }

    public NativeSeekFrameDecoder(string path, int width, int height, double frameRate, double duration)
    {
        if (!LibavRuntime.TryInitialize()) throw new NotSupportedException(LibavRuntime.UnavailableReason ?? "libav runtime unavailable");
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _width = width;
        _height = height;
        _interruptCallback = Interrupt;
        _getFormatCallback = SelectHardwareFormat;
        try
        {
            var ctx = ffmpeg.avformat_alloc_context();
            if (ctx == null) throw new InvalidOperationException("avformat_alloc_context failed");
            ctx->interrupt_callback.callback = _interruptCallback;
            var openResult = ffmpeg.avformat_open_input(&ctx, Path.GetFullPath(path), null, null);
            _format = ctx;
            Throw(openResult, "avformat_open_input");
            Throw(ffmpeg.avformat_find_stream_info(_format, null), "avformat_find_stream_info");
            var index = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            Throw(index, "av_find_best_stream");
            _streamIndex = index;
            var stream = _format->streams[index];
            ValidateCodecDisplayMatrix(stream->codecpar);
            _timeBaseSec = (double)stream->time_base.num / stream->time_base.den;
            if (!double.IsFinite(_timeBaseSec) || _timeBaseSec <= 0) throw new InvalidDataException("Video stream has an invalid time base.");
            var origin = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;
            StartTimeSeconds = origin * _timeBaseSec;
            LastTimestampSeconds = double.NaN;
            Duration = duration > 0 ? duration : stream->duration > 0 ? stream->duration * _timeBaseSec : _format->duration / (double)ffmpeg.AV_TIME_BASE;
            FrameRate = stream->avg_frame_rate.den != 0 ? (double)stream->avg_frame_rate.num / stream->avg_frame_rate.den : frameRate;
            var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (decoder == null) throw new InvalidOperationException("video decoder not found");
            OpenDecoder(decoder, stream->codecpar);
            InputWidth = _codec->width;
            InputHeight = _codec->height;
            HasBFrames = _codec->has_b_frames != 0;
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _lastFrame = ffmpeg.av_frame_alloc();
            _transfer = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null || _lastFrame == null || _transfer == null) throw new InvalidOperationException("packet/frame allocation failed");
        }
        catch { Dispose(); throw; }
    }

    private void OpenDecoder(AVCodec* decoder, AVCodecParameters* parameters)
    {
        var hardwareAvailable = EnableD3D11VA && TryCreateD3D11Device(decoder);
        _codec = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codec == null) throw new InvalidOperationException("avcodec_alloc_context3 failed");
        Throw(ffmpeg.avcodec_parameters_to_context(_codec, parameters), "avcodec_parameters_to_context");
        _codec->thread_count = 0;
        if (hardwareAvailable)
        {
            _codec->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
            _codec->get_format = _getFormatCallback;
            var result = ffmpeg.avcodec_open2(_codec, decoder, null);
            if (result >= 0)
            {
                BackendName = "libav/d3d11va";
                return;
            }
            var failedCodec = _codec;
            ffmpeg.avcodec_free_context(&failedCodec);
            _codec = null;
            var failedDevice = _hwDevice;
            ffmpeg.av_buffer_unref(&failedDevice);
            _hwDevice = null;
            _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            if (_codec == null) throw new InvalidOperationException("avcodec_alloc_context3 failed");
            Throw(ffmpeg.avcodec_parameters_to_context(_codec, parameters), "avcodec_parameters_to_context");
            _codec->thread_count = 0;
        }
        Throw(ffmpeg.avcodec_open2(_codec, decoder, null), "avcodec_open2(software)");
        BackendName = "libav/software";
    }

    private bool TryCreateD3D11Device(AVCodec* decoder)
    {
        for (var index = 0; ; index++)
        {
            var config = ffmpeg.avcodec_get_hw_config(decoder, index);
            if (config == null) break;
            // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX is 1 in libavcodec's public API.
            if (config->device_type != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA || (config->methods & 1) == 0) continue;
            AVBufferRef* device = null;
            var result = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
            if (result < 0 || device == null)
            {
                if (device != null) ffmpeg.av_buffer_unref(&device);
                return false;
            }
            _hwDevice = device;
            _hardwarePixelFormat = config->pix_fmt;
            return true;
        }
        return false;
    }

    private AVPixelFormat SelectHardwareFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
            if (*format == _hardwarePixelFormat) return *format;
        return ffmpeg.avcodec_default_get_format(context, formats);
    }

    public byte[] ReadFrame(double normalizedTime, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(normalizedTime) || normalizedTime < 0 || normalizedTime > Duration + 0.05) throw new ArgumentOutOfRangeException(nameof(normalizedTime));
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = GC.AllocateUninitializedArray<byte>(checked(_width * _height * 4));
        ReadFrameInto(normalizedTime, pixels, cancellationToken);
        return pixels;
    }

    /// <summary>Decodes directly into caller-owned storage; callers must keep it exclusive until the call returns.</summary>
    public void ReadFrameInto(double normalizedTime, byte[] destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length != checked(_width * _height * 4)) throw new ArgumentException("Destination length does not match decoder dimensions.", nameof(destination));
        if (!double.IsFinite(normalizedTime) || normalizedTime < 0 || normalizedTime > Duration + 0.05) throw new ArgumentOutOfRangeException(nameof(normalizedTime));
        cancellationToken.ThrowIfCancellationRequested();
        var originPts = _format->streams[_streamIndex]->start_time != ffmpeg.AV_NOPTS_VALUE ? _format->streams[_streamIndex]->start_time : 0;
        var relativeTargetPts = (long)Math.Ceiling(normalizedTime / _timeBaseSec - 1e-9);
        var targetPts = originPts + relativeTargetPts;
        var continueForward = !double.IsNaN(LastTimestampSeconds) && normalizedTime > LastTimestampSeconds && normalizedTime <= LastTimestampSeconds + 0.5;
        if (_lastFramePts != ffmpeg.AV_NOPTS_VALUE && normalizedTime >= _lastRequestedTime && targetPts <= _lastFramePts)
        {
            _lastRequestedTime = normalizedTime;
            ConvertFrame(_lastFrame, destination);
            return;
        }

        _activeCancellation = cancellationToken;
        try
        {
            if (!continueForward)
            {
                if (_packetPending)
                {
                    ffmpeg.av_packet_unref(_packet);
                    _packetPending = false;
                }
                ffmpeg.av_frame_unref(_lastFrame);
                _lastFramePts = ffmpeg.AV_NOPTS_VALUE;
                LastTimestampSeconds = double.NaN;
                _lastRequestedTime = double.NaN;
                var seekResult = ffmpeg.av_seek_frame(_format, _streamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ThrowOrCancel(seekResult, "av_seek_frame", cancellationToken);
                ffmpeg.avcodec_flush_buffers(_codec);
                LastTimestampSeconds = double.NaN;
            }
            else
            {
                if (ReceiveToTarget(targetPts, destination, cancellationToken))
                {
                    _lastRequestedTime = normalizedTime;
                    return;
                }
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_packetPending)
                {
                    var pendingSend = ffmpeg.avcodec_send_packet(_codec, _packet);
                    if (pendingSend == ErrorAgain)
                    {
                        if (ReceiveToTarget(targetPts, destination, cancellationToken))
                        {
                            _lastRequestedTime = normalizedTime;
                            return;
                        }
                        continue;
                    }
                    ThrowOrCancel(pendingSend, "avcodec_send_packet(pending)", cancellationToken);
                    ffmpeg.av_packet_unref(_packet);
                    _packetPending = false;
                    if (ReceiveToTarget(targetPts, destination, cancellationToken))
                    {
                        _lastRequestedTime = normalizedTime;
                        return;
                    }
                    continue;
                }
                var read = ffmpeg.av_read_frame(_format, _packet);
                if (read == ffmpeg.AVERROR_EOF)
                {
                    if (FlushToTarget(targetPts, destination, cancellationToken))
                    {
                        _lastRequestedTime = normalizedTime;
                        return;
                    }
                    throw new InvalidOperationException("EOF before requested frame");
                }
                ThrowOrCancel(read, "av_read_frame", cancellationToken);
                try
                {
                    if (_packet->stream_index != _streamIndex) continue;
                    ValidatePacketDisplayMatrix(_packet);
                    var sent = ffmpeg.avcodec_send_packet(_codec, _packet);
                    if (sent == ErrorAgain)
                    {
                        _packetPending = true;
                        if (ReceiveToTarget(targetPts, destination, cancellationToken))
                        {
                            _lastRequestedTime = normalizedTime;
                            return;
                        }
                        continue;
                    }
                    ThrowOrCancel(sent, "avcodec_send_packet", cancellationToken);
                    if (ReceiveToTarget(targetPts, destination, cancellationToken))
                    {
                        _lastRequestedTime = normalizedTime;
                        return;
                    }
                }
                finally
                {
                    if (!_packetPending) ffmpeg.av_packet_unref(_packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Leave the persistent demuxer usable after cancellation. The next call performs a fresh seek and flush.
            ffmpeg.avcodec_flush_buffers(_codec);
            ffmpeg.av_frame_unref(_lastFrame);
            _lastFramePts = ffmpeg.AV_NOPTS_VALUE;
            _lastRequestedTime = double.NaN;
            LastTimestampSeconds = double.NaN;
            if (_packetPending)
            {
                ffmpeg.av_packet_unref(_packet);
                _packetPending = false;
            }
            throw;
        }
        finally { _activeCancellation = default; }
    }

    private bool ReceiveToTarget(long targetPts, byte[] destination, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var receive = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (receive == ErrorAgain || receive == ffmpeg.AVERROR_EOF) return false;
            ThrowOrCancel(receive, "avcodec_receive_frame", token);
            try
            {
                var pts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? _frame->best_effort_timestamp : _frame->pts;
                if (pts == ffmpeg.AV_NOPTS_VALUE) continue;
                ValidateFrameDisplayMatrix(_frame);
                ffmpeg.av_frame_unref(_lastFrame);
                Throw(ffmpeg.av_frame_ref(_lastFrame, _frame), "av_frame_ref(last frame)");
                _lastFramePts = pts;
                if (pts < targetPts) continue;
                ConvertFrame(_frame, destination);
                LastTimestampSeconds = (pts - (_format->streams[_streamIndex]->start_time != ffmpeg.AV_NOPTS_VALUE ? _format->streams[_streamIndex]->start_time : 0)) * _timeBaseSec;
                return true;
            }
            finally { ffmpeg.av_frame_unref(_frame); }
        }
    }

    private bool FlushToTarget(long targetPts, byte[] destination, CancellationToken token)
    {
        var sent = ffmpeg.avcodec_send_packet(_codec, null);
        if (sent == ErrorAgain)
        {
            if (ReceiveToTarget(targetPts, destination, token)) return true;
            sent = ffmpeg.avcodec_send_packet(_codec, null);
        }
        if (sent < 0 && sent != ffmpeg.AVERROR_EOF) ThrowOrCancel(sent, "avcodec_send_packet(flush)", token);
        if (ReceiveToTarget(targetPts, destination, token)) return true;
        if (_lastFramePts == ffmpeg.AV_NOPTS_VALUE) return false;
        var lastPts = _lastFramePts;
        if (lastPts == ffmpeg.AV_NOPTS_VALUE) return false;
        LastTimestampSeconds = (lastPts - (_format->streams[_streamIndex]->start_time != ffmpeg.AV_NOPTS_VALUE ? _format->streams[_streamIndex]->start_time : 0)) * _timeBaseSec;
        ConvertFrame(_lastFrame, destination);
        return true;
    }

    private void ConvertFrame(AVFrame* inputFrame, byte[] destination)
    {
        AVFrame* source = inputFrame;
        if (_hardwarePixelFormat != AVPixelFormat.AV_PIX_FMT_NONE && inputFrame->format == (int)_hardwarePixelFormat)
        {
            ffmpeg.av_frame_unref(_transfer);
            Throw(ffmpeg.av_hwframe_transfer_data(_transfer, inputFrame, 0), "av_hwframe_transfer_data");
            Throw(ffmpeg.av_frame_copy_props(_transfer, inputFrame), "av_frame_copy_props");
            source = _transfer;
        }
        var sourceFormat = (AVPixelFormat)source->format;
        if (_sws == null || _swsSourceFormat != sourceFormat || _swsSourceWidth != source->width || _swsSourceHeight != source->height)
        {
            if (_sws != null) ffmpeg.sws_freeContext(_sws);
            _sws = ffmpeg.sws_getContext(source->width, source->height, sourceFormat, _width, _height,
                AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BICUBIC, null, null, null);
            if (_sws == null) throw new InvalidOperationException($"sws_getContext failed for {sourceFormat}");
            _swsSourceFormat = sourceFormat;
            _swsSourceWidth = source->width;
            _swsSourceHeight = source->height;
        }
        var colorspace = source->colorspace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
            AVColorSpace.AVCOL_SPC_FCC => ffmpeg.SWS_CS_FCC,
            AVColorSpace.AVCOL_SPC_SMPTE240M => ffmpeg.SWS_CS_SMPTE240M,
            AVColorSpace.AVCOL_SPC_BT2020_NCL => ffmpeg.SWS_CS_BT2020,
            AVColorSpace.AVCOL_SPC_SMPTE170M or AVColorSpace.AVCOL_SPC_BT470BG => ffmpeg.SWS_CS_ITU601,
            AVColorSpace.AVCOL_SPC_UNSPECIFIED => ffmpeg.SWS_CS_ITU601,
            _ => throw new NotSupportedException($"Native preview does not support colorspace {source->colorspace}; use the CLI preview path."),
        };
        var coefficientPointer = ffmpeg.sws_getCoefficients(colorspace);
        var coefficients = new int4();
        for (uint i = 0; i < 4; i++) coefficients[i] = coefficientPointer[i];
        var sourceRange = source->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
        Throw(ffmpeg.sws_setColorspaceDetails(_sws, in coefficients, sourceRange, in coefficients, 1, 0, 1 << 16, 1 << 16), "sws_setColorspaceDetails");
        var rowBytes = checked(_width * 4);
        fixed (byte* destinationPointer = destination)
        {
            _destinationData[0] = destinationPointer;
            _destinationLinesize[0] = rowBytes;
            try
            {
                var rows = ffmpeg.sws_scale(_sws, source->data, source->linesize, 0, source->height, _destinationData, _destinationLinesize);
                if (rows != _height) throw new InvalidOperationException($"sws_scale produced {rows}/{_height} rows");
            }
            finally
            {
                _destinationData[0] = null;
                _destinationLinesize[0] = 0;
            }
        }
    }

    private static void ValidateCodecDisplayMatrix(AVCodecParameters* parameters)
    {
        for (var i = 0; i < parameters->nb_coded_side_data; i++)
        {
            var sideData = parameters->coded_side_data + i;
            if (sideData->type == AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX)
                EnsureIdentityDisplayMatrix(sideData->data, sideData->size);
        }
    }

    private static void ValidatePacketDisplayMatrix(AVPacket* packet)
    {
        ulong size = 0;
        var matrix = ffmpeg.av_packet_get_side_data(packet, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX, &size);
        if (matrix != null) EnsureIdentityDisplayMatrix(matrix, size);
    }

    private static void ValidateFrameDisplayMatrix(AVFrame* frame)
    {
        var sideData = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        if (sideData != null) EnsureIdentityDisplayMatrix(sideData->data, sideData->size);
    }

    private static void EnsureIdentityDisplayMatrix(byte* data, ulong size)
    {
        if (data == null || size < 9 * sizeof(int))
            throw new NotSupportedException("Video display matrix is malformed; use the CLI preview path.");
        var matrix = (int*)data;
        if (matrix[0] != 1 << 16 || matrix[1] != 0 || matrix[2] != 0 ||
            matrix[3] != 0 || matrix[4] != 1 << 16 || matrix[5] != 0 ||
            matrix[6] != 0 || matrix[7] != 0 || matrix[8] != 1 << 30)
            throw new NotSupportedException("Video display transform is not an identity matrix; use the CLI preview path.");
    }

    private int Interrupt(void* _) => _activeCancellation.IsCancellationRequested ? 1 : 0;
    private static void Throw(int result, string operation) { if (result < 0) throw new InvalidOperationException($"{operation} failed: {result}"); }
    private static void ThrowOrCancel(int result, string operation, CancellationToken token)
    {
        if (result >= 0) return;
        token.ThrowIfCancellationRequested();
        Throw(result, operation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_transfer != null) { var p = _transfer; ffmpeg.av_frame_free(&p); _transfer = null; }
        if (_lastFrame != null) { var p = _lastFrame; ffmpeg.av_frame_free(&p); _lastFrame = null; }
        if (_frame != null) { var p = _frame; ffmpeg.av_frame_free(&p); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_codec != null) { var p = _codec; ffmpeg.avcodec_free_context(&p); _codec = null; }
        if (_format != null) { var p = _format; ffmpeg.avformat_close_input(&p); _format = null; }
        if (_hwDevice != null) { var p = _hwDevice; ffmpeg.av_buffer_unref(&p); _hwDevice = null; }
        GC.KeepAlive(_interruptCallback);
        GC.KeepAlive(_getFormatCallback);
    }
}
