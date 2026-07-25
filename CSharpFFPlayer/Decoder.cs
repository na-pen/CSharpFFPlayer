using FFmpeg.AutoGen;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using ManagedCuda.VectorTypes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace CSharpFFPlayer
{
    public static class FFmpegErrors
    {
        public const int AVERROR_EAGAIN = -11;             // 一時的なリソース不足
        public const int AVERROR_EOF = -541478725;      // End of File
        public const int AVERROR_EINVAL = -22;             // 無効な引数
        public const int AVERROR_EIO = -5;              // I/O エラー
    }

    public enum FrameReadResult
    {
        FrameAvailable,  // フレーム取得
        FrameNotReady,   // まだ準備できていない（EAGAIN）
        EndOfStream      // EOF
    }

    /// <summary>
    /// FFmpeg デコーダ（必要に応じて CUDA NV12→BGRA カーネル使用）。
    /// 描画は別レイヤ（UnifiedImageWriter など）に委譲します。
    /// </summary>
    public unsafe class Decoder : IDisposable
    {
        // ====================== Fields ======================
        private AVFormatContext* formatContext;
        private AVStream* videoStream;
        private AVStream* audioStream;

        private AVCodec* videoCodec;
        private AVCodec* audioCodec;

        private AVCodecContext* videoCodecContext;
        private AVCodecContext* audioCodecContext;

        private AVHWDeviceType? videoHardwareType = null;
        private AVHWDeviceType? hwType = null;

        private bool isDisposed;

        // --- CUDA (decode-thread 専有) ---
        private CudaContext _decCudaCtx;
        private CudaKernel _decNv12ToBgra;
        private CudaDeviceVariable<byte> _decOutBgra;
        private bool _decCudaReady;

        // ====================== Ctor ======================
        public Decoder()
        {
            string exeDir = AppContext.BaseDirectory;
            string ffmpegDir = Path.Combine(exeDir, "ffmpeg");
            if (!Directory.Exists(ffmpegDir))
                throw new DirectoryNotFoundException($"FFmpeg ディレクトリが見つかりません: {ffmpegDir}");

            ffmpeg.RootPath = ffmpegDir;
            Log($"[FFmpeg] RootPath: {ffmpeg.RootPath}");

            // 主要 DLL のロード確認
            foreach (var kv in ffmpeg.LibraryVersionMap)
            {
                var dllName = $"{kv.Key}-{kv.Value}.dll";
                var full = Path.Combine(ffmpeg.RootPath, dllName);
                if (!File.Exists(full)) { LogWarn($"欠落: {dllName} ({full})"); continue; }

                try
                {
                    IntPtr h = NativeLibrary.Load(full);
                    Log($"Load OK: {dllName} (0x{h.ToInt64():X})");

                    if (kv.Key.Equals("avformat", StringComparison.OrdinalIgnoreCase))
                    {
                        IntPtr p = NativeLibrary.GetExport(h, "avformat_version");
                        if (p != IntPtr.Zero)
                        {
                            delegate* unmanaged[Cdecl]<uint> f = (delegate* unmanaged[Cdecl]<uint>)p;
                            Log($"avformat_version = {f()}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogErr($"Load NG: {dllName} : {ex.Message}");
                }
            }
        }

        // ====================== Public getters ======================
        public AVFormatContext* FormatContextPointer => formatContext;
        public AVStream VideoStream => *videoStream;
        public AVStream AudioStream => *audioStream;
        public AVStream* AudioStreamPointer => audioStream;
        public AVCodecContext* VideoCodecContextPointer => videoCodecContext;
        public AVCodecContext AudioCodecContext => *audioCodecContext;
        public AVCodecContext* AudioCodecContextPointer => audioCodecContext;

        // ====================== Open / Info ======================
        public unsafe VideoInfo OpenFile(string path)
        {
            AVFormatContext* ctx = null;
            AVDictionary* fmtOpt = null;

            try
            {
                Log($"Open: {path}");
                ffmpeg.av_dict_set(&fmtOpt, "probesize", "512000", 0);
                ffmpeg.av_dict_set(&fmtOpt, "analyzeduration", "1000000", 0);

                int ret = ffmpeg.avformat_open_input(&ctx, path, null, null);
                ThrowIfErr(ret, "avformat_open_input");

                formatContext = ctx;
                formatContext->max_analyze_duration = 1000000;

                ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                ThrowIfErr(ret, "avformat_find_stream_info");

                var info = GetVideoInfo(formatContext, path);
                PrintVideoInfo(info);

                videoStream = GetFirstStream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO);
                audioStream = GetFirstStream(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO);

                Log($"Streams: video={(videoStream != null ? videoStream->index.ToString() : "-")} " +
                    $"audio={(audioStream != null ? audioStream->index.ToString() : "-")}");

                return info;
            }
            finally
            {
                ffmpeg.av_dict_free(&fmtOpt);
            }
        }
        public static void PrintVideoInfo(VideoInfo info)
        {
            if (info == null) { Console.WriteLine("[Info] VideoInfo = null"); return; }

            Console.WriteLine($"--- メディア情報: {info.FilePath} ---");
            Console.WriteLine($"再生時間: {info.Duration.Milliseconds:F0} ms");
            Console.WriteLine($"ストリーム数: {info.StreamCount}");
            Console.WriteLine($"ビットレート: {info.BitRate.BitsPerSecond} bps");

            foreach (var v in info.VideoStreams)
            {
                Console.WriteLine($"\n[映像 #{v.Index}] {v.CodecName}  {v.Resolution.Width}x{v.Resolution.Height}  ~{v.Fps:F3}fps  TB={v.TimeBase.Num}/{v.TimeBase.Den}");
            }
            foreach (var a in info.AudioStreams)
            {
                Console.WriteLine($"\n[音声 #{a.Index}] {a.CodecName}  {a.SampleRate}Hz  ch={a.Channels}  TB={a.TimeBase.Num}/{a.TimeBase.Den}");
            }
            foreach (var o in info.OtherStreams)
            {
                Console.WriteLine($"\n[その他 #{o.Index}] type={o.StreamType}");
            }
            Console.WriteLine($"--- 以上 ---");
        }

        public unsafe VideoInfo GetVideoInfo(AVFormatContext* fc, string path)
        {
            if (fc == null) { LogErr("GetVideoInfo: AVFormatContext = null"); return null; }

            var info = new VideoInfo
            {
                FilePath = path,
                Duration = new TimeInfo { Milliseconds = fc->duration / (double)ffmpeg.AV_TIME_BASE * 1000 },
                StreamCount = (int)fc->nb_streams,
                BitRate = new BitRateInfo { BitsPerSecond = fc->bit_rate }
            };

            for (int i = 0; i < fc->nb_streams; i++)
            {
                AVStream* s = fc->streams[i];
                AVCodecParameters* cp = s->codecpar;
                AVRational tb = s->time_base;

                switch (cp->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        double fps = s->r_frame_rate.den != 0 ? s->r_frame_rate.num / (double)s->r_frame_rate.den : 0.0;
                        info.VideoStreams.Add(new VideoStreamInfo
                        {
                            Index = i,
                            CodecName = ffmpeg.avcodec_get_name(cp->codec_id),
                            Resolution = new ResolutionInfo { Width = cp->width, Height = cp->height },
                            Fps = fps,
                            TimeBase = new Rational { Num = tb.num, Den = tb.den }
                        });
                        break;

                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        info.AudioStreams.Add(new AudioStreamInfo
                        {
                            Index = i,
                            CodecName = ffmpeg.avcodec_get_name(cp->codec_id),
                            SampleRate = cp->sample_rate,
                            Channels = cp->ch_layout.nb_channels,
                            TimeBase = new Rational { Num = tb.num, Den = tb.den }
                        });
                        break;

                    default:
                        info.OtherStreams.Add(new OtherStreamInfo { Index = i, StreamType = cp->codec_type.ToString() });
                        break;
                }
            }
            return info;
        }

        private static AVStream* GetFirstStream(AVFormatContext* fc, AVMediaType type)
        {
            for (int i = 0; i < (int)fc->nb_streams; ++i)
                if (fc->streams[i]->codecpar->codec_type == type) return fc->streams[i];
            return null;
        }

        // ====================== Decoder init ======================
        public unsafe void InitializeDecoders()
        {
            // ---- Video ----
            if (videoStream != null && videoCodecContext == null)
            {
                videoCodec = TryGetHardwareDecoder(videoStream->codecpar->codec_id);
                if (videoCodec == null) throw new InvalidOperationException("映像デコーダが見つかりません。");


                videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                if (videoCodecContext == null)
                    throw new InvalidOperationException("映像コーデックコンテキストの確保に失敗しました。");

                ThrowIfErr(ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar),
                           "avcodec_parameters_to_context(video)");

                // 推定 HW
                string name = Marshal.PtrToStringAnsi((nint)videoCodec->name) ?? "";
                videoHardwareType =
                      name.Contains("d3d11va") ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
                    : name.Contains("qsv") ? AVHWDeviceType.AV_HWDEVICE_TYPE_QSV
                    : (name.Contains("cuda") || name.Contains("cuvid")) ? AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA
                    : (AVHWDeviceType?)null;

                if (videoHardwareType is AVHWDeviceType t)
                {
                    hwType = t;
                    AVBufferRef* dev = null;
                    int rc = ffmpeg.av_hwdevice_ctx_create(&dev, (AVHWDeviceType)t, null, null, 0);
                    if (rc >= 0)
                    {
                        videoCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(dev);
                        Log($"HW Decode: {t}");
                    }
                    else
                    {
                        LogWarn($"HW device init NG ({t}): {ErrStr(rc)} → SW fallback");
                        videoCodec = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);
                        videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
                        ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar);
                        videoHardwareType = null;
                    }
                }

                AVDictionary* vopt = null;
                ffmpeg.av_dict_set(&vopt, "threads", "1", 0);
                ThrowIfErr(ffmpeg.avcodec_open2(videoCodecContext, videoCodec, &vopt), "avcodec_open2(video)");
                ffmpeg.av_dict_free(&vopt);
            }

            // ---- Audio ----
            if (audioStream != null && audioCodecContext == null)
            {
                audioCodec = ffmpeg.avcodec_find_decoder(audioStream->codecpar->codec_id);
                if (audioCodec == null)
                    throw new InvalidOperationException("対応する音声デコーダが見つかりません。");

                audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                if (audioCodecContext == null)
                    throw new InvalidOperationException("音声コーデックコンテキストの確保に失敗しました。");

                ThrowIfErr(ffmpeg.avcodec_parameters_to_context(audioCodecContext, audioStream->codecpar),
                           "avcodec_parameters_to_context(audio)");

                AVDictionary* aopt = null;
                ffmpeg.av_dict_set(&aopt, "threads", "1", 0);
                ThrowIfErr(ffmpeg.avcodec_open2(audioCodecContext, audioCodec, &aopt), "avcodec_open2(audio)");
                ffmpeg.av_dict_free(&aopt);
            }
        }

        /// <summary> 利用可能な HW デコーダを探索（D3D11VA → ベンダー固有 → SW）。</summary>
        private unsafe AVCodec* TryGetHardwareDecoder(AVCodecID codecId)
        {
            Log("HW devices:");
            AVHWDeviceType t = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            while ((t = ffmpeg.av_hwdevice_iterate_types(t)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                Log($"  - {t}");

            // D3D11VA 優先
            string? d3d11 = codecId switch
            {
                AVCodecID.AV_CODEC_ID_H264 => "h264_d3d11va",
                AVCodecID.AV_CODEC_ID_HEVC => "hevc_d3d11va",
                _ => null
            };
            if (d3d11 != null)
            {
                var c = ffmpeg.avcodec_find_decoder_by_name(d3d11);
                if (c != null) { Log($"Use D3D11VA: {d3d11}"); return c; }
                LogWarn($"D3D11VA {d3d11} not found");
            }

            // ベンダー固有
            bool hasNvidia = false, hasIntel = false, hasAMD = false;
            try
            {
                using var q = new ManagementObjectSearcher("Select * from Win32_VideoController");
                foreach (var a in q.Get())
                {
                    string n = a["Name"]?.ToString() ?? "";
                    if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) hasNvidia = true;
                    if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) hasIntel = true;
                    if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) hasAMD = true;
                }
            }
            catch (Exception ex) { LogWarn($"GPU 検出失敗: {ex.Message}"); }

            (bool ok, string name)[] cand =
            {
                (hasNvidia, codecId==AVCodecID.AV_CODEC_ID_H264 ? "h264_cuvid" : codecId==AVCodecID.AV_CODEC_ID_HEVC ? "hevc_cuvid" : null),
                (hasIntel,  codecId==AVCodecID.AV_CODEC_ID_H264 ? "h264_qsv"   : codecId==AVCodecID.AV_CODEC_ID_HEVC ? "hevc_qsv"   : null),
                (hasAMD,    codecId==AVCodecID.AV_CODEC_ID_H264 ? "h264_amf"   : codecId==AVCodecID.AV_CODEC_ID_HEVC ? "hevc_amf"   : null),
            };

            foreach (var (ok, name) in cand)
            {
                if (!ok || name == null) continue;
                var c = ffmpeg.avcodec_find_decoder_by_name(name);
                if (c != null && c->id == codecId) { Log($"Use Vendor HW: {name}"); return c; }
            }

            // SW fallback
            var sw = ffmpeg.avcodec_find_decoder(codecId);
            Log($"Use SW: {ffmpeg.avcodec_get_name(codecId)}");
            return sw;
        }

        // ====================== Read video/audio ======================
        private readonly object sendPackedSyncObject = new();
        private readonly Queue<AVPacketPtr> videoPackets = new();
        private readonly Queue<AVPacketPtr> audioPackets = new();

        public int SendPacket(int streamIndex)
        {
            lock (sendPackedSyncObject)
            {
                AVCodecContext* ctx;
                Queue<AVPacketPtr> queue;
                SelectCodecAndQueue(streamIndex, out ctx, out queue);

                // 先にキューに溜めてあるパケットを消化
                if (queue.TryDequeue(out var queued))
                {
                    int sret = ffmpeg.avcodec_send_packet(ctx, queued.Ptr);
                    queued.Dispose();
                    ThrowIfErr(sret, "avcodec_send_packet(queued)");
                    return 0;
                }

                // 読み進めて該当ストリームのパケットを送る
                while (true)
                {
                    AVPacket pkt = new AVPacket();
                    int r = ffmpeg.av_read_frame(formatContext, &pkt);
                    if (r < 0) return -1; // EOF or read error

                    try
                    {
                        int idx = pkt.stream_index;
                        if (idx == videoStream->index || idx == audioStream->index)
                        {
                            bool isTarget = (idx == streamIndex);
                            var c = (idx == videoStream->index) ? videoCodecContext : audioCodecContext;
                            var q = (idx == videoStream->index) ? videoPackets : audioPackets;

                            if (isTarget)
                            {
                                int sret = ffmpeg.avcodec_send_packet(c, &pkt);
                                ThrowIfErr(sret, "avcodec_send_packet(target)");
                                return 0;
                            }
                            else
                            {
                                AVPacket* cloned = ffmpeg.av_packet_clone(&pkt);
                                q.Enqueue(new AVPacketPtr(cloned));
                            }
                        }
                    }
                    finally
                    {
                        ffmpeg.av_packet_unref(&pkt);
                    }
                }
            }
        }



        private unsafe void SelectCodecAndQueue(
            int streamIndex,
            out AVCodecContext* ctx,
            out Queue<AVPacketPtr> queue)
        {
            if (videoStream != null && streamIndex == videoStream->index)
            { ctx = videoCodecContext; queue = videoPackets; return; }

            if (audioStream != null && streamIndex == audioStream->index)
            { ctx = audioCodecContext; queue = audioPackets; return; }

            ctx = null; queue = null;
            throw new InvalidOperationException($"未知のストリーム: {streamIndex}");
        }

        public unsafe (FrameReadResult result, ManagedFrame frame) TryReadFrame()
        {
            var raw = TryReadUnsafeFrame(out var result);
            return result == FrameReadResult.FrameAvailable ? (result, new ManagedFrame(raw, hwType)) : (result, null);
        }

        private unsafe AVFrame* TryReadUnsafeFrame(out FrameReadResult result)
        {
            AVFrame* f = ffmpeg.av_frame_alloc();
            int r = ffmpeg.avcodec_receive_frame(videoCodecContext, f);

            if (r == 0) { result = FrameReadResult.FrameAvailable; return f; }

            if (r == FFmpegErrors.AVERROR_EAGAIN)
            {
                int s = SendPacket(videoStream->index);
                if (s == 0)
                {
                    r = ffmpeg.avcodec_receive_frame(videoCodecContext, f);
                    if (r == 0) { result = FrameReadResult.FrameAvailable; return f; }
                }
                else if (s == -1)
                {
                    ffmpeg.avcodec_send_packet(videoCodecContext, null);
                    r = ffmpeg.avcodec_receive_frame(videoCodecContext, f);
                    if (r == 0) { result = FrameReadResult.FrameAvailable; return f; }
                    if (r == ffmpeg.AVERROR_EOF) { ffmpeg.av_frame_free(&f); result = FrameReadResult.EndOfStream; return null; }
                }

                if (r == FFmpegErrors.AVERROR_EAGAIN) { ffmpeg.av_frame_free(&f); result = FrameReadResult.FrameNotReady; return null; }
            }

            if (r == ffmpeg.AVERROR_EOF) { ffmpeg.av_frame_free(&f); result = FrameReadResult.EndOfStream; return null; }

            ffmpeg.av_frame_free(&f);
            ThrowIfErr(r, "avcodec_receive_frame(video)");
            result = FrameReadResult.FrameNotReady; // 到達しない
            return null;
        }

        public unsafe (FrameReadResult result, ManagedFrame frame) TryReadAudioFrame()
        {
            var raw = TryReadUnsafeAudioFrame(out var result);
            return (result, raw == null ? null : new ManagedFrame(raw, hwType));
        }

        public unsafe AVFrame* TryReadUnsafeAudioFrame(out FrameReadResult result)
        {
            AVFrame* f = ffmpeg.av_frame_alloc();
            if (f == null) throw new Exception("音声フレームの確保に失敗。");

            int r = ffmpeg.avcodec_receive_frame(audioCodecContext, f);
            if (r == 0) { result = FrameReadResult.FrameAvailable; return f; }

            if (r == FFmpegErrors.AVERROR_EAGAIN)
            {
                int s = SendPacket(audioStream->index);
                if (s == 0)
                {
                    r = ffmpeg.avcodec_receive_frame(audioCodecContext, f);
                    if (r == 0) { result = FrameReadResult.FrameAvailable; return f; }
                }
                else if (s == -1)
                {
                    ffmpeg.avcodec_send_packet(audioCodecContext, null);
                    r = ffmpeg.avcodec_receive_frame(audioCodecContext, f);
                    if (r == 0) { result = FrameReadResult.FrameAvailable; return f; }
                    if (r == ffmpeg.AVERROR_EOF) { ffmpeg.av_frame_free(&f); result = FrameReadResult.EndOfStream; return null; }
                }

                if (r == FFmpegErrors.AVERROR_EAGAIN) { ffmpeg.av_frame_free(&f); result = FrameReadResult.FrameNotReady; return null; }
            }

            if (r == ffmpeg.AVERROR_EOF) { ffmpeg.av_frame_free(&f); result = FrameReadResult.EndOfStream; return null; }

            ffmpeg.av_frame_free(&f);
            ThrowIfErr(r, "avcodec_receive_frame(audio)");
            result = FrameReadResult.FrameNotReady; // 到達しない
            return null;
        }

        public void ClearInternalQueues()
        {
            lock (sendPackedSyncObject)
            {
                while (videoPackets.Count > 0) videoPackets.Dequeue()?.Dispose();
                while (audioPackets.Count > 0) audioPackets.Dequeue()?.Dispose();
            }
        }

        // ====================== CUDA: NV12 → BGRA ======================
        [StructLayout(LayoutKind.Sequential)]
        private struct AVCUDADeviceContext { public IntPtr cuda_ctx; }

        private unsafe void EnsureCudaOnDecodeThreadInitialized(AVFrame* hwFrame)
        {
            if (_decCudaReady) return;

            // 1) 現在のスレッドに current ctx があるか？
            CUcontext cur = new CUcontext();
            var rc = DriverAPINativeMethods.ContextManagement.cuCtxGetCurrent(ref cur);

            // 2) 無ければ FFmpeg の device ctx から取得して current にする
            if (rc != CUResult.Success || cur.Pointer == IntPtr.Zero)
            {
                if (hwFrame == null || hwFrame->hw_frames_ctx == null)
                    throw new InvalidOperationException("CUDA ctx attach 失敗: hw_frames_ctx が無効。");

                var frames = (AVHWFramesContext*)hwFrame->hw_frames_ctx->data;
                var devRef = frames->device_ref;
                if (devRef == null) throw new InvalidOperationException("CUDA ctx attach 失敗: device_ref が null。");

                var devCtx = (AVHWDeviceContext*)devRef->data;
                if (devCtx == null || devCtx->hwctx == null)
                    throw new InvalidOperationException("CUDA ctx attach 失敗: device_ctx/hwctx が無効。");

                var cuDev = (AVCUDADeviceContext*)devCtx->hwctx;
                if (cuDev->cuda_ctx == IntPtr.Zero)
                    throw new InvalidOperationException("CUDA ctx attach 失敗: cuda_ctx が null。");

                var want = new CUcontext { Pointer = cuDev->cuda_ctx };
                var rcSet = DriverAPINativeMethods.ContextManagement.cuCtxSetCurrent(want);
                if (rcSet != CUResult.Success)
                    throw new InvalidOperationException($"cuCtxSetCurrent 失敗: {rcSet}");
            }

            // 3) 既存 current ctx にバインド（新規作成しない）
            _decCudaCtx = new CudaContext(0, CUCtxFlags.SchedAuto, createNew: false);
            _decCudaCtx.SetCurrent();

            // 4) PTX / カーネル（遅延ロード）
            string ptxPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nv12_to_bgra.ptx");
            if (!File.Exists(ptxPath)) throw new FileNotFoundException("PTX not found", ptxPath);

            var module = _decCudaCtx.LoadModulePTX(File.ReadAllBytes(ptxPath));
            _decNv12ToBgra = new CudaKernel("Nv12ToBgraKernel", module, _decCudaCtx);

            _decCudaReady = true;
            Log("CUDA: kernel ready");
        }

        // キャッシュ用フィールド
        private int _cachedWidth;
        private int _cachedHeight;
        private int _cachedPitchOut;
        private int _cachedBufSize;
        private dim3 _cachedBlock;
        private dim3 _cachedGrid;

        // ==== BGRA ホストバッファのプール ====
        // 1920x1080 なら 1 枚 8MB。毎フレーム確保すると LOH を圧迫するため使い回す。
        // 単一の共有バッファにすると、描画待ち(Dispatcher)のバッファを次のフレームが
        // 上書きしてしまうので、表示側が使い終わったものを ReturnBgraBuffer() で戻す。
        // 借用/返却は描画スレッドと UI スレッドの双方から起きるためロックフリーな
        // ConcurrentBag を使う（呼び出し側は描画差し替えロックを保持していることがある）。
        private const int BGRA_POOL_MAX = 4;

        private readonly ConcurrentBag<byte[]> _bgraPool = new();
        private int _bgraPoolCount;      // ConcurrentBag.Count は内部ロックを取るため自前で数える
        private int _bgraBufferSize = -1;

        /// <summary>BGRA 1 フレーム分のバッファを借りる。</summary>
        private byte[] RentBgraBuffer(int size)
        {
            if (size != _bgraBufferSize)
            {
                // 解像度や出力形式が変わったら古いサイズのバッファは捨てる
                _bgraPool.Clear();
                Interlocked.Exchange(ref _bgraPoolCount, 0);
                _bgraBufferSize = size;
            }

            if (_bgraPool.TryTake(out var buffer))
            {
                Interlocked.Decrement(ref _bgraPoolCount);
                return buffer;
            }
            return new byte[size];
        }

        /// <summary>
        /// GetBgraFrame() が返したバッファを返却する。
        /// 表示側が使い終わったタイミングで呼ぶこと（呼ばなくても GC されるだけ）。
        /// </summary>
        public void ReturnBgraBuffer(byte[] buffer)
        {
            if (buffer == null || buffer.Length != _bgraBufferSize) return;

            // 際限なく溜め込まない
            if (Interlocked.Increment(ref _bgraPoolCount) > BGRA_POOL_MAX)
            {
                Interlocked.Decrement(ref _bgraPoolCount);
                return;
            }
            _bgraPool.Add(buffer);
        }

        private void EnsureCachedParams(int w, int h)
        {
            if (w == _cachedWidth && h == _cachedHeight)
                return; // 解像度が同じなら再計算不要

            _cachedWidth = w;
            _cachedHeight = h;

            _cachedPitchOut = w * 4;
            _cachedBufSize = _cachedPitchOut * h;

            _cachedBlock = new dim3(32, 16);
            _cachedGrid = new dim3((w + 31) / 32, (h + 15) / 16);

            // デバイス側バッファ確保
            _decOutBgra?.Dispose();
            _decOutBgra = new CudaDeviceVariable<byte>(_cachedBufSize);

            Log($"CUDA: setup for {w}x{h}, buf={_cachedBufSize} bytes");
        }

        private unsafe byte[] ConvertCudaNV12FrameToBgra_OnDecodeThread(AVFrame* hwFrame, bool bt709 = true)
        {
            EnsureCudaOnDecodeThreadInitialized(hwFrame);
            _decCudaCtx.SetCurrent();

            int w = hwFrame->width;
            int h = hwFrame->height;

            // 必要ならキャッシュ更新 & バッファ再確保
            EnsureCachedParams(w, h);

            _decNv12ToBgra.BlockDimensions = _cachedBlock;
            _decNv12ToBgra.GridDimensions = _cachedGrid;

            _decNv12ToBgra.Run(
                new CUdeviceptr((ulong)hwFrame->data[0]), hwFrame->linesize[0],
                new CUdeviceptr((ulong)hwFrame->data[1]), hwFrame->linesize[1],
                _decOutBgra.DevicePointer, _cachedPitchOut,
                _cachedWidth, _cachedHeight, bt709 ? 1 : 0
            );
            _decCudaCtx.Synchronize();

            // CopyToHost はデバイス側のサイズ分をコピーするため、ぴったりのサイズで借りる
            byte[] host = RentBgraBuffer(_cachedBufSize);
            _decOutBgra.CopyToHost(host);
            return host;
        }



        /// <summary>
        /// CUDA ハードウェアフレーム(NV12)→BGRA。CPU フレームは conv にフォールバック。
        /// 戻り値はプールから借りたバッファ。表示が終わったら ReturnBgraBuffer() で返すこと。
        /// </summary>
        public unsafe byte[] GetBgraFrame(ManagedFrame managed, FrameConveter conv, bool bt709 = true)
        {
            if (managed == null || managed.Frame == null) throw new ArgumentNullException(nameof(managed));
            AVFrame* f = managed.Frame;

            if (videoHardwareType == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA &&
                managed.IsGpuFrame &&
                f->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA)
            {
                return ConvertCudaNV12FrameToBgra_OnDecodeThread(f, bt709);
            }

            // CUDA 以外（QSV/AMF/ソフトウェアデコード）の経路。
            // 以前はフレーム毎に byte[] を新規確保していたためプールから借りる。
            byte[] host = RentBgraBuffer(conv.DestinationBufferSize);
            conv.ConvertFrameToBuffer(managed, host);
            return host;
        }

        // ====================== AVPacket holder ======================
        public class AVPacketPtr : IDisposable
        {
            public AVPacket* Ptr { get; }
            public AVPacketPtr(AVPacket* p) { Ptr = p; }
            public void Dispose()
            {
                if (Ptr != null)
                {
                    ffmpeg.av_packet_unref(Ptr);
                    AVPacket* tmp = Ptr;
                    ffmpeg.av_packet_free(&tmp);
                }
            }
        }

        // ====================== Dispose ======================
        ~Decoder() { DisposeUnManaged(); }

        public void Dispose()
        {
            DisposeUnManaged();
            GC.SuppressFinalize(this);
        }

        private void DisposeUnManaged()
        {
            if (isDisposed) return;

            // BGRA バッファプールを解放
            _bgraPool.Clear();
            Interlocked.Exchange(ref _bgraPoolCount, 0);

            // CUDA
            try
            {
                _decOutBgra?.Dispose(); _decOutBgra = null;
                _decCudaCtx?.Dispose(); _decCudaCtx = null;
                _decCudaReady = false;
            }
            catch { /* ignore */ }


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

            // Queues
            ClearInternalQueues();

            isDisposed = true;
            Log("Decoder disposed");
        }

        // ====================== Helpers ======================
        private static void ThrowIfErr(int err, string api)
        {
            if (err < 0) throw new InvalidOperationException($"{api} failed: {ErrStr(err)} ({err})");
        }

        private static string ErrStr(int err)
        {
            var buf = stackalloc byte[1024];
            ffmpeg.av_strerror(err, buf, 1024);
            return Marshal.PtrToStringAnsi((nint)buf) ?? $"err={err}";
        }

        private static void Log(string msg)
        {
            var tid = Environment.CurrentManagedThreadId;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][T{tid}] {msg}");
        }
        private static void LogWarn(string msg) => Log("[Warn] " + msg);
        private static void LogErr(string msg) => Log("[Error] " + msg);
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
