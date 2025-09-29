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
            string exeDir = AppContext.BaseDirectory;
            string ffmpegDir = Path.Combine(exeDir, "ffmpeg");
            if (!Directory.Exists(ffmpegDir))
                throw new DirectoryNotFoundException($"FFmpeg ディレクトリが見つかりません: {ffmpegDir}");

            ffmpeg.RootPath = ffmpegDir;
            Console.WriteLine($"[FFmpeg] RootPath set: {ffmpeg.RootPath}");


            // ★ FFmpeg.AutoGen が使うライブラリ名とバージョンのマップを確認
            foreach (var kv in ffmpeg.LibraryVersionMap)
            {
                string dllName = $"{kv.Key}-{kv.Value}.dll";
                string fullPath = Path.Combine(ffmpeg.RootPath, dllName);

                if (!File.Exists(fullPath))
                {
                    Console.WriteLine($"[Error] {dllName} が見つかりません: {fullPath}");
                    continue;
                }

                try
                {
                    IntPtr handle = NativeLibrary.Load(fullPath);
                    Console.WriteLine($"[LoadCheck] {dllName} => 成功 (0x{handle.ToInt64():X})");

                    // 特別に avformat-XX.dll なら version 関数を直接呼んでテスト
                    if (kv.Key.Equals("avformat", StringComparison.OrdinalIgnoreCase))
                    {
                        IntPtr proc = NativeLibrary.GetExport(handle, "avformat_version");
                        if (proc != IntPtr.Zero)
                        {
                            delegate* unmanaged[Cdecl]<uint> p = (delegate* unmanaged[Cdecl]<uint>)proc;
                            uint version = p();
                            Console.WriteLine($"[Check] avformat_version => {version}");
                        }
                        else
                        {
                            Console.WriteLine("[Warn] avformat_version がエクスポートされていません。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] {dllName} のロードに失敗: {ex.Message}");
                }
            }
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
        public AVFormatContext* FormatContextPointer => formatContext;

        /// <summary>
        /// 現在の動画ストリーム（AVStream）を取得します。
        /// </summary>
        public AVStream VideoStream => *videoStream;


        /// <summary>
        /// 現在の動画コーデックコンテキスト（AVCodecContext）を取得します。
        /// </summary>
        public AVCodecContext* VideoCodecContextPointer => videoCodecContext;

        /// <summary>
        /// 現在の音声コーデックコンテキスト（AVCodecContext）を取得します。
        /// </summary>
        public AVCodecContext AudioCodecContext => *audioCodecContext;
        public AVCodecContext* AudioCodecContextPointer => audioCodecContext;


        /// <summary>
        /// 現在の音声ストリーム（AVStream）を取得します。
        /// </summary>
        public AVStream AudioStream => *audioStream;

        public AVStream* AudioStreamPointer => audioStream;
        /// <summary>
        /// 指定コーデックに対応する最適なデコーダを探します。
        /// D3D11VA → ベンダー固有HW → ソフトウェアの順で選択。
        /// </summary>
        private unsafe AVCodec* TryGetHardwareDecoder(AVCodecID codecId)
        {
            Console.WriteLine("[Info] 利用可能なハードウェアデバイス一覧:");
            AVHWDeviceType type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                Console.WriteLine($"  - {type}");

            // ① D3D11VA デコーダを優先
            string? d3d11DecoderName = codecId switch
            {
                AVCodecID.AV_CODEC_ID_H264 => "h264_d3d11va",
                AVCodecID.AV_CODEC_ID_HEVC => "hevc_d3d11va",
                _ => null
            };
            if (d3d11DecoderName != null)
            {
                AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(d3d11DecoderName);
                if (codec != null)
                {
                    Console.WriteLine($"[Info] D3D11VA デコーダを使用: {d3d11DecoderName}");
                    return codec;
                }
                Console.WriteLine($"[Warn] D3D11VA デコーダ {d3d11DecoderName} が見つかりません。");
            }

            // ② GPU ベンダー固有デコーダ
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
                Console.WriteLine($"[Warn] GPU検出に失敗: {ex.Message}");
            }

            (bool available, string? decoderName)[] candidates =
            {
        (hasNvidia, codecId == AVCodecID.AV_CODEC_ID_H264 ? "h264_cuvid" : codecId == AVCodecID.AV_CODEC_ID_HEVC ? "hevc_cuvid" : null),
        (hasIntel,  codecId == AVCodecID.AV_CODEC_ID_H264 ? "h264_qsv"   : codecId == AVCodecID.AV_CODEC_ID_HEVC ? "hevc_qsv"   : null),
        (hasAMD,    codecId == AVCodecID.AV_CODEC_ID_H264 ? "h264_amf"   : codecId == AVCodecID.AV_CODEC_ID_HEVC ? "hevc_amf"   : null),
    };

            foreach (var (available, name) in candidates)
            {
                if (available && name != null)
                {
                    AVCodec* codec = ffmpeg.avcodec_find_decoder_by_name(name);
                    if (codec != null && codec->id == codecId)
                    {
                        Console.WriteLine($"[Info] ベンダー固有デコーダを使用: {name}");
                        return codec;
                    }
                }
            }

            // ③ ソフトウェアフォールバック
            AVCodec* fallback = ffmpeg.avcodec_find_decoder(codecId);
            Console.WriteLine($"[Info] ソフトウェアデコーダを使用: {ffmpeg.avcodec_get_name(codecId)}");
            return fallback;
        }

        /// <summary>
        /// メディアファイルを開き、ストリーム情報を解析して VideoInfo を返します。
        /// </summary>
        public unsafe VideoInfo OpenFile(string path)
        {
            AVFormatContext* _formatContext = null;
            AVDictionary* formatOptions = null;
            try
            {
                // デバッグ情報出力
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    Console.WriteLine($"Loaded Assembly: {asm.FullName}");

                Console.WriteLine($"FFmpeg RootPath: {ffmpeg.RootPath}");
                Console.WriteLine($"FFmpeg avformat version: {ffmpeg.avformat_version()}");
                Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");

                // ストリーム解析オプション設定
                ffmpeg.av_dict_set(&formatOptions, "probesize", "512000", 0);
                ffmpeg.av_dict_set(&formatOptions, "analyzeduration", "1000000", 0);

                // 入力ファイルを開く
                int ret = ffmpeg.avformat_open_input(&_formatContext, path, null, null);
                if (ret < 0)
                {
                    var errbuf = stackalloc byte[1024];
                    ffmpeg.av_strerror(ret, errbuf, 1024);
                    throw new InvalidOperationException($"指定されたファイルを開けません: {Marshal.PtrToStringAnsi((nint)errbuf)}");
                }

                formatContext = _formatContext;
                formatContext->max_analyze_duration = 1000000;

                // ストリーム情報解析
                ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                if (ret < 0)
                    throw new InvalidOperationException("ストリーム情報の取得に失敗しました。");

                var videoInfo = GetVideoInfo(formatContext, path);
                PrintVideoInfo(videoInfo);

                // 最初の映像・音声ストリームを記録
                videoStream = GetFirstVideoStream();
                audioStream = GetFirstAudioStream();

                return videoInfo;
            }
            finally
            {
                ffmpeg.av_dict_free(&formatOptions);
            }
        }

        public static void PrintVideoInfo(VideoInfo info)
        {
            Console.WriteLine($"--- メディア情報: {info.FilePath} ---");

            Console.WriteLine($"再生時間: {info.Duration.Milliseconds:F0} ミリ秒");
            Console.WriteLine($"ストリーム数: {info.StreamCount}");
            Console.WriteLine($"ビットレート: {info.BitRate.BitsPerSecond} bps");

            foreach (var v in info.VideoStreams)
            {
                Console.WriteLine($"\n[映像ストリーム #{v.Index}]");
                Console.WriteLine($"  コーデック: {v.CodecName}");
                Console.WriteLine($"  解像度: {v.Resolution.Width} x {v.Resolution.Height}");
                Console.WriteLine($"  推定FPS: {v.Fps:F3}");
                Console.WriteLine($"  タイムベース: {v.TimeBase.Num}/{v.TimeBase.Den}");
            }

            foreach (var a in info.AudioStreams)
            {
                Console.WriteLine($"\n[音声ストリーム #{a.Index}]");
                Console.WriteLine($"  コーデック: {a.CodecName}");
                Console.WriteLine($"  サンプルレート: {a.SampleRate} Hz");
                Console.WriteLine($"  チャンネル数: {a.Channels}");
                Console.WriteLine($"  タイムベース: {a.TimeBase.Num}/{a.TimeBase.Den}");
            }

            foreach (var o in info.OtherStreams)
            {
                Console.WriteLine($"\n[その他ストリーム #{o.Index}] 種類: {o.StreamType}");
            }

            Console.WriteLine($"--- メディア情報の出力完了 ---");
        }

        /// <summary>
        /// AVFormatContext からメタ情報を抽出して VideoInfo に詰めます。
        /// </summary>
        public unsafe VideoInfo GetVideoInfo(AVFormatContext* formatContext, string path)
        {
            if (formatContext == null)
            {
                Console.WriteLine("[Error] AVFormatContext が null");
                return null;
            }

            var info = new VideoInfo
            {
                FilePath = path,
                Duration = new TimeInfo { Milliseconds = formatContext->duration / (double)ffmpeg.AV_TIME_BASE * 1000 },
                StreamCount = (int)formatContext->nb_streams,
                BitRate = new BitRateInfo { BitsPerSecond = formatContext->bit_rate }
            };

            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                AVStream* stream = formatContext->streams[i];
                AVCodecParameters* codecpar = stream->codecpar;
                AVRational tb = stream->time_base;

                switch (codecpar->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        double fps = stream->r_frame_rate.den != 0
                            ? stream->r_frame_rate.num / (double)stream->r_frame_rate.den
                            : 0.0;
                        info.VideoStreams.Add(new VideoStreamInfo
                        {
                            Index = i,
                            CodecName = ffmpeg.avcodec_get_name(codecpar->codec_id),
                            Resolution = new ResolutionInfo { Width = codecpar->width, Height = codecpar->height },
                            Fps = fps,
                            TimeBase = new Rational { Num = tb.num, Den = tb.den }
                        });
                        break;

                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        info.AudioStreams.Add(new AudioStreamInfo
                        {
                            Index = i,
                            CodecName = ffmpeg.avcodec_get_name(codecpar->codec_id),
                            SampleRate = codecpar->sample_rate,
                            Channels = codecpar->ch_layout.nb_channels,
                            TimeBase = new Rational { Num = tb.num, Den = tb.den }
                        });
                        break;

                    default:
                        info.OtherStreams.Add(new OtherStreamInfo
                        {
                            Index = i,
                            StreamType = codecpar->codec_type.ToString()
                        });
                        break;
                }
            }

            return info;
        }

        /// <summary>
        /// 映像・音声デコーダを初期化し、必要ならハードウェアコンテキストを作成。
        /// </summary>
        public unsafe void InitializeDecoders()
        {
            if (videoStream != null && videoCodecContext == null)
            {
                videoCodec = TryGetHardwareDecoder(videoStream->codecpar->codec_id);
                if (videoCodec == null)
                    throw new InvalidOperationException("対応する映像デコーダが見つかりません。");

                videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                if (videoCodecContext == null)
                    throw new InvalidOperationException("映像コーデックコンテキストの確保に失敗しました。");

                ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar)
                    .OnError(() => throw new InvalidOperationException("映像コーデックパラメータの適用に失敗しました。"));

                // ハードウェア種別を判定
                string codecName = Marshal.PtrToStringAnsi((nint)videoCodec->name);
                videoHardwareType =
                    codecName.Contains("d3d11va") ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA :
                    codecName.Contains("qsv") ? AVHWDeviceType.AV_HWDEVICE_TYPE_QSV :
                    (codecName.Contains("cuda") || codecName.Contains("cuvid")) ? AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA :
                    (AVHWDeviceType?)null;

                // ハードウェアデバイス初期化
                if (videoHardwareType is AVHWDeviceType hwType)
                {
                    AVBufferRef* hw_device_ctx = null;
                    int result = ffmpeg.av_hwdevice_ctx_create(&hw_device_ctx, hwType, null, null, 0);
                    if (result >= 0)
                    {
                        videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hw_device_ctx);
                        Console.WriteLine($"[Info] ハードウェアデコード使用: {hwType}");
                    }
                    else
                    {
                        var errbuf = stackalloc byte[1024];
                        ffmpeg.av_strerror(result, errbuf, 1024);
                        Console.WriteLine($"[Warn] ハードウェアデバイス初期化失敗 ({hwType}): {Marshal.PtrToStringAnsi((nint)errbuf)}");
                        Console.WriteLine("[Info] ソフトウェアデコードにフォールバックします。");

                        videoCodec = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);
                        videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                        ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar);
                        videoHardwareType = null;
                    }
                }

                // デコーダオープン
                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "threads", "1", 0);
                ffmpeg.avcodec_open2(videoCodecContext, videoCodec, &opts)
                    .OnError(() => throw new InvalidOperationException("映像デコーダの初期化に失敗しました。"));
                ffmpeg.av_dict_free(&opts);
            }

            if (audioStream != null && audioCodecContext == null)
            {
                audioCodec = ffmpeg.avcodec_find_decoder(audioStream->codecpar->codec_id);
                if (audioCodec == null)
                    throw new InvalidOperationException("対応する音声デコーダが見つかりません。");

                audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                if (audioCodecContext == null)
                    throw new InvalidOperationException("音声コーデックコンテキストの確保に失敗しました。");


                ffmpeg.avcodec_parameters_to_context(audioCodecContext, audioStream->codecpar)
                    .OnError(() => throw new InvalidOperationException("音声コーデックパラメータの適用に失敗しました。"));

                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "threads", "1", 0);
                ffmpeg.avcodec_open2(audioCodecContext, audioCodec, &opts)
                    .OnError(() => throw new InvalidOperationException("音声デコーダの初期化に失敗しました。"));
                ffmpeg.av_dict_free(&opts);
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

        // Decoder 内に追加
        public void ClearInternalQueues()
        {
            lock (sendPackedSyncObject)
            {
                while (videoPackets.Count > 0)
                    videoPackets.Dequeue()?.Dispose();
                while (audioPackets.Count > 0)
                    audioPackets.Dequeue()?.Dispose();

                isVideoFrameEnded = false;
                isAudioFrameEnded = false;
            }
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
