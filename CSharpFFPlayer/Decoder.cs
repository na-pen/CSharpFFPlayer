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
        public const int AVERROR_EAGAIN = -11;             // 一時的なリソース不足
        public const int AVERROR_EOF = -541478725;         // ストリームの終端（End of File）
        public const int AVERROR_EINVAL = -22;             // 無効な引数
        public const int AVERROR_EIO = -5;                 // 入出力エラー
                                                           // public const int AVERROR_ENOMEM = -12;          // メモリ不足（必要に応じて追加）
                                                           // public const int AVERROR_UNKNOWN = -1313558101; // 未知のエラー（環境依存）
    }

    public enum FrameReadResult
    {
        FrameAvailable,  // フレームが取得できた
        FrameNotReady,   // フレームはまだ準備できていない（EAGAINなど）
        EndOfStream      // ストリームの終端（EOF）
    }

    public unsafe class Decoder : IDisposable
    {
        public Decoder()
        {
            ffmpeg.RootPath = @"ffmpeg";  // 必要に応じて環境変数からの読み取りに置換可
                                          // ffmpeg.av_register_all(); // FFmpeg 4.0以降は不要
        }

        private AVFormatContext* formatContext;
        private AVStream* videoStream;
        private AVStream* audioStream;

        private AVCodec* videoCodec;
        private AVCodec* audioCodec;

        private AVCodecContext* videoCodecContext;
        private AVCodecContext* audioCodecContext;

        private AVHWDeviceType? videoHardwareType = null;

        private bool isVideoFrameEnded;

        /// <summary>
        /// 現在の AVFormatContext を取得します。
        /// </summary>
        public AVFormatContext FormatContext => *formatContext;
        public AVFormatContext* FormatContextPointer => formatContext;

        /// <summary>
        /// 現在の動画ストリーム（AVStream）を取得します。
        /// </summary>
        public AVStream VideoStream => *videoStream;
        public AVStream* VideoStreamPointer => videoStream;

        /// <summary>
        /// 現在の動画コーデック（AVCodec）を取得します。
        /// </summary>
        public AVCodec VideoCodec => *videoCodec;

        /// <summary>
        /// 現在の動画コーデックコンテキスト（AVCodecContext）を取得します。
        /// </summary>
        public AVCodecContext VideoCodecContext => *videoCodecContext;
        public AVCodecContext* VideoCodecContextPointer => videoCodecContext;

        /// <summary>
        /// 現在の音声コーデックコンテキスト（AVCodecContext）を取得します。
        /// </summary>
        public AVCodecContext AudioCodecContext => *audioCodecContext;
        public AVCodecContext* AudioCodecContextPointer => audioCodecContext;

        /// <summary>
        /// 映像ストリームが終端に達したかどうかを取得します。
        /// </summary>
        public bool IsVideoFrameEnded => isVideoFrameEnded;

        /// <summary>
        /// 現在の音声ストリーム（AVStream）を取得します。
        /// </summary>
        public AVStream AudioStream => *audioStream;

        public AVStream* AudioStreamPointer => audioStream;

        private unsafe AVCodec* TryGetHardwareDecoder(AVCodecID codecId, bool forceD3D11 = false)
        {
            if (forceD3D11 && (codecId == AVCodecID.AV_CODEC_ID_H264 || codecId == AVCodecID.AV_CODEC_ID_HEVC))
            {
                Console.WriteLine("[Info] D3D11VA 使用を強制。ソフトウェアデコーダを返します。");
                return ffmpeg.avcodec_find_decoder(codecId);
            }

            bool hasNvidia = false, hasIntel = false, hasAMD = false;

            try
            {
                using var searcher = new ManagementObjectSearcher("Select * from Win32_VideoController");
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
                Console.WriteLine($"[Warn] GPU検出に失敗しました。ハードウェアデコーダの選択をスキップします: {ex.Message}");
            }

            // GPUベンダー別のデコーダ名を決定
            (bool available, string? decoderName)[] candidates = new[]
            {
                (hasNvidia, codecId == AVCodecID.AV_CODEC_ID_H264 ? "h264_cuvid" :
                    codecId == AVCodecID.AV_CODEC_ID_HEVC ? "hevc_cuvid" : null),
                (hasIntel,  codecId == AVCodecID.AV_CODEC_ID_H264 ? "h264_qsv"   :
                    codecId == AVCodecID.AV_CODEC_ID_HEVC ? "hevc_qsv"   : null),
                (hasAMD,    codecId == AVCodecID.AV_CODEC_ID_H264 ? "h264_amf"   :
                    codecId == AVCodecID.AV_CODEC_ID_HEVC ? "hevc_amf"   : null),
            };

            foreach (var (available, name) in candidates)
            {
                if (available && name != null)
                {
                    AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(name);
                    if (codec != null && codec->id == codecId)
                    {
                        Console.WriteLine($"[Info] ハードウェアデコーダを使用します: {name}");
                        return codec;
                    }
                }
            }

            AVCodec* fallback = ffmpeg.avcodec_find_decoder(codecId);
            Console.WriteLine($"[Info] ハードウェアデコーダ未使用。ソフトウェアデコーダを使用します: {ffmpeg.avcodec_get_name(codecId)}");
            return fallback;
        }

        public unsafe AVFrame* TransferFrameToCPU(AVFrame* hwFrame)
        {
            if (hwFrame == null)
                return null;

            AVPixelFormat pixFmt = (AVPixelFormat)hwFrame->format;

            bool isGPUFormat = pixFmt == AVPixelFormat.AV_PIX_FMT_D3D11 ||
                               pixFmt == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                               pixFmt == AVPixelFormat.AV_PIX_FMT_QSV ||
                               pixFmt == AVPixelFormat.AV_PIX_FMT_CUDA ||
                               pixFmt == AVPixelFormat.AV_PIX_FMT_VAAPI;

            if (!isGPUFormat)
                return hwFrame; // GPUフレームでなければそのまま返す

            AVFrame* swFrame = ffmpeg.av_frame_alloc();
            if (swFrame == null)
                throw new InvalidOperationException("CPUフレーム用のバッファ確保に失敗しました。");

            int err = ffmpeg.av_hwframe_transfer_data(swFrame, hwFrame, 0);
            if (err < 0)
            {
                ffmpeg.av_frame_free(&swFrame);
                var errbuf = stackalloc byte[1024];
                ffmpeg.av_strerror(err, errbuf, 1024);
                throw new InvalidOperationException($"GPUフレームのCPU転送に失敗しました。原因: {Marshal.PtrToStringAnsi((nint)errbuf)}");
            }

            // 解像度とフォーマットの明示的な設定
            swFrame->width = hwFrame->width;
            swFrame->height = hwFrame->height;
            swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12; // 多くのハードウェア出力はNV12フォーマット

            return swFrame;
        }


        /// <summary>
        /// ファイルを開き、デコーダを初期化します。
        /// </summary>
        /// <param name="path">開くファイルのパス。</param>
        /// <exception cref="InvalidOperationException" />
        public double OpenFile(string path)
        {
            unsafe
            {
                AVFormatContext* _formatContext = null;
                AVDictionary* formatOptions = null;
                double durationMs = 0.0;

                try
                {
                    // ストリーム解析のヒントとなるオプションを設定
                    ffmpeg.av_dict_set(&formatOptions, "probesize", "512000", 0);             // バイト単位の解析サイズ
                    ffmpeg.av_dict_set(&formatOptions, "analyzeduration", "1000000", 0);      // 解析最大時間（ミリセカンド）

                    // 入力ファイルを開く
                    int ret = ffmpeg.avformat_open_input(&_formatContext, path, null, &formatOptions);
                    if (ret < 0)
                        throw new InvalidOperationException("指定されたファイルを開くことができません。");

                    formatContext = _formatContext;

                    // ストリーム解析時間の上限設定（ミリセカンド単位）
                    formatContext->max_analyze_duration = 1000000;

                    // ストリーム情報を解析
                    ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                    if (ret < 0)
                        throw new InvalidOperationException("ストリーム情報の取得に失敗しました。");
                    // コンソールに詳細出力
                    durationMs = PrintMediaInfo(formatContext, path);

                    // 最初の映像・音声ストリームを取得
                    videoStream = GetFirstVideoStream();
                    audioStream = GetFirstAudioStream();
                }
                finally
                {
                    // formatOptions のメモリを解放
                    ffmpeg.av_dict_free(&formatOptions);
                }
                return durationMs;
            }
        }

        /// <summary>
        /// AVFormatContext から動画/音声ストリームの情報を標準出力に出力します。
        /// </summary>
        /// <param name="formatContext">取得済みの AVFormatContext*</param>
        /// <param name="path">入力ファイルのパス</param>
        public static unsafe double PrintMediaInfo(AVFormatContext* formatContext, string path)
        {
            if (formatContext == null)
            {
                Console.WriteLine("AVFormatContext が null です。");
                return -1;
            }

            Console.WriteLine($"--- メディア情報: {path} ---");

            // 全体情報
            double durationMs = formatContext->duration / (double)ffmpeg.AV_TIME_BASE * 1000;
            Console.WriteLine($"再生時間: {durationMs:F0} ミリ秒");
            Console.WriteLine($"ストリーム数: {formatContext->nb_streams}");
            Console.WriteLine($"ビットレート: {formatContext->bit_rate} bps");

            // 各ストリームの情報
            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                AVStream* stream = formatContext->streams[i];
                AVCodecParameters* codecpar = stream->codecpar;
                AVRational timeBase = stream->time_base;

                if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    double fps = stream->r_frame_rate.den != 0
                        ? stream->r_frame_rate.num / (double)stream->r_frame_rate.den
                        : 0.0;

                    Console.WriteLine($"\n[映像ストリーム #{i}]");
                    Console.WriteLine($"  コーデック: {ffmpeg.avcodec_get_name(codecpar->codec_id)}");
                    Console.WriteLine($"  解像度: {codecpar->width} x {codecpar->height}");
                    Console.WriteLine($"  推定FPS: {fps:F3}");
                    Console.WriteLine($"  タイムベース: {timeBase.num}/{timeBase.den}");
                }
                else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    Console.WriteLine($"\n[音声ストリーム #{i}]");
                    Console.WriteLine($"  コーデック: {ffmpeg.avcodec_get_name(codecpar->codec_id)}");
                    Console.WriteLine($"  サンプルレート: {codecpar->sample_rate} Hz");
                    Console.WriteLine($"  チャンネル数: {codecpar->ch_layout.nb_channels}");
                    Console.WriteLine($"  タイムベース: {timeBase.num}/{timeBase.den}");
                }
                else
                {
                    Console.WriteLine($"\n[その他ストリーム #{i}] 種類: {codecpar->codec_type}");
                }
            }

            Console.WriteLine($"--- メディア情報の出力完了 ---");
            return durationMs;
        }

        /// <summary>
        /// 映像・音声デコーダの初期化を行います。必要に応じてハードウェアデコードを使用します。
        /// </summary>
        /// <param name="forceD3D11">D3D11VAを強制するかどうか。</param>
        public unsafe void InitializeDecoders(bool forceD3D11 = false)
        {
            if (videoStream is not null && videoCodecContext == null)
            {
                videoCodec = TryGetHardwareDecoder(videoStream->codecpar->codec_id, forceD3D11);
                if (videoCodec == null)
                    throw new InvalidOperationException("対応する映像デコーダが見つかりません。");

                videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                if (videoCodecContext == null)
                    throw new InvalidOperationException("映像コーデックコンテキストの確保に失敗しました。");

                ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar)
                    .OnError(() => throw new InvalidOperationException("映像コーデックパラメータの適用に失敗しました。"));

                // ハードウェアデコーダの種類を判別
                string? codecName = Marshal.PtrToStringAnsi((nint)videoCodec->name);
                videoHardwareType = codecName switch
                {
                    not null when codecName.Contains("d3d11va") || forceD3D11 => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                    not null when codecName.Contains("qsv") => AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
                    not null when codecName.Contains("cuda") || codecName.Contains("cuvid") => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                    _ => null
                };

                // ハードウェアコンテキストの初期化
                if (videoHardwareType is AVHWDeviceType hwType)
                {
                    AVBufferRef* hw_device_ctx = null;
                    int result = ffmpeg.av_hwdevice_ctx_create(&hw_device_ctx, hwType, null, null, 0);
                    if (result >= 0)
                    {
                        videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hw_device_ctx);
                        Console.WriteLine($"ハードウェアデコード使用中: {hwType}");
                    }
                    else
                    {
                        // 初期化失敗時はソフトウェアにフォールバック
                        var errbuf = stackalloc byte[1024];
                        ffmpeg.av_strerror(result, errbuf, 1024);
                        Console.WriteLine($"ハードウェアデバイス {hwType} の初期化に失敗: {Marshal.PtrToStringAnsi((nint)errbuf)}");
                        Console.WriteLine("ソフトウェアデコードへ切り替えます。");

                        videoCodec = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);
                        videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                        ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar);
                        videoHardwareType = null;
                    }
                }

                // デコーダ初期化（スレッド数の指定も可）
                AVDictionary* options = null;
                ffmpeg.av_dict_set(&options, "threads", "1", 0);
                ffmpeg.avcodec_open2(videoCodecContext, videoCodec, &options)
                    .OnError(() => throw new InvalidOperationException("映像デコーダの初期化に失敗しました。"));
                ffmpeg.av_dict_free(&options);
            }

            if (audioStream is not null && audioCodecContext == null)
            {
                audioCodec = ffmpeg.avcodec_find_decoder(audioStream->codecpar->codec_id);
                if (audioCodec == null)
                    throw new InvalidOperationException("対応する音声デコーダが見つかりません。");

                audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                if (audioCodecContext == null)
                    throw new InvalidOperationException("音声コーデックコンテキストの確保に失敗しました。");

                ffmpeg.avcodec_parameters_to_context(audioCodecContext, audioStream->codecpar)
                    .OnError(() => throw new InvalidOperationException("音声コーデックパラメータの適用に失敗しました。"));

                AVDictionary* options = null;
                ffmpeg.av_dict_set(&options, "threads", "1", 0);
                ffmpeg.avcodec_open2(audioCodecContext, audioCodec, &options)
                    .OnError(() => throw new InvalidOperationException("音声デコーダの初期化に失敗しました。"));
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
        public void SetVideoCodecContextPointer(AVCodecContext* ptr)
        {
            videoCodecContext = ptr;
        }
        public unsafe void ReinitializeVideoCodecContext()
        {
            if (videoCodecContext != null)
            {
                AVCodecContext* tmp = videoCodecContext;
                ffmpeg.avcodec_free_context(&tmp);
                videoCodecContext = null;
            }

            // AVCodecContext を新しく作成し初期化
            videoCodecContext = ffmpeg.avcodec_alloc_context3(null);
            ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar);

            AVCodec* codec = ffmpeg.avcodec_find_decoder(videoCodecContext->codec_id);
            if (codec == null || ffmpeg.avcodec_open2(videoCodecContext, codec, null) < 0)
            {
                throw new InvalidOperationException("Video codec の再初期化に失敗しました。");
            }
        }

        public unsafe void SetVideoCodecContext(AVCodecContext* ctx)
        {
            videoCodecContext = ctx;
        }

        private object sendPackedSyncObject = new();

        private Queue<AVPacketPtr> videoPackets = new();
        private Queue<AVPacketPtr> audioPackets = new();

        public int SendPacket(int index)
        {
            lock (sendPackedSyncObject)
            {
                // ストリームとデコーダの対応を取得
                AVCodecContext* targetCodecContext = null;
                Queue<AVPacketPtr> targetPacketQueue = null;

                if (index == videoStream->index)
                {
                    targetCodecContext = videoCodecContext;
                    targetPacketQueue = videoPackets;
                }
                else if (index == audioStream->index)
                {
                    targetCodecContext = audioCodecContext;
                    targetPacketQueue = audioPackets;
                }
                else
                {
                    throw new InvalidOperationException($"不明なストリームインデックス: {index}");
                }

                // 事前にキューにパケットがある場合はそれを送信
                if (targetPacketQueue.TryDequeue(out var queuedPacket))
                {
                    int sendResult = ffmpeg.avcodec_send_packet(targetCodecContext, queuedPacket.Ptr);
                    queuedPacket.Dispose(); // パケットの解放（Unref + Free）

                    if (sendResult < 0)
                        throw new InvalidOperationException("デコーダへのパケット送信に失敗しました。");

                    return 0;
                }

                // パケット読み込みループ
                while (true)
                {
                    AVPacket packet = new AVPacket();
                    int readResult = ffmpeg.av_read_frame(formatContext, &packet);

                    if (readResult < 0)
                    {
                        // ストリーム終端、または読み込み失敗
                        return -1;
                    }

                    try
                    {
                        int streamIdx = packet.stream_index;

                        if (streamIdx == videoStream->index || streamIdx == audioStream->index)
                        {
                            bool isTargetStream = (streamIdx == index);
                            AVCodecContext* codecCtx = (streamIdx == videoStream->index) ? videoCodecContext : audioCodecContext;
                            var packetQueue = (streamIdx == videoStream->index) ? videoPackets : audioPackets;

                            if (isTargetStream)
                            {
                                int sendResult = ffmpeg.avcodec_send_packet(codecCtx, &packet);
                                if (sendResult < 0)
                                    throw new InvalidOperationException("デコーダへのパケット送信に失敗しました。");

                                return 0;
                            }
                            else
                            {
                                AVPacket* cloned = ffmpeg.av_packet_clone(&packet);
                                packetQueue.Enqueue(new AVPacketPtr(cloned));
                            }
                        }
                    }
                    finally
                    {
                        ffmpeg.av_packet_unref(&packet); // 処理対象外のストリーム or 処理済み
                    }
                }
            }
        }


        public unsafe (FrameReadResult result, ManagedFrame frame) TryReadFrame()
        {
            var frame = TryReadUnsafeFrame(out var result);

            if (result != FrameReadResult.FrameAvailable)
            {
                return (result, null);
            }
            return (result, new ManagedFrame(frame));
        }

        private unsafe AVFrame* TryReadUnsafeFrame(out FrameReadResult result)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();

            // 試しに1回フレーム受信
            int receiveResult = ffmpeg.avcodec_receive_frame(videoCodecContext, frame);

            // 受信に成功した場合
            if (receiveResult == 0)
            {
                result = FrameReadResult.FrameAvailable;
                return frame;
            }

            // 入力不足（パケット供給が必要）
            if (receiveResult == FFmpegErrors.AVERROR_EAGAIN)
            {
                int sendResult = SendPacket(videoStream->index);

                if (sendResult == 0)
                {
                    // 再度受信を試みる
                    receiveResult = ffmpeg.avcodec_receive_frame(videoCodecContext, frame);
                    if (receiveResult == 0)
                    {
                        result = FrameReadResult.FrameAvailable;
                        return frame;
                    }
                }
                else if (sendResult == -1) // ファイル終端
                {
                    // デコーダに空パケットを送りEOFを通知
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

                // フレームはまだ利用不可
                if (receiveResult == FFmpegErrors.AVERROR_EAGAIN)
                {
                    ffmpeg.av_frame_free(&frame);
                    result = FrameReadResult.FrameNotReady;
                    return null;
                }
            }

            // ストリーム終了
            if (receiveResult == ffmpeg.AVERROR_EOF)
            {
                isVideoFrameEnded = true;
                ffmpeg.av_frame_free(&frame);
                result = FrameReadResult.EndOfStream;
                return null;
            }

            // その他のエラー
            ffmpeg.av_frame_free(&frame);
            throw new Exception($"avcodec_receive_frame failed: {receiveResult}");
        }


        public static unsafe AVFrame* ConvertToYUV420P(AVFrame* src)
        {
            if ((AVPixelFormat)src->format == AVPixelFormat.AV_PIX_FMT_YUV420P)
                return src; // すでにYUV420P形式なら変換不要

            AVFrame* dst = ffmpeg.av_frame_alloc();
            if (dst == null)
                throw new Exception("出力フレームの確保に失敗しました。");

            dst->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            dst->width = src->width;
            dst->height = src->height;

            if (ffmpeg.av_frame_get_buffer(dst, 32) < 0)
            {
                ffmpeg.av_frame_free(&dst);
                throw new Exception("出力フレームバッファの確保に失敗しました。");
            }

            SwsContext* sws = ffmpeg.sws_getContext(
                src->width, src->height, (AVPixelFormat)src->format,
                dst->width, dst->height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                1, null, null, null);

            if (sws == null)
            {
                ffmpeg.av_frame_free(&dst);
                throw new Exception("スケーラコンテキスト（sws_getContext）の作成に失敗しました。");
            }

            ffmpeg.sws_scale(
                sws, src->data, src->linesize, 0, src->height,
                dst->data, dst->linesize);

            ffmpeg.sws_freeContext(sws);
            return dst;
        }




        public static unsafe ulong GetAVFrameMemoryUsage(AVFrame* frame)
        {
            ulong totalSize = 0;

            for (uint i = 0; i < ffmpeg.AV_NUM_DATA_POINTERS; i++)
            {
                if (frame->buf[i] != null)
                    totalSize += frame->buf[i]->size;
            }

            // バッファ情報がない場合は推定サイズを使う
            if (totalSize == 0)
            {
                int estSize = ffmpeg.av_image_get_buffer_size(
                    (AVPixelFormat)frame->format,
                    frame->width,
                    frame->height,
                    1);

                if (estSize > 0)
                    totalSize = (ulong)estSize;
            }

            return totalSize;
        }



        /// <summary>
        /// 次の音声フレームを読み取って <see cref="ManagedFrame"/> に包んで返します。終端に達した場合は null を返します。
        /// </summary>
        public unsafe ManagedFrame ReadAudioFrame()
        {
            var frame = ReadUnsafeAudioFrame();
            return frame == null ? null : new ManagedFrame(frame);
        }

        /// <summary>
        /// 次の音声フレームを読み取って <see cref="ManagedFrame"/> に包んで返します。
        /// 読み取り状態を <see cref="FrameReadResult"/> で返します。
        /// </summary>
        public unsafe (FrameReadResult result, ManagedFrame frame) TryReadAudioFrame()
        {
            var frame = TryReadUnsafeAudioFrame(out var result);
            return (result, frame == null ? null : new ManagedFrame(frame));
        }

        /// <summary>
        /// 次の音声フレームを読み取ります。呼び出し側が <see cref="ffmpeg.av_frame_free"/> によって解放する必要があります。
        /// 読み取り状態を <see cref="FrameReadResult"/> で返します。
        /// </summary>
        public unsafe AVFrame* TryReadUnsafeAudioFrame(out FrameReadResult result)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            if (frame == null)
                throw new Exception("音声フレームの確保に失敗しました。");

            int receiveResult = ffmpeg.avcodec_receive_frame(audioCodecContext, frame);

            if (receiveResult == 0)
            {
                result = FrameReadResult.FrameAvailable;
                return frame;
            }

            // EAGAIN: 入力不足 → パケットを供給して再試行
            if (receiveResult == FFmpegErrors.AVERROR_EAGAIN)
            {
                int sendResult = SendPacket(audioStream->index);

                if (sendResult == 0)
                {
                    receiveResult = ffmpeg.avcodec_receive_frame(audioCodecContext, frame);
                    if (receiveResult == 0)
                    {
                        result = FrameReadResult.FrameAvailable;
                        return frame;
                    }
                }
                else if (sendResult == -1) // ファイル終端
                {
                    // デコーダに null パケット送信で EOF 通知
                    ffmpeg.avcodec_send_packet(audioCodecContext, null);

                    receiveResult = ffmpeg.avcodec_receive_frame(audioCodecContext, frame);
                    if (receiveResult == 0)
                    {
                        result = FrameReadResult.FrameAvailable;
                        return frame;
                    }
                    else if (receiveResult == ffmpeg.AVERROR_EOF)
                    {
                        isAudioFrameEnded = true;
                        ffmpeg.av_frame_free(&frame);
                        result = FrameReadResult.EndOfStream;
                        return null;
                    }
                }

                // フレームはまだ利用不可
                if (receiveResult == FFmpegErrors.AVERROR_EAGAIN)
                {
                    ffmpeg.av_frame_free(&frame);
                    result = FrameReadResult.FrameNotReady;
                    return null;
                }
            }

            if (receiveResult == ffmpeg.AVERROR_EOF)
            {
                isAudioFrameEnded = true;
                ffmpeg.av_frame_free(&frame);
                result = FrameReadResult.EndOfStream;
                return null;
            }

            // その他のエラー
            ffmpeg.av_frame_free(&frame);
            throw new Exception($"avcodec_receive_frame (Audio) failed: {receiveResult}");
        }



        private bool isAudioFrameEnded;

        /// <summary>
        /// 次の音声フレームを読み取ります。呼び出し側が <see cref="ffmpeg.av_frame_free"/> によって解放する必要があります。
        /// 終端に達した場合は null を返します。
        /// </summary>
        public unsafe AVFrame* ReadUnsafeAudioFrame()
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            if (frame == null)
                throw new Exception("音声フレームの確保に失敗しました。");

            if (ffmpeg.avcodec_receive_frame(audioCodecContext, frame) == 0)
                return frame;

            if (isAudioFrameEnded)
            {
                ffmpeg.av_frame_free(&frame);
                return null;
            }

            while (SendPacket(audioStream->index) == 0)
            {
                if (ffmpeg.avcodec_receive_frame(audioCodecContext, frame) == 0)
                    return frame;
            }

            isAudioFrameEnded = true;

            // デコーダに null パケットを送り終端を通知
            if (ffmpeg.avcodec_send_packet(audioCodecContext, null) < 0)
            {
                ffmpeg.av_frame_free(&frame);
                throw new InvalidOperationException("デコーダへの null パケット送信に失敗しました。");
            }

            if (ffmpeg.avcodec_receive_frame(audioCodecContext, frame) == 0)
                return frame;

            ffmpeg.av_frame_free(&frame);
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
