using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Management;
using FFmpeg.AutoGen;

namespace CSharpFFPlayer
{
    public static class FFmpegErrors
    {
        public const int AVERROR_EAGAIN = -11;            // Resource temporarily unavailable
        public const int AVERROR_EOF = -541478725;        // End of file (AVERROR_EOF)
        public const int AVERROR_EINVAL = -22;            // Invalid argument
        public const int AVERROR_EIO = -5;                // I/O error
                                                          // 必要に応じて追加可能
    }

    public enum FrameReadResult
    {
        FrameAvailable,
        FrameNotReady,
        EndOfStream
    }

    public unsafe class Decoder : IDisposable
    {
        public Decoder()
        {
            ffmpeg.RootPath = @"ffmpeg";
            //ffmpeg.av_register_all();
        }

        private AVFormatContext* formatContext;
        /// <summary>
        /// 現在の <see cref="AVFormatContext"/> を取得します。
        /// </summary>
        public AVFormatContext FormatContext { get => *formatContext; }

        private AVStream* videoStream;
        /// <summary>
        /// 現在の動画ストリームを表す <see cref="AVStream"/> を取得します。
        /// </summary>
        public AVStream VideoStream { get => *videoStream; }

        private AVStream* audioStream;

        private AVCodec* videoCodec;
        /// <summary>
        /// 現在の動画コーデックを表す <see cref="AVCodec"/> を取得します。
        /// </summary>
        public AVCodec VideoCodec { get => *videoCodec; }

        private AVCodec* audioCodec;

        private AVCodecContext* videoCodecContext;
        /// <summary>
        /// 現在の動画コーデックの <see cref="AVCodecContext"/> を取得します。
        /// </summary>
        public AVCodecContext VideoCodecContext { get => *videoCodecContext; }

        private AVCodecContext* audioCodecContext;
        public AVCodecContext AudioCodecContext => *audioCodecContext;

        private AVHWDeviceType? videoHardwareType = null;


        private bool isVideoFrameEnded;
        public bool IsVideoFrameEnded => isVideoFrameEnded;

        private unsafe AVCodec* TryGetHardwareDecoder(AVCodecID codecId, bool forceD3D11 = false)
        {
            if (forceD3D11 && (codecId == AVCodecID.AV_CODEC_ID_H264 || codecId == AVCodecID.AV_CODEC_ID_HEVC))
            {
                Console.WriteLine("💡 D3D11VA を強制使用：通常デコーダと組み合わせます");
                return ffmpeg.avcodec_find_decoder(codecId); // 通常のh264等
            }

            // ② GPU 情報から候補を探す（CUVID, QSV, AMF）
            bool hasNvidia = false;
            bool hasIntel = false;
            bool hasAMD = false;

            try
            {
                var searcher = new ManagementObjectSearcher("Select * from Win32_VideoController");
                foreach (var adapter in searcher.Get())
                {
                    string name = adapter["Name"]?.ToString() ?? "";
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) hasNvidia = true;
                    if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) hasIntel = true;
                    if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) hasAMD = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GPU検出失敗] {ex.Message}（ハードウェアデコードの判定をスキップ）");
            }

            // ③ NVIDIA CUVID
            if (hasNvidia)
            {
                string? cuvidName = codecId switch
                {
                    AVCodecID.AV_CODEC_ID_H264 => "h264_cuvid",
                    AVCodecID.AV_CODEC_ID_HEVC => "hevc_cuvid",
                    _ => null
                };
                if (cuvidName != null)
                {
                    AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(cuvidName);
                    if (codec != null && codec->id == codecId)
                    {
                        Console.WriteLine($"✅ NVIDIA デコーダを使用: {cuvidName}");
                        return codec;
                    }
                }
            }

            // ④ Intel QSV
            if (hasIntel)
            {
                string? qsvName = codecId switch
                {
                    AVCodecID.AV_CODEC_ID_H264 => "h264_qsv",
                    AVCodecID.AV_CODEC_ID_HEVC => "hevc_qsv",
                    _ => null
                };
                if (qsvName != null)
                {
                    AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(qsvName);
                    if (codec != null && codec->id == codecId)
                    {
                        Console.WriteLine($"✅ Intel QSV デコーダを使用: {qsvName}");
                        return codec;
                    }
                }
            }

            // ⑤ AMD AMF
            if (hasAMD)
            {
                string? amfName = codecId switch
                {
                    AVCodecID.AV_CODEC_ID_H264 => "h264_amf",
                    AVCodecID.AV_CODEC_ID_HEVC => "hevc_amf",
                    _ => null
                };
                if (amfName != null)
                {
                    AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(amfName);
                    if (codec != null && codec->id == codecId)
                    {
                        Console.WriteLine($"✅ AMD AMF デコーダを使用: {amfName}");
                        return codec;
                    }
                }
            }

            AVCodec* fallback = ffmpeg.avcodec_find_decoder(codecId);
    Console.WriteLine($"⚙️ ソフトウェア/汎用デコーダを使用: {ffmpeg.avcodec_get_name(codecId)}");
    return fallback;
}





        public unsafe AVFrame* TransferFrameToCPU(AVFrame* hwFrame)
        {
            if (hwFrame == null)
                return null;

            // GPU 上のフレーム形式かどうかを判定
            AVPixelFormat pixFmt = (AVPixelFormat)hwFrame->format;
            bool isGPUFormat =
                pixFmt == AVPixelFormat.AV_PIX_FMT_D3D11 ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_QSV ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_CUDA ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_VAAPI;

            if (!isGPUFormat)
                return hwFrame; // GPUフレームでなければそのまま返す

            AVFrame* swFrame = ffmpeg.av_frame_alloc();
            if (swFrame == null)
                throw new InvalidOperationException("CPUフレームの確保に失敗しました。");

            // GPU → CPU 転送
            int err = ffmpeg.av_hwframe_transfer_data(swFrame, hwFrame, 0);
            if (err < 0)
            {
                ffmpeg.av_frame_free(&swFrame);
                var errbuf = stackalloc byte[1024];
                ffmpeg.av_strerror(err, errbuf, 1024);
                throw new InvalidOperationException($"GPUからCPUへのフレーム転送に失敗しました: {Marshal.PtrToStringAnsi((nint)errbuf)}");
            }

            // 解像度とフォーマットを引き継ぐ（必要に応じて）
            swFrame->width = hwFrame->width;
            swFrame->height = hwFrame->height;
            swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12; // 多くのGPU出力はNV12（必要なら動的取得でもOK）

            return swFrame;
        }


        /// <summary>
        /// ファイルを開き、デコーダを初期化します。
        /// </summary>
        /// <param name="path">開くファイルのパス。</param>
        /// <exception cref="InvalidOperationException" />
        public void OpenFile(string path)
        {
            unsafe
            {
                AVFormatContext* _formatContext = null;
                AVDictionary* formatOptions = null;

                try
                {
                    // formatOptions にオプションをセット
                    ffmpeg.av_dict_set(&formatOptions, "probesize", "512000", 0);
                    ffmpeg.av_dict_set(&formatOptions, "analyzeduration", "1000000", 0); // 1秒

                    // ファイルを開く
                    int ret = ffmpeg.avformat_open_input(&_formatContext, path, null, &formatOptions);
                    if (ret < 0)
                    {
                        throw new InvalidOperationException("指定のファイルは開けませんでした。");
                    }

                    // 成功したので、formatContext に代入
                    formatContext = _formatContext;

                    // FFmpeg の分析最大時間を指定（追加的に設定する場合）
                    formatContext->max_analyze_duration = 1 * ffmpeg.AV_TIME_BASE;

                    // ストリーム情報の取得
                    ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                    if (ret < 0)
                    {
                        throw new InvalidOperationException("ストリームを検出できませんでした。");
                    }

                    // ストリーム情報を取得
                    videoStream = GetFirstVideoStream();
                    audioStream = GetFirstAudioStream();
                }
                finally
                {
                    // formatOptions の解放（FFmpegが自動確保した内部構造を開放）
                    ffmpeg.av_dict_free(&formatOptions);
                }
            }
        }


        public unsafe void InitializeDecoders(bool forceD3D11 = false)
        {
            if (videoStream is not null && videoCodecContext == null)
            {
                videoCodec = TryGetHardwareDecoder(videoStream->codecpar->codec_id, forceD3D11);

                if (videoCodec == null)
                    throw new InvalidOperationException("対応する動画デコーダが見つかりませんでした");

                videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                if (videoCodecContext == null)
                    throw new InvalidOperationException("動画コーデックコンテキストの確保に失敗しました");

                ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar)
                    .OnError(() => throw new InvalidOperationException("動画コーデックパラメータ設定失敗"));

                // ハードウェア使用判断
                string? codecName = Marshal.PtrToStringAnsi((nint)videoCodec->name);
                videoHardwareType = codecName switch
                {
                    not null when codecName.Contains("d3d11va") || forceD3D11 => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                    not null when codecName.Contains("qsv") => AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
                    not null when codecName.Contains("cuda") || codecName.Contains("cuvid") => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                    _ => null
                };

                if (videoHardwareType is AVHWDeviceType hwType)
                {
                    AVBufferRef* hw_device_ctx = null;
                    int result = ffmpeg.av_hwdevice_ctx_create(&hw_device_ctx, hwType, null, null, 0);
                    if (result >= 0)
                    {
                        videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hw_device_ctx);
                        Console.WriteLine($"✅ ハードウェアデコード使用中: {hwType}");
                    }
                    else
                    {
                        var errbuf = stackalloc byte[1024];
                        ffmpeg.av_strerror(result, errbuf, 1024);
                        Console.WriteLine($"⚠️ ハードウェアデバイス {hwType} の初期化に失敗: {Marshal.PtrToStringAnsi((nint)errbuf)}");
                        Console.WriteLine("➡️ ソフトウェアにフォールバック");

                        videoCodec = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);
                        videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                        ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar);
                        videoHardwareType = null;
                    }
                }

                AVDictionary* options = null;
                ffmpeg.av_dict_set(&options, "threads", "1", 0);
                ffmpeg.avcodec_open2(videoCodecContext, videoCodec, &options)
                    .OnError(() => throw new InvalidOperationException("動画デコーダ初期化失敗"));
                ffmpeg.av_dict_free(&options);
            }

            if (audioStream is not null && audioCodecContext == null)
            {
                audioCodec = ffmpeg.avcodec_find_decoder(audioStream->codecpar->codec_id);
                if (audioCodec == null)
                    throw new InvalidOperationException("音声コーデックが見つかりませんでした");

                audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                if (audioCodecContext == null)
                    throw new InvalidOperationException("音声コーデックコンテキストの確保に失敗しました");

                ffmpeg.avcodec_parameters_to_context(audioCodecContext, audioStream->codecpar)
                    .OnError(() => throw new InvalidOperationException("音声コーデックパラメータ設定失敗"));

                AVDictionary* options = null;
                ffmpeg.av_dict_set(&options, "threads", "1", 0);
                ffmpeg.avcodec_open2(audioCodecContext, audioCodec, &options)
                    .OnError(() => throw new InvalidOperationException("音声デコーダ初期化失敗"));
                ffmpeg.av_dict_free(&options);
            }
        }




        private AVStream* GetFirstVideoStream()
        {
            for (int i = 0; i < (int)formatContext->nb_streams; ++i)
            {
                var stream = formatContext->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    return stream;
                }
            }
            return null;
        }

        private AVStream* GetFirstAudioStream()
        {
            for (int i = 0; i < (int)formatContext->nb_streams; i++)
            {
                var stream = formatContext->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    return stream;
                }
            }
            return null;
        }

        public class AVPacketPtr : IDisposable
        {
            public AVPacket* Ptr { get; }

            public AVPacketPtr(AVPacket* ptr)
            {
                Ptr = ptr;
            }

            public void Dispose()
            {
                if (Ptr != null)
                {
                    ffmpeg.av_packet_unref(Ptr);       // データ領域の解放
                    AVPacket* tmp = Ptr;
                    ffmpeg.av_packet_free(&tmp);       // パケット構造体自体の解放
                }
            }
        }

        private object sendPackedSyncObject = new();

        private Queue<AVPacketPtr> videoPackets = new();
        private Queue<AVPacketPtr> audioPackets = new();

        public int SendPacket(int index)
        {
            lock (sendPackedSyncObject)
            {
                if (index == videoStream->index)
                {
                    if (videoPackets.TryDequeue(out var ptr))
                    {
                        ffmpeg.avcodec_send_packet(videoCodecContext, ptr.Ptr)
                            .OnError(() => throw new InvalidOperationException("動画デコーダへのパケットの送信に失敗しました。"));

                        ptr.Dispose(); // ← 重要: Unref + Free
                        return 0;
                    }
                }

                if (index == audioStream->index)
                {
                    if (audioPackets.TryDequeue(out var ptr))
                    {
                        ffmpeg.avcodec_send_packet(audioCodecContext, ptr.Ptr)
                            .OnError(() => throw new InvalidOperationException("音声デコーダへのパケットの送信に失敗しました。"));

                        ptr.Dispose(); // ← 重要: Unref + Free
                        return 0;
                    }
                }

                while (true)
                {
                    AVPacket packet = new AVPacket();
                    var result = ffmpeg.av_read_frame(formatContext, &packet);

                    if (result == 0)
                    {
                        if (packet.stream_index == videoStream->index)
                        {
                            if (packet.stream_index == index)
                            {
                                ffmpeg.avcodec_send_packet(videoCodecContext, &packet)
                                    .OnError(() => throw new InvalidOperationException("動画デコーダへのパケットの送信に失敗しました。"));
                                ffmpeg.av_packet_unref(&packet);
                                return 0;
                            }
                            else
                            {
                                var cloned = ffmpeg.av_packet_clone(&packet);
                                videoPackets.Enqueue(new AVPacketPtr(cloned));
                                ffmpeg.av_packet_unref(&packet);
                                continue;
                            }
                        }

                        if (packet.stream_index == audioStream->index)
                        {
                            if (packet.stream_index == index)
                            {
                                ffmpeg.avcodec_send_packet(audioCodecContext, &packet)
                                    .OnError(() => throw new InvalidOperationException("音声デコーダへのパケットの送信に失敗しました。"));
                                ffmpeg.av_packet_unref(&packet);
                                return 0;
                            }
                            else
                            {
                                var cloned = ffmpeg.av_packet_clone(&packet);
                                audioPackets.Enqueue(new AVPacketPtr(cloned));
                                ffmpeg.av_packet_unref(&packet);
                                continue;
                            }
                        }

                        ffmpeg.av_packet_unref(&packet); // ストリームに該当しない場合も忘れずに解放
                    }
                    else
                    {
                        return -1;
                    }
                }
            }
        }

        public unsafe (FrameReadResult result, ManagedFrame frame) TryReadFrame()
        {
            var frame = TryReadUnsafeFrame(out var result);

            if (result == FrameReadResult.FrameAvailable)
            {
                // GPUフレームだったらCPUに転送
                var cpuFrame = TransferFrameToCPU(frame);
                if (cpuFrame != frame)
                {
                    ffmpeg.av_frame_free(&frame); // 元のGPUフレームを解放
                }

                // NV12 → YUV420P へ変換
                var yuv420Frame = ConvertToYUV420P(cpuFrame);
                if (yuv420Frame != cpuFrame)
                {
                    ffmpeg.av_frame_free(&cpuFrame);
                }

                ulong mem = GetAVFrameMemoryUsage(yuv420Frame);
                //Console.WriteLine($"[Frame] Format: {(AVPixelFormat)yuv420Frame->format}, Size: {yuv420Frame->width}x{yuv420Frame->height}, Memory: {mem / 1024.0 / 1024:F2} MB");
                return (result, new ManagedFrame(yuv420Frame));
            }

            return (result, null);
        }


        private unsafe AVFrame* TryReadUnsafeFrame(out FrameReadResult result)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();

            int receiveResult = ffmpeg.avcodec_receive_frame(videoCodecContext, frame);
            if (receiveResult == 0)
            {
                result = FrameReadResult.FrameAvailable;
                return frame;
            }

            if (receiveResult == FFmpegErrors.AVERROR_EAGAIN)
            {
                // もっとパケットが必要
                int sendResult = SendPacket(videoStream->index);
                if (sendResult == 0)
                {
                    receiveResult = ffmpeg.avcodec_receive_frame(videoCodecContext, frame);
                    if (receiveResult == 0)
                    {
                        result = FrameReadResult.FrameAvailable;
                        return frame;
                    }
                }
                else if (sendResult == -1) // ← EOF（av_read_frame が失敗した）
                {
                    // デコーダにnullパケットを送って EOF を通知
                    ffmpeg.avcodec_send_packet(videoCodecContext, null);

                    receiveResult = ffmpeg.avcodec_receive_frame(videoCodecContext, frame);
                    if (receiveResult == 0)
                    {
                        result = FrameReadResult.FrameAvailable;
                        return frame;
                    }
                    else if (receiveResult == ffmpeg.AVERROR_EOF)
                    {
                        isVideoFrameEnded = true;
                        ffmpeg.av_frame_free(&frame);
                        result = FrameReadResult.EndOfStream;
                        return null;
                    }
                }

                if (receiveResult == FFmpegErrors.AVERROR_EAGAIN)
                {
                    ffmpeg.av_frame_free(&frame);
                    result = FrameReadResult.FrameNotReady;
                    return null;
                }
            }

            if (receiveResult == ffmpeg.AVERROR_EOF)
            {
                isVideoFrameEnded = true;
                ffmpeg.av_frame_free(&frame);
                result = FrameReadResult.EndOfStream;
                return null;
            }

            ffmpeg.av_frame_free(&frame);
            throw new Exception($"avcodec_receive_frame failed: {receiveResult}");
        }


        public static unsafe AVFrame* ConvertToYUV420P(AVFrame* src)
        {
            if ((AVPixelFormat)src->format == AVPixelFormat.AV_PIX_FMT_YUV420P)
                return src; // すでに軽量形式なら変換しない

            AVFrame* dst = ffmpeg.av_frame_alloc();
            dst->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            dst->width = src->width;
            dst->height = src->height;

            ffmpeg.av_frame_get_buffer(dst, 32).OnError(() =>
                throw new Exception("出力フレームバッファ確保に失敗"));

            SwsContext* sws = ffmpeg.sws_getContext(
                src->width, src->height, (AVPixelFormat)src->format,
                dst->width, dst->height, (AVPixelFormat)dst->format,
                1, null, null, null);

            if (sws == null)
                throw new Exception("sws_getContext 失敗");

            ffmpeg.sws_scale(
                sws, src->data, src->linesize, 0, src->height,
                dst->data, dst->linesize);

            ffmpeg.sws_freeContext(sws);

            return dst;
        }



        ulong GetAVFrameMemoryUsage(AVFrame* frame)
        {
            ulong totalSize = 0;

            // 1. 実際の buf から
            for (uint i = 0; i < 8; i++)
            {
                if (frame->buf[i] != null)
                {
                    totalSize += frame->buf[i]->size;
                }
            }

            // 2. バッファがないときは推定値で
            if (totalSize == 0)
            {
                uint est = (uint)ffmpeg.av_image_get_buffer_size((AVPixelFormat)frame->format, frame->width, frame->height, 1);
                if (est > 0)
                    totalSize = est;
            }

            return totalSize;
        }


        /// <summary>
        /// 次の音声フレームを読み取ります。動画の終端に達している場合は <c>null</c> が返されます。
        /// </summary>
        public unsafe ManagedFrame ReadAudioFrame()
        {
            var frame = ReadUnsafeAudioFrame();
            if (frame is null)
            {
                return null;
            }
            return new ManagedFrame(frame);
        }

        private bool isAudioFrameEnded;

        /// <summary>
        /// 次の音声フレームを読み取ります。動画の終端に達している場合は <c>null</c> が返されます。
        /// </summary>
        /// <remarks>
        /// 取得したフレームは <see cref="ffmpeg.av_frame_free(AVFrame**)"/> を呼び出して手動で解放する必要があることに注意してください。
        /// </remarks>
        /// <returns></returns>
        public unsafe AVFrame* ReadUnsafeAudioFrame()
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();

            if (ffmpeg.avcodec_receive_frame(audioCodecContext, frame) == 0)
            {
                return frame;
            }

            if (isAudioFrameEnded)
            {
                ffmpeg.av_frame_free(&frame); // ← 解放追加
                return null;
            }

            while (SendPacket(audioStream->index) == 0)
            {
                if (ffmpeg.avcodec_receive_frame(audioCodecContext, frame) == 0)
                {
                    return frame;
                }
            }

            isAudioFrameEnded = true;
            ffmpeg.avcodec_send_packet(audioCodecContext, null)
                .OnError(() => throw new InvalidOperationException("デコーダへのnullパケットの送信に失敗しました。"));

            if (ffmpeg.avcodec_receive_frame(audioCodecContext, frame) == 0)
            {
                return frame;
            }

            ffmpeg.av_frame_free(&frame); // ← 最後に解放
            return null;
        }

        ~Decoder()
        {
            DisposeUnManaged();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            DisposeUnManaged();
            GC.SuppressFinalize(this);
        }

        private bool isDisposed = false;
        private void DisposeUnManaged()
        {
            if (isDisposed) { return; }

            AVCodecContext* codecContext = videoCodecContext;
            AVFormatContext* formatContext = this.formatContext;
            AVCodecContext* audioCtx = audioCodecContext;

            if (audioCtx != null)
            {
                ffmpeg.avcodec_free_context(&audioCtx);
                audioCodecContext = null;
            }
            if (codecContext != null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
                videoCodecContext = null;
            }
            if (formatContext != null)
            {
                ffmpeg.avformat_close_input(&formatContext);
                this.formatContext = null;
            }
            

            isDisposed = true;
        }

        

    }



    internal static class WrapperHelper
    {
        public static int OnError(this int n, Action act)
        {
            if (n < 0)
            {
                var buffer = Marshal.AllocHGlobal(1000);
                string str;
                unsafe
                {
                    ffmpeg.av_make_error_string((byte*)buffer.ToPointer(), 1000, n);
                    str = new string((sbyte*)buffer.ToPointer());
                }
                Marshal.FreeHGlobal(buffer);
                Debug.WriteLine(str);
                act.Invoke();
            }
            return n;
        }
    }
}
