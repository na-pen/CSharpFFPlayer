using FFmpeg.AutoGen;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CSharpFFPlayer
{
    public enum PlaybackState
    {
        Stopped,
        Playing,
        Paused,
        Buffering,
        Seeking,
        SeekBuffering,
        EndedStream,
        Ended
    }

    /// <summary>
    /// 映像/音声の再生制御・同期・バッファリングを担当するコントローラ。
    /// - Producer(デコード) → GPU/CPUキュー → UI提示
    /// - CPUターゲット(WriteableBitmap)とD3Dターゲット(D3DImage)の分岐をできる限り共通化
    /// - 詳細なログ出力付き（フレーム番号、バッファ長、状態遷移など）
    /// </summary>
    public class VideoPlayController
    {
        // --- 表示ターゲット ---
        private RenderTargetType targetType;

        // FFmpeg ピクセルフォーマット（CPU表示/BGR24, D3D表示/BGRA）
        private static readonly AVPixelFormat CpuFfPixFmt = AVPixelFormat.AV_PIX_FMT_BGR24;
        private static readonly AVPixelFormat D3dFfPixFmt = AVPixelFormat.AV_PIX_FMT_BGRA;

        // WPF 側のピクセルフォーマット
        private static readonly PixelFormat WpfPixFmt = PixelFormats.Bgr24;

        // --- しきい値・定数 ---
        private const int GPU_FRAME_TARGET = 20;         // GPUキューの目標深さ
        private const int CPU_FRAME_TARGET = 10;         // CPUキューの目標深さ
        private const int WAIT_TIME_MS = 150;            // バッファ待機のスリープ
        private const int RESUME_GRACE_MS = 250;         // 再開直後のグレース期間
        private const int LOW_WATER_DIV = 3;             // LOW=TARGET/3
        private const int PREFILL_DIV = 2;               // 再開前プリフィル=TARGET/2

        // --- 状態 ---
        private PlaybackState playbackState = PlaybackState.Stopped;
        public bool IsPlaying => playbackState == PlaybackState.Playing;
        public bool IsPaused => playbackState == PlaybackState.Paused;
        public bool IsBuffering => playbackState == PlaybackState.Buffering;
        public bool IsEnded => playbackState == PlaybackState.Ended;

        // --- デコーダ/描画 ---
        private Decoder decoder;
        private UnifiedImageWriter imageWriter;
        private FrameConveter frameConveter;

        // --- FPS/タイミング ---
        private AVRational rawFps;
        private AVRational videoFps;
        private float fps;
        private float baseFrameDurationMs;

        // --- キュー/同期 ---
        private readonly ConcurrentQueue<ManagedFrame> gpuFrames = new();
        private readonly ConcurrentQueue<ManagedFrame> cpuFrames = new();
        private readonly SemaphoreSlim decoderLock = new(1, 1);   // FFmpeg 呼び出しの直列化
        private readonly SemaphoreSlim seekLock = new(1, 1);

        // Producer の停止制御
        private volatile bool producerPaused = false;
        private TaskCompletionSource<bool>? producerQuiescedTcs;

        // 再生タスク
        private Task playTask;

        // 音声
        private AudioPlayer audioPlayer;

        // 現在のフレームインデックス
        private int frameIndex = 0;
        public int FrameIndex => frameIndex;

        // 直近エンキュー済みのインデックス（重複防止）
        private long lastEnqueuedFrameIndex = -1;

        // 再生再開を許可するか（Play で true、Pause/Seek 後は false）
        private volatile bool allowAutoResume = false;

        // シーク/終了フラグ類
        private volatile bool isSeeking = false;
        private long seekPrefetchEndFrameIndex = -1;
        private bool endedStreamVideo = false;
        private bool endedStreamAudio = false;

        // フレーム番号推定のための観測値
        private volatile int frameIndexFactor = 1;
        private long? lastObservedPts = null;

        // 追加
        private VideoInfo? videoInfo;  // ファイルを開いた後にセット
        public VideoInfo VideoInfo => videoInfo
            ?? throw new InvalidOperationException("OpenFile() を先に呼んでください。");


        // ----------------------------------
        // ログユーティリティ
        // ----------------------------------
        private static string Now => DateTime.Now.ToString("HH:mm:ss.fff");
        private void Log(string msg) => Console.WriteLine($"[{Now}] [INFO ] {msg}");
        private void Warn(string msg) => Console.WriteLine($"[{Now}] [WARN ] {msg}");
        private void Err(string msg) => Console.WriteLine($"[{Now}] [ERROR] {msg}");

        private void LogBuffers(string tag = "")
        {
            Log($"{tag} Queues: CPU={cpuFrames.Count}, GPU={gpuFrames.Count}, State={playbackState}");
        }

        private void SetState(PlaybackState s, string reason = "")
        {
            if (playbackState == s) return;
            Log($"State: {playbackState} -> {s} {(string.IsNullOrEmpty(reason) ? "" : $"({reason})")}");
            playbackState = s;
        }

        // ----------------------------------
        // 公開：ファイルを開く
        // ----------------------------------
        public void OpenFile(string path)
        {
            decoder = new Decoder();
            var info = decoder.OpenFile(path);
            videoInfo = info;

            rawFps = decoder.VideoStream.avg_frame_rate;

            // FPS 補正（29.97 の検出/矯正など）
            var fpsRaw = rawFps.num / (double)rawFps.den;
            if (Math.Abs(fpsRaw - 30.0) < 0.05 || Math.Abs(fpsRaw - 30.3) < 0.1)
            {
                videoFps = new AVRational { num = 30000, den = 1001 };
                fps = 30000f / 1001f;
                Warn($"fps={fpsRaw:F3} → 29.97fps に矯正");
            }
            else
            {
                videoFps = rawFps;
                fps = (float)videoFps.num / videoFps.den;
            }

            videoInfo.VideoStreams[0].Fps = fps;
            baseFrameDurationMs = 1000.0f / fps;

            decoder.InitializeDecoders();
            SetState(PlaybackState.Stopped, "OpenFile");
            Log($"OpenFile: {Path.GetFileName(path)}, fps={fps:F3}, baseFrameMs={baseFrameDurationMs:F3}");
        }

        // ----------------------------------
        // 公開：最初のフレームを取得→描画ターゲット作成
        // ----------------------------------
        public async Task<ImageSource> CreateBitmapAsync(int dpiX, int dpiY, RenderTargetType _targetType)
        {
            targetType = _targetType;
            if (decoder == null) throw new InvalidOperationException("OpenFile 後に呼び出してください。");

            ManagedFrame? managedFrame = null;
            FrameReadResult result = FrameReadResult.FrameNotReady;

            // 最初のフレームを非同期で取りに行く（最大 300ms）
            for (int i = 0; i < 30; i++)
            {
                await decoderLock.WaitAsync();
                try { (result, managedFrame) = decoder.TryReadFrame(); }
                finally { decoderLock.Release(); }

                unsafe
                {
                    if (result == FrameReadResult.FrameAvailable && managedFrame.Frame != null) break;
                }

                managedFrame?.Dispose();
                managedFrame = null;
                await Task.Delay(10);
            }

            unsafe
            {
                if (result != FrameReadResult.FrameAvailable || managedFrame == null || managedFrame.Frame == null)
                    throw new InvalidOperationException("最初のフレーム取得に失敗しました。");
            }

            // 必要なら GPU→CPU 転送
            if (managedFrame.IsGpuFrame)
                unsafe { managedFrame.GetCpuFrame(); }

            unsafe
            {
                if (managedFrame.Frame == null) throw new InvalidOperationException("CPU 転送後のフレームが null");

                int w = managedFrame.Frame->width;
                int h = managedFrame.Frame->height;
                var srcFmt = (AVPixelFormat)managedFrame.Frame->format;

                frameConveter = new FrameConveter();

                if (IsCpuTarget)
                {
                    // CPU(BGR24) で描画
                    frameConveter.Configure(w, h, srcFmt, w, h, CpuFfPixFmt);
                    var wb = new WriteableBitmap(w, h, dpiX, dpiY, WpfPixFmt, null);
                    imageWriter = new UnifiedImageWriter(RenderTargetType.WriteableBitmap, w, h, frameConveter, wb);
                    Log($"CreateBitmap: WriteableBitmap {w}x{h}");
                    // 初回フレームを投げて UI 側へ
                    managedFrame.Index = GetFrameIndex(managedFrame) ?? -1;
                    imageWriter.EnqueueFrame(managedFrame);
                    cpuFrames.Enqueue(managedFrame);
                    return wb;
                }
                else
                {
                    // D3DImage(BGRA) で描画
                    frameConveter.Configure(w, h, srcFmt, w, h, D3dFfPixFmt);
                    var di = new D3DImage();
                    imageWriter = new UnifiedImageWriter(RenderTargetType.D3DImage, w, h, frameConveter, di: di);
                    Log($"CreateBitmap: D3DImage {w}x{h}");
                    // 初回フレームは GPU キューで扱う
                    managedFrame.Index = GetFrameIndex(managedFrame) ?? -1;
                    gpuFrames.Enqueue(managedFrame);
                    return di;
                }
            }
        }

        // ----------------------------------
        // 公開：再生/一時停止/停止
        // ----------------------------------
        public async Task Play()
        {
            if (playbackState == PlaybackState.Stopped)
            {
                allowAutoResume = true;
                SetState(PlaybackState.Playing, "Play (fresh)");

                audioPlayer = new AudioPlayer();
                var waveFormat = new WaveFormat(decoder.AudioCodecContext.sample_rate, 16, decoder.AudioCodecContext.ch_layout.nb_channels);
                audioPlayer.Init(waveFormat, volume: 0.5f, latencyMs: 200);

                playTask = Task.Run(PlayInternal);
            }
            else
            {
                allowAutoResume = true;
                SetState(PlaybackState.Playing, "Play (resume)");
            }
        }

        public void Pause()
        {
            if (IsPlaying || IsBuffering)
            {
                allowAutoResume = false;
                SetState(PlaybackState.Paused, "Pause");
                audioPlayer?.Pause();
            }
        }

        public void Stop()
        {
            Pause();
            SetState(PlaybackState.Stopped, "Stop");

            FlushQueues();

            audioPlayer?.Dispose();
            decoder?.Dispose();
            frameConveter?.Dispose();

            Log("再生停止。リソース解放済み。");
        }

        // ----------------------------------
        // Seek
        // ----------------------------------
        public async Task<bool> SeekToExactFrameAsync(long targetFrameIndex)
        {
            Pause();
            if (!await seekLock.WaitAsync(0))
            {
                Warn("[Seek] 二重実行は無視されました。");
                return false;
            }

            try
            {
                // 1) 目標 PTS を算出（補正後 FPS 基準）
                long targetPts = ffmpeg.av_rescale_q(
                    Math.Max(0, targetFrameIndex),
                    new AVRational { num = videoFps.den, den = videoFps.num },
                    decoder.VideoStream.time_base
                );

                seekPrefetchEndFrameIndex = targetFrameIndex;

                // 2) デコーダを安全にリセット/シーク
                await ResetDecoderAndFlushQueuesAsync(targetPts);

                // 3) 目標以降のフレームを 1 枚拾う
                ManagedFrame? targetFrame = null;
                for (int i = 0; i < 5000; i++)
                {
                    var frame = await TryReadNextFrameAsync(targetFrameIndex);
                    if (frame == null) { await Task.Delay(1); continue; }
                    if (frame.Index < targetFrameIndex) { frame.Dispose(); continue; }
                    targetFrame = frame;
                    frameIndex = (int)frame.Index;
                    break;
                }

                // 4) UI に提示。CPU/D3D で入れる先を分ける
                if (targetFrame != null)
                {
                    if (IsCpuTarget)
                    {
                        imageWriter.EnqueueFrame(targetFrame);
                        cpuFrames.Enqueue(targetFrame);
                    }
                    else
                    {
                        gpuFrames.Enqueue(targetFrame);
                    }
                }
                else
                {
                    Warn($"[Seek] 目標フレーム {targetFrameIndex} を取得できませんでした。");
                    frameIndex = (int)targetFrameIndex;
                }

                // 5) 音声も合わせる
                await SeekAudioAsync(frameIndex);

                // 6) バッファ待機後、停止状態に（自動再開はしない）
                await WaitForBuffer();
                allowAutoResume = false;
                SetState(PlaybackState.Paused, "Seek complete");
                audioPlayer?.Pause();

                Log($"[Seek 完了] frameIndex={frameIndex} CPU={cpuFrames.Count} GPU={gpuFrames.Count}");
                return true;
            }
            finally
            {
                seekLock.Release();
            }
        }

        // ----------------------------------
        // 内部：音声シーク
        // ----------------------------------
        private async Task SeekAudioAsync(int videoFrameIndex)
        {
            var aStream = decoder.AudioStream;
            var atb = aStream.time_base;

            long targetAudioPts = ffmpeg.av_rescale_q(
                videoFrameIndex,
                new AVRational { num = videoFps.den, den = videoFps.num },
                atb
            );

            await decoderLock.WaitAsync();
            try
            {
                unsafe
                {
                    int ret = ffmpeg.av_seek_frame(decoder.FormatContextPointer, aStream.index, targetAudioPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (ret < 0) { Warn($"[音声シーク失敗] PTS={targetAudioPts}"); return; }

                    ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);
                    audioPlayer.ResetBuffer();

                    const int MAX_PREFETCH = 1000;
                    for (int i = 0; i < MAX_PREFETCH; i++)
                    {
                        var (readResult, audioFrame) = decoder.TryReadAudioFrame();
                        if (readResult != FrameReadResult.FrameAvailable || audioFrame.Frame == null) break;

                        long pts = audioFrame.Frame->pts;
                        if (pts == ffmpeg.AV_NOPTS_VALUE) pts = audioFrame.Frame->best_effort_timestamp;

                        if (pts < targetAudioPts) { audioFrame.Dispose(); continue; }
                        if (pts > targetAudioPts + 1) { audioFrame.Dispose(); break; }

                        using var pcm = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                        audioPlayer.AddAudioData(pcm.AsMemory().Span);
                        audioFrame.Dispose();
                    }

                    // 再生位置もだいたい合わせる
                    double sec = ffmpeg.av_q2d(atb) * targetAudioPts;
                    long bytes = (long)(sec * audioPlayer.AverageBytesPerSecond);
                    audioPlayer.SetAbsolutePosition(bytes);
                }
            }
            finally
            {
                decoderLock.Release();
            }
        }

        // ----------------------------------
        // メイン再生ループ
        // ----------------------------------
        private async Task PlayInternal()
        {
            try
            {
                // Producer / 転送 / 音声読み出しを並行起動
                _ = Task.Run(FrameProducerLoop);
                _ = Task.Run(GpuToCpuTransferLoop);
                _ = Task.Run(ReadAudioFrames);

                await WaitForBuffer();

                SetState(PlaybackState.Playing, "Buffer ready");
                audioPlayer.Start();

                var sw = Stopwatch.StartNew();
                long lastTicks = sw.ElapsedTicks;
                float tickToMs = 1000.0f / Stopwatch.Frequency;

                bool resumed = false;
                var resumeGrace = new Stopwatch();
                var lastState = playbackState;

                while (playbackState is not (PlaybackState.Stopped or PlaybackState.Ended))
                {
                    // --- 状態監視 (ヒステリシス + グレース期間) ---
                    if (playbackState == PlaybackState.Playing)
                    {
                        int low = ActiveTarget() == RenderTargetType.WriteableBitmap
                                  ? CPU_FRAME_TARGET / LOW_WATER_DIV
                                  : GPU_FRAME_TARGET / LOW_WATER_DIV;

                        if (ActiveQueueCount < low && (!resumed || resumeGrace.ElapsedMilliseconds > RESUME_GRACE_MS))
                        {
                            audioPlayer?.Pause();
                            SetState(PlaybackState.Buffering, "Low buffer");
                        }
                    }
                    else if (playbackState == PlaybackState.Buffering)
                    {
                        int high = ActiveTarget() == RenderTargetType.WriteableBitmap ? CPU_FRAME_TARGET : GPU_FRAME_TARGET;
                        if (ActiveQueueCount >= high)
                        {
                            SetState(allowAutoResume ? PlaybackState.Playing : PlaybackState.Paused, "Recovered");
                        }
                    }

                    // 状態遷移ログ
                    if (playbackState != lastState)
                    {
                        if (playbackState == PlaybackState.Playing)
                        {
                            resumed = false;
                            resumeGrace.Restart();
                        }
                        lastState = playbackState;
                    }

                    // 停止系は少し待つ
                    if (playbackState is PlaybackState.Paused or PlaybackState.Buffering or PlaybackState.SeekBuffering or PlaybackState.Seeking)
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    // --- 再開直後の同期調整 ---
                    if (!resumed && allowAutoResume)
                    {
                        int prefillTarget = (ActiveTarget() == RenderTargetType.WriteableBitmap ? CPU_FRAME_TARGET : GPU_FRAME_TARGET) / PREFILL_DIV;
                        int retry = 0;
                        while (ActiveQueueCount < prefillTarget && retry++ < 500) await Task.Delay(5);

                        double audioSec = (double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond;
                        var (idealIdx, timeInFrame) = GetCurrentFrameInfo(TimeSpan.FromSeconds(audioSec));

                        const int SAFETY_MARGIN = 2;
                        long cutoff = Math.Max(0, idealIdx - SAFETY_MARGIN);
                        DropOlderFrames(cutoff);

                        await Task.Delay((int)(baseFrameDurationMs * 3)); // 追いつく猶予
                        int offsetMs = (int)timeInFrame.TotalMilliseconds;
                        if (offsetMs > 0) await Task.Delay(offsetMs);

                        audioPlayer.Resume();
                        resumed = true;
                        resumeGrace.Restart();
                    }

                    // --- フレーム取得→提示 ---
                    var mf = DequeueForTarget();
                    int delay = 1;
                    unsafe
                    {
                        if (mf != null && mf.Frame != null)
                        {
                            try
                            {
                                if (IsCpuTarget && mf.IsGpuFrame)
                                {
                                    unsafe { mf.GetCpuFrame(); }
                                    unsafe { if (mf.Frame == null) { mf.Dispose(); continue; } }
                                }

                                // UIへ提示（CPU/D3D を吸収）
                                EnqueueForUi(mf);

                                frameIndex = (int)(mf.Index < 0 ? frameIndex + 1 : mf.Index);
                            }
                            catch (Exception ex)
                            {
                                Err($"[Present] {ex.Message}");
                                mf.Dispose();
                            }


                            // --- 音声との微調整 ---
                            double aSec = (double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond;
                            var (idealIdx2, inFrame2) = GetCurrentFrameInfo(TimeSpan.FromSeconds(aSec));
                            int diff = idealIdx2 - frameIndex;
                            float offset = (float)inFrame2.TotalMilliseconds + diff * baseFrameDurationMs;

                            long nowTicks = sw.ElapsedTicks;
                            float usedMs = (nowTicks - lastTicks) * tickToMs;
                            lastTicks = nowTicks;
                            delay = (int)MathF.Max(0, baseFrameDurationMs - usedMs - offset);
                            Console.WriteLine($"[Draw] idx={frameIndex} 描画にかかった時間={usedMs}ms");
                        }
                    }

                    await Task.Delay(delay);

                }
            }
            catch (Exception e)
            {
                Err($"PlayInternal crashed: {e}");
                SetState(PlaybackState.Stopped, "Exception");
            }
        }

        // ----------------------------------
        // 共通ヘルパ（CPU/D3D の違いを吸収）
        // ----------------------------------
        private bool IsCpuTarget => targetType == RenderTargetType.WriteableBitmap;
        private RenderTargetType ActiveTarget() => targetType;

        private int ActiveQueueCount => IsCpuTarget ? cpuFrames.Count : gpuFrames.Count;

        private ManagedFrame? DequeueForTarget()
        {
            if (IsCpuTarget)
            {
                return cpuFrames.TryDequeue(out var f) ? f : null;
            }
            else
            {
                return gpuFrames.TryDequeue(out var f) ? f : null;
            }
        }

        private void EnqueueForUi(ManagedFrame frame)
        {
            if (IsCpuTarget)
            {
                imageWriter.EnqueueFrame(frame);
            }
            else
            {
                // D3DImage では BGRA を渡して表示
                byte[] bgra = decoder.GetBgraFrame(frame, frameConveter, bt709: true);
                imageWriter.PresentBgra(bgra);
                // ManagedFrame は UnifiedImageWriter 側に渡さないので、ここで解放
                frame.Dispose();
            }
        }

        private void DropOlderFrames(long cutoffIndex)
        {
            if (IsCpuTarget)
            {
                while (cpuFrames.TryPeek(out var p))
                {
                    if (p.Index >= cutoffIndex) break;
                    if (cpuFrames.TryDequeue(out var drop)) drop.Dispose();
                }
            }
            else
            {
                while (gpuFrames.TryPeek(out var p))
                {
                    if (p.Index >= cutoffIndex) break;
                    if (gpuFrames.TryDequeue(out var drop)) drop.Dispose();
                }
            }
        }

        // ----------------------------------
        // バッファ待機（共通化）
        // ----------------------------------
        private async Task WaitForBuffer()
        {
            int halfTarget = IsCpuTarget ? CPU_FRAME_TARGET / 2 : GPU_FRAME_TARGET / 2;
            while ((ActiveQueueCount < halfTarget || isSeeking) &&
                   (playbackState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.SeekBuffering))
            {
                await Task.Delay(WAIT_TIME_MS);
            }
        }

        // ----------------------------------
        // Producer：デコードして GPU キューへ
        // ----------------------------------
        private async Task FrameProducerLoop()
        {
            try
            {
                while (playbackState != PlaybackState.Stopped)
                {
                    if (producerPaused)
                    {
                        producerQuiescedTcs?.TrySetResult(true);
                        await Task.Delay(30);
                        continue;
                    }

                    if (gpuFrames.Count >= GPU_FRAME_TARGET) { await Task.Delay(1); continue; }

                    var frame = await TryReadNextFrameAsync(seekPrefetchEndFrameIndex >= 0 ? seekPrefetchEndFrameIndex : null);
                    if (frame == null) { await Task.Delay(1); continue; }

                    if (seekPrefetchEndFrameIndex >= 0 && frame.Index < seekPrefetchEndFrameIndex)
                    {
                        frame.Dispose();
                        continue;
                    }

                    // 重複防止
                    if (Interlocked.Read(ref lastEnqueuedFrameIndex) == frame.Index)
                    {
                        frame.Dispose();
                        continue;
                    }

                    gpuFrames.Enqueue(frame);
                    Interlocked.Exchange(ref lastEnqueuedFrameIndex, frame.Index);

#if DEBUG
                    //Log($"[Producer] Enqueue GPU idx={frame.Index} (GPU={gpuFrames.Count})");
#endif
                }
            }
            catch (Exception e)
            {
                Err($"FrameProducerLoop crashed: {e}");
            }
            finally
            {
                producerQuiescedTcs?.TrySetResult(true);
            }
        }

        // ----------------------------------
        // GPU→CPU 転送（CPU ターゲット時のみ）
        // ----------------------------------
        private async Task GpuToCpuTransferLoop()
        {
            try
            {
                while (playbackState != PlaybackState.Stopped)
                {
                    if (isSeeking || !IsCpuTarget) { await Task.Delay(30); continue; }

                    while (cpuFrames.Count < CPU_FRAME_TARGET && gpuFrames.TryDequeue(out var g))
                    {
                        try
                        {
                            if (g.IsGpuFrame) unsafe { g.GetCpuFrame(); }
                            cpuFrames.Enqueue(g);
#if DEBUG
                            //Log($"[Transfer] GPU→CPU idx={g.Index} (CPU={cpuFrames.Count})");
#endif
                        }
                        catch (Exception ex)
                        {
                            Err($"[GPU→CPU] {ex.Message}");
                            g.Dispose();
                        }
                    }

                    await Task.Delay(1);
                }
            }
            catch (Exception e)
            {
                Err($"GpuToCpuTransferLoop crashed: {e}");
            }
        }

        // ----------------------------------
        // 音声フレーム読み出し
        // ----------------------------------
        private async Task ReadAudioFrames()
        {
            try
            {
                const int maxRetry = 100;
                const int retryDelayMs = 10;
                int retryCount = 0;

                while (playbackState != PlaybackState.Stopped)
                {
                    if (isSeeking) { await Task.Delay(50); continue; }

                    // Paused中でもバッファが不足なら読み続ける
                    bool canRead = (playbackState != PlaybackState.Paused) || audioPlayer.BufferedDuration.TotalMilliseconds < 500;
                    if (!canRead) { await Task.Delay(50); continue; }

                    if (audioPlayer.BufferedDuration.TotalSeconds >= 10) { await Task.Delay(50); continue; }

                    ManagedFrame audioFrame = null;
                    FrameReadResult rr;

                    await decoderLock.WaitAsync();
                    try { (rr, audioFrame) = decoder.TryReadAudioFrame(); }
                    finally { decoderLock.Release(); }

                    if (rr == FrameReadResult.FrameAvailable && audioFrame != null)
                    {
                        unsafe
                        {
                            long pts = audioFrame.Frame->pts;
                            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = audioFrame.Frame->best_effort_timestamp;

                            if (seekPrefetchEndFrameIndex >= 0 && pts <= seekPrefetchEndFrameIndex) { audioFrame.Dispose(); continue; }
                            if (audioFrame.Frame == null || audioFrame.Frame->nb_samples <= 0) { audioFrame.Dispose(); continue; }
                        }

                        var pcm = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                        audioPlayer.AddAudioData(pcm.AsMemory().Span);
                        audioFrame.Dispose();
                        retryCount = 0;
                    }
                    else if (rr == FrameReadResult.FrameNotReady)
                    {
                        await Task.Delay(retryDelayMs);
                        if (++retryCount > maxRetry) return;
                    }
                    else if (rr == FrameReadResult.EndOfStream)
                    {
                        endedStreamAudio = true;
                    }
                }
            }
            catch (Exception e)
            {
                Err($"ReadAudioFrames crashed: {e}");
            }
        }

        // ----------------------------------
        // デコーダの安全なリセット/シーク
        // ----------------------------------
        private async Task ResetDecoderCoreAsync(long? seekPts = null)
        {
            await decoderLock.WaitAsync();
            try
            {
                unsafe
                {
                    if (seekPts.HasValue)
                    {
                        int ret = ffmpeg.av_seek_frame(decoder.FormatContextPointer, decoder.VideoStream.index, seekPts.Value, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        if (ret < 0) throw new InvalidOperationException($"av_seek_frame 失敗 PTS={seekPts}");
                    }

                    ffmpeg.avcodec_flush_buffers(decoder.VideoCodecContextPointer);
                    if (decoder.AudioCodecContextPointer != null) ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);

                    decoder.ClearInternalQueues();
                }
            }
            finally
            {
                decoderLock.Release();
            }
        }

        private async Task PauseProducerAsync()
        {
            if (producerPaused && producerQuiescedTcs != null) { await producerQuiescedTcs.Task; return; }

            producerPaused = true;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref producerQuiescedTcs, tcs);
            await tcs.Task;
        }

        private void ResumeProducer()
        {
            producerPaused = false;
            Interlocked.Exchange(ref producerQuiescedTcs, null);
        }

        private async Task WithProducerPausedAsync(Func<Task> action)
        {
            isSeeking = true;
            try
            {
                await PauseProducerAsync();
                await action();
            }
            finally
            {
                ResumeProducer();
                isSeeking = false;
            }
        }

        private async Task ResetDecoderAndFlushQueuesAsync(long? seekPts = null)
        {
            await WithProducerPausedAsync(async () =>
            {
                FlushQueues();
                Interlocked.Exchange(ref lastEnqueuedFrameIndex, -1);
                endedStreamVideo = false;

                ResetIndexObservation(); // フレーム番号推定をリセット
                await ResetDecoderCoreAsync(seekPts);

                FlushQueues();
                LogBuffers("[After Reset]");
            });
        }

        public void FlushQueues()
        {
            while (gpuFrames.TryDequeue(out var g)) g.Dispose();
            while (cpuFrames.TryDequeue(out var c)) c.Dispose();
        }

        // ----------------------------------
        // 便利関数：現在の再生時間→フレーム番号/フレーム内経過
        // ----------------------------------
        public (int frameNumber, TimeSpan timeInFrame) GetCurrentFrameInfo(TimeSpan playbackTime)
        {
            double totalMs = playbackTime.TotalMilliseconds;
            int fn = (int)(totalMs / baseFrameDurationMs);
            double frameStart = fn * baseFrameDurationMs;
            return (fn, TimeSpan.FromMilliseconds(totalMs - frameStart));
        }

        // ----------------------------------
        // フレームの PTS → インデックス推定
        // ----------------------------------
        public unsafe long? GetFrameIndex(ManagedFrame frame)
        {
            if (frame.Frame == null) return null;

            long pts = frame.Frame->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
            {
                pts = frame.Frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE) return null;
            }

            UpdateFrameIndexFactor(pts);

            long idxRaw = ffmpeg.av_rescale_q(
                pts,
                decoder.VideoStream.time_base,
                new AVRational { num = videoFps.den, den = videoFps.num }
            );
            long idx = idxRaw / Math.Max(1, frameIndexFactor);
            frame.Index = idx;
            return idx;
        }

        private unsafe long? GetFrameIndexUsingBestEffort(ManagedFrame frame)
        {
            if (frame.Frame == null) return null;

            long pts = frame.Frame->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
            {
                pts = frame.Frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE) return null;
            }

            UpdateFrameIndexFactor(pts);

            long idxRaw = ffmpeg.av_rescale_q(
                pts,
                decoder.VideoStream.time_base,
                new AVRational { num = videoFps.den, den = videoFps.num }
            );
            return idxRaw / Math.Max(1, frameIndexFactor);
        }

        private static int Gcd(int a, int b)
        {
            if (a <= 0) return b;
            if (b <= 0) return a;
            while (b != 0) { int t = a % b; a = b; b = t; }
            return Math.Abs(a);
        }

        private void UpdateFrameIndexFactor(long pts)
        {
            if (lastObservedPts is long prev)
            {
                long dPts = pts - prev;
                if (dPts > 0)
                {
                    long inc = ffmpeg.av_rescale_q(
                        dPts,
                        decoder.VideoStream.time_base,
                        new AVRational { num = videoFps.den, den = videoFps.num }
                    );
                    if (inc > 0)
                    {
                        frameIndexFactor = frameIndexFactor <= 1 ? (int)inc : Gcd(frameIndexFactor, (int)inc);
                        if (frameIndexFactor <= 0) frameIndexFactor = 1;
                    }
                }
            }
            lastObservedPts = pts;
        }

        private void ResetIndexObservation()
        {
            lastObservedPts = null;
            // 必要なら係数も初期化：
            // frameIndexFactor = 1;
        }

        // ----------------------------------
        // 次の 1 フレームをデコード（最低インデックス指定可）
        // ----------------------------------
        private async Task<ManagedFrame?> TryReadNextFrameAsync(long? minFrameIndex = null)
        {
            FrameReadResult result;
            ManagedFrame frame = null;

            await decoderLock.WaitAsync().ConfigureAwait(false);
            try { (result, frame) = decoder.TryReadFrame(); }
            finally { decoderLock.Release(); }

            if (result is FrameReadResult.FrameNotReady or FrameReadResult.EndOfStream || frame == null) return null;

            long idx;
            long? maybeIdx = GetFrameIndexUsingBestEffort(frame);
            idx = maybeIdx ?? (Interlocked.Read(ref lastEnqueuedFrameIndex) >= 0 ? Interlocked.Read(ref lastEnqueuedFrameIndex) + 1 : 0);
            frame.Index = idx;

            if (minFrameIndex.HasValue && idx < minFrameIndex.Value)
            {
                frame.Dispose();
                return null;
            }

            return frame;
        }

        // ----------------------------------
        // 合計フレーム数（概算）
        // ----------------------------------
        public long GetTotalFrameCount()
        {
            return (long)(fps * (videoInfo.Duration.Milliseconds / 1000.0));
        }
    }
}
