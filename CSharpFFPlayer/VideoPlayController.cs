using FFmpeg.AutoGen;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Intrinsics.X86;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

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

    public class VideoPlayController
    {

        // FFmpeg で使用するピクセル形式（BGR24）
        private static readonly AVPixelFormat ffPixelFormat = AVPixelFormat.AV_PIX_FMT_BGR24;

        // WPF におけるピクセル形式（Bgr24）
        private static readonly System.Windows.Media.PixelFormat wpfPixelFormat = PixelFormats.Bgr24;

        private PlaybackState playbackState = PlaybackState.Stopped;
        public bool IsPlaying => playbackState == PlaybackState.Playing;
        public bool IsPaused => playbackState == PlaybackState.Paused;
        public bool IsSeeking => playbackState == PlaybackState.Seeking;
        public bool IsBuffering => playbackState == PlaybackState.Buffering;
        public bool IsEnded => playbackState == PlaybackState.Ended;

        private MemoryStream audioStream = new MemoryStream();
        private readonly object audioLock = new object();

        private Decoder decoder;
        private ImageWriter imageWriter;
        private FrameConveter frameConveter;

        private const int frameCap = 50;
        private const int waitTime = 150;

        private uint decodedFrames = 0;
        private AVRational rawFps;
        private AVRational videoFps;
        private float fps = 0;
        private float baseFrameDurationMs = 0;

        private ConcurrentQueue<ManagedFrame> frames = new ConcurrentQueue<ManagedFrame>();
        private Task playTask;

        private AudioPlayer audioPlayer;
        private int frameIndex = 0;
        public int FrameIndex => frameIndex;

        private static readonly SemaphoreSlim decoderLock = new(1, 1);
        private readonly SemaphoreSlim seekLock = new SemaphoreSlim(1, 1);

        private VideoInfo videoInfo;
        public VideoInfo VideoInfo => videoInfo;
        // ---- 直近エンキューしたフレームのインデックス（重複防止用）----
        private long lastEnqueuedFrameIndex = -1;

        // 自動再開を許可するか（Play で true、Pause/Seek 後は false）
        private volatile bool allowAutoResume = false;


        /// <summary>
        /// ファイルを開いて FFmpeg デコーダーを初期化
        /// </summary>
        public void OpenFile(string path)
        {
            decoder = new Decoder();
            videoInfo = decoder.OpenFile(path);

            rawFps = decoder.VideoStream.avg_frame_rate;

            // ---- FPS 補正ロジック ----
            double fpsRaw = rawFps.num / (double)rawFps.den;
            if (Math.Abs(fpsRaw - 30.0) < 0.05 || Math.Abs(fpsRaw - 30.3) < 0.1)
            {
                // 実質 NTSC 29.97 とみなして矯正
                videoFps.num = 30000;
                videoFps.den = 1001;
                fps = 30000f / 1001f; // 29.97002997...
                Console.WriteLine($"[警告] fps={fpsRaw:F3} → 29.97fps に矯正しました");
            }
            else
            {
                videoFps = rawFps;
                fps = (float)videoFps.num / (float)videoFps.den;
            }

            videoInfo.VideoStreams.FirstOrDefault().Fps = fps;
            baseFrameDurationMs = 1000.0f / fps;
            decoder.InitializeDecoders();
            playbackState = PlaybackState.Stopped;
        }


        /// <summary>
        /// 最初のフレームを取得し、WPF 描画用の WriteableBitmap を作成する
        /// </summary>
        public async Task<WriteableBitmap> CreateBitmapAsync(int dpiX, int dpiY)
        {
            if (decoder is null)
                throw new InvalidOperationException("動画を開いてから描画先を作成してください。");

            ManagedFrame? managedFrame = null;
            FrameReadResult result = FrameReadResult.FrameNotReady;

            // 非同期で最初のフレームを取得
            for (int i = 0; i < 30; i++)
            {
                await decoderLock.WaitAsync();
                try
                {
                    (result, managedFrame) = decoder.TryReadFrame();
                }
                finally
                {
                    decoderLock.Release();
                }

                unsafe
                {
                    if (result == FrameReadResult.FrameAvailable && managedFrame.Frame != null)

                        break;
                }
                // フレームが無効なら破棄して次へ
                managedFrame?.Dispose();
                managedFrame = null;
                await Task.Delay(10);
            }
            unsafe
            {
                if (result != FrameReadResult.FrameAvailable || managedFrame == null || managedFrame.Frame == null)
                    throw new InvalidOperationException("最初のフレームの取得に失敗しました。");
            }
            // GPU → CPU 転送
            if (managedFrame.IsGpuFrame)
                unsafe { managedFrame.GetCpuFrame(); }

            unsafe
            {
                if (managedFrame.Frame == null)
                    throw new InvalidOperationException("CPU 転送後のフレームが null です。");

                AVFrame* frame = managedFrame.Frame;
                int width = frame->width;
                int height = frame->height;
                AVPixelFormat srcFormat = (AVPixelFormat)frame->format;

                var writeableBitmap = new WriteableBitmap(width, height, dpiX, dpiY, wpfPixelFormat, null);
                frameConveter = new FrameConveter();
                frameConveter.Configure(width, height, srcFormat, width, height, ffPixelFormat);

                imageWriter = new ImageWriter(width, height, writeableBitmap, frameConveter);

                // 安全にインデックス設定
                managedFrame.Index = GetFrameIndex(managedFrame) ?? -1;

                // 最新フレームとしてキャッシュ（初回描画はRenderingイベントで行われる）
                imageWriter.EnqueueFrame(managedFrame);


                // 再生開始時に同期が取りやすいようキューにも積む
                cpuFrames.Enqueue(managedFrame);

                return writeableBitmap;
            }
        }







        /// <summary>
        /// 映像の再生を開始する（または一時停止から再開）
        /// </summary>
        public async Task Play()
        {
            if (playbackState != PlaybackState.Stopped)
            {
                allowAutoResume = true;               // ← 追加
                playbackState = PlaybackState.Playing;
                //audioPlayer?.Resume(); // ← Resume はループ側の同期で行う（任せる）
            }
            else
            {
                allowAutoResume = true;               // ← 追加
                playbackState = PlaybackState.Playing;
                audioPlayer = new AudioPlayer();

                var waveFormat = new WaveFormat(decoder.AudioCodecContext.sample_rate, 16, decoder.AudioCodecContext.ch_layout.nb_channels);
                audioPlayer.Init(waveFormat, volume: 0.5f, latencyMs: 200);

                playTask = Task.Run(() => PlayInternal());
            }
        }

        public void Pause()
        {
            if (playbackState.Equals(PlaybackState.Playing) || playbackState.Equals(PlaybackState.Buffering))
            {
                allowAutoResume = false;              // ← 追加
                playbackState = PlaybackState.Paused;
                audioPlayer?.Pause();
            }
        }


        /// <summary>
        /// 再生を停止して状態をリセットする
        /// </summary>
        public void Stop()
        {
            Pause();
            playbackState = PlaybackState.Stopped;

            while (frames.TryDequeue(out var remainingFrame))
            {
                remainingFrame.Dispose();
            }

            Console.WriteLine("再生を終了しました");

            frames.Clear();
            audioPlayer?.Dispose();
            decoder?.Dispose();
            frameConveter?.Dispose();
        }

        // 旧: private static readonly SemaphoreSlim transferLimiter = new(4); // ← 不要なら削除
        private volatile bool isSeeking = false;

        public async Task<bool> SeekToExactFrameAsync(long targetFrameIndex)
        {
            Pause();
            if (!await seekLock.WaitAsync(0))
            {
                Console.WriteLine("[シーク] 二重実行は無視されました。");
                return false;
            }

            try
            {
                long currentFrameIndex = frameIndex;
                bool isForwardSeek = targetFrameIndex > currentFrameIndex;

                // ★ 補正後 FPS を使用
                long targetPts = ffmpeg.av_rescale_q(
                    Math.Max(0, targetFrameIndex),
                    new AVRational { num = videoFps.den, den = videoFps.num },
                    decoder.VideoStream.time_base
                );

                // 1) デコーダとキューをリセットして安全にシーク
                seekPrefetchEndFrameIndex = targetFrameIndex; // ★古いフレームは破棄
                await ResetDecoderAndFlushQueuesAsync(targetPts);

                // 2) 目標フレームを取得（古いフレームはスキップ）
                ManagedFrame? targetFrame = null;
                for (int i = 0; i < 5000; i++)
                {
                    var frame = await TryReadNextFrameAsync(targetFrameIndex);
                    if (frame == null)
                    {
                        await Task.Delay(1);
                        continue;
                    }

                    if (frame.Index < targetFrameIndex)
                    {
                        frame.Dispose();
                        continue; // 古いフレームはスキップ
                    }

                    targetFrame = frame;
                    frameIndex = (int)frame.Index;
                    break;
                }

                // 3) UI と再生キューへ追加
                if (targetFrame != null)
                {
                    imageWriter.EnqueueFrame(targetFrame); // UI優先で描画
                    cpuFrames.Enqueue(targetFrame);        // 再生ループでも利用
                }
                else
                {
                    Console.WriteLine($"[警告] 目標フレーム {targetFrameIndex} を取得できませんでした");
                    frameIndex = (int)targetFrameIndex;
                }

                // 4) 音声同期
                bool hasAudioStream;
                unsafe { hasAudioStream = decoder.AudioCodecContextPointer != null && decoder.AudioStreamPointer != null; }
                if (hasAudioStream)
                    await SeekAudioAsync(frameIndex);

                // 5) バッファ待機
                await WaitForBuffer();

                // 6) 自動再開を禁止して停止状態に固定
                allowAutoResume = false;
                playbackState = PlaybackState.Paused;
                audioPlayer?.Pause();

                Console.WriteLine($"[シーク完了] frameIndex={frameIndex}, cpuFrames={cpuFrames.Count}, 状態={playbackState}");
                return true;
            }
            finally
            {
                seekLock.Release();
            }
        }




        private async Task SeekAudioAsync(int videoFrameIndex)
        {
            var audioStream = decoder.AudioStream;
            var audioTimeBase = audioStream.time_base;

            long targetAudioPts = ffmpeg.av_rescale_q(
                videoFrameIndex,
                new AVRational { num = videoFps.den, den = videoFps.num }, // frame -> sec
                audioTimeBase
            );

            await decoderLock.WaitAsync();
            try
            {
                unsafe
                {
                    int result = ffmpeg.av_seek_frame(
                        decoder.FormatContextPointer,
                        audioStream.index,
                        targetAudioPts,
                        ffmpeg.AVSEEK_FLAG_BACKWARD
                    );
                    if (result < 0)
                    {
                        Console.WriteLine($"[警告] 音声シーク失敗 (PTS: {targetAudioPts})");
                        return;
                    }

                    ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);
                    audioPlayer.ResetBuffer();

                    const int maxAudioFrames = 1000;
                    for (int i = 0; i < maxAudioFrames; i++)
                    {
                        var (readResult, audioFrame) = decoder.TryReadAudioFrame();
                        if (readResult != FrameReadResult.FrameAvailable || audioFrame.Frame == null)
                            break;

                        long framePts = audioFrame.Frame->pts;
                        if (framePts == ffmpeg.AV_NOPTS_VALUE)
                            framePts = audioFrame.Frame->best_effort_timestamp;

                        if (framePts < targetAudioPts)
                        {
                            audioFrame.Dispose();
                            continue;
                        }
                        if (framePts > targetAudioPts + 1)
                        {
                            audioFrame.Dispose();
                            break;
                        }

                        using var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                        audioPlayer.AddAudioData(audioData.AsMemory().Span);
                        audioFrame.Dispose();
                    }

                    double targetSeconds = ffmpeg.av_q2d(audioStream.time_base) * targetAudioPts;
                    long targetPositionBytes = (long)(targetSeconds * audioPlayer.AverageBytesPerSecond);
                    audioPlayer.SetAbsolutePosition(targetPositionBytes);
                }
            }
            finally
            {
                decoderLock.Release();
            }
        }


        public long GetTotalFrameCount()
        {
            return (long)(fps * (videoInfo.Duration.Milliseconds / 1000.0));
        }




        private async Task PlayInternal()
        {
            // プロデューサ開始
            _ = Task.Run(() => FrameProducerLoop());
            _ = Task.Run(() => GpuToCpuTransferLoop());
            _ = Task.Run(() => ReadAudioFrames());

            await WaitForBuffer();

            playbackState = PlaybackState.Playing;
            bool resumed = false;

            audioPlayer.Start();

            var stopwatch = Stopwatch.StartNew();
            long lastTicks = stopwatch.ElapsedTicks;
            float tickToMs = 1000.0f / Stopwatch.Frequency;

            // しきい値（ヒステリシス）
            const int HIGH_WATERMARK = CPU_FRAME_TARGET;           // 回復とみなす
            const int LOW_WATERMARK = CPU_FRAME_TARGET / 3;       // 不足とみなす

            // 再開直後の猶予（この間は Buffering へ落とさない）
            const int RESUME_GRACE_MS = 250;
            var resumeGrace = new Stopwatch();
            var lastState = playbackState;

            while (playbackState != PlaybackState.Stopped && playbackState != PlaybackState.Ended)
            {
                // ---- バッファ監視（ヒステリシス + グレース期間）----
                if (playbackState == PlaybackState.Playing)
                {
                    if (cpuFrames.Count < LOW_WATERMARK && (!resumed || resumeGrace.ElapsedMilliseconds > RESUME_GRACE_MS))
                    {
                        audioPlayer?.Pause();
                        playbackState = PlaybackState.Buffering;
                    }
                }
                else if (playbackState == PlaybackState.Buffering)
                {
                    if (cpuFrames.Count >= HIGH_WATERMARK)
                    {
                        if (allowAutoResume)
                            playbackState = PlaybackState.Playing;
                        else
                            playbackState = PlaybackState.Paused;
                    }
                }

                // 状態遷移検出
                if (playbackState != lastState)
                {
                    if (playbackState == PlaybackState.Playing)
                    {
                        resumed = false;          // 再開直後の同期フローを走らせる
                        resumeGrace.Restart();    // グレース期間開始
                    }
                    lastState = playbackState;
                }

                // 停止系状態はゆっくり待機
                if (playbackState == PlaybackState.Paused ||
                    playbackState == PlaybackState.Buffering ||
                    playbackState == PlaybackState.SeekBuffering ||
                    playbackState == PlaybackState.Seeking)
                {
                    await Task.Delay(100);
                    continue;
                }

                // ---- ここから Playing のみ ----

                // 再開直後の同期（プリフィル + セーフティマージン + 少し待ってから再開）
                if (!resumed && allowAutoResume)
                {
                    // 1) プリフィル：できるだけ満タンに近づける
                    // 再生開始前のプリフェッチ
                    const int PREFILL_TARGET = CPU_FRAME_TARGET / 2;
                    int retry = 0;
                    while (cpuFrames.Count < PREFILL_TARGET && retry < 500) // 最大 500ms 待機
                    {
                        await Task.Delay(5);
                        retry++;
                    }


                    // 2) 音声位置から理想フレームを算出
                    double audioSec = (double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond;
                    var (idealFrameIndex, timeInFrame) = GetCurrentFrameInfo(TimeSpan.FromSeconds(audioSec));

                    // 3) セーフティマージン：理想位置の2フレ前から再開できるよう破棄を控えめに
                    const int SAFETY_MARGIN = 2;
                    long cutoffIndex = Math.Max(0, idealFrameIndex - SAFETY_MARGIN);
                    while (cpuFrames.TryPeek(out var peeked))
                    {
                        long peekedIdx = peeked.Index;
                        if (peekedIdx >= cutoffIndex) break;
                        if (cpuFrames.TryDequeue(out var skipped)) skipped.Dispose();
                    }


                    // 4) プロデューサがさらに追いつく小休止
                    await Task.Delay((int)(baseFrameDurationMs * 3));

                    // 5) フレーム内オフセット待ち
                    int remainingDelayMs = (int)timeInFrame.TotalMilliseconds;
                    if (remainingDelayMs > 0) await Task.Delay(remainingDelayMs);

                    // 6) 音声再開
                    audioPlayer?.Resume();
                    resumed = true;
                    resumeGrace.Restart(); // ここから猶予カウント
                }

                // ---- フレーム取得 → 最新フレームとして提示（描画は Rendering で）----
                // 再生ループ内
                var frame = DequeueCpuFrame();
                if (frame != null)
                {
                    try
                    {
                        if (frame.IsGpuFrame)
                        {
                            unsafe { frame.GetCpuFrame(); } // ★ここでCPU転送を強制
                        }

                        unsafe
                        {
                            if (frame.Frame == null) // まだ転送に失敗した場合
                            {
                                frame.Dispose();
                                continue;
                            }
                        }

                        long frameIdx = frame.Index < 0 ? frameIndex + 1 : frame.Index;
                        long prevFrameIdx = -1;

                        if (frameIdx >= 0)
                        {
                            if (prevFrameIdx >= 0 && frameIdx <= prevFrameIdx)
                            {
                                frame.Dispose();
                                continue; // ★古いフレームはスキップ
                            }
                            prevFrameIdx = frameIdx;
                        }

                        imageWriter.EnqueueFrame(frame);
                        frameIndex = (int)frameIdx;

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[描画前CPU転送失敗] {ex.Message}");
                        frame.Dispose();
                    }
                }



                // ---- 音声との細かな同期補正 ----
                double audioSec2 = (double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond;
                var (idealFrameIndex2, timeInFrame2) = GetCurrentFrameInfo(TimeSpan.FromSeconds(audioSec2));
                int frameDiff = idealFrameIndex2 - frameIndex;
                float offsetMs = (float)timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs;

                long currentTicks = stopwatch.ElapsedTicks;
                long elapsedTicks = currentTicks - lastTicks;
                lastTicks = currentTicks;
                float usedMs = elapsedTicks * tickToMs;

                int delay = (int)MathF.Truncate(baseFrameDurationMs - usedMs - offsetMs);
                await Task.Delay(Math.Max(0, delay));
            }
        }









        /// <summary>
        /// 指定された再生時間におけるフレーム番号とフレーム内の経過時間を取得
        /// </summary>
        public (int frameNumber, TimeSpan timeInFrame) GetCurrentFrameInfo(TimeSpan playbackTime)
        {
            double totalMilliseconds = playbackTime.TotalMilliseconds;
            int frameNumber = (int)(totalMilliseconds / baseFrameDurationMs);
            double frameStartTime = frameNumber * baseFrameDurationMs;
            double timeInFrame = totalMilliseconds - frameStartTime;
            return (frameNumber, TimeSpan.FromMilliseconds(timeInFrame));
        }

        /// <summary>
        /// フレームのPTSからフレームインデックスを計算（null なら未定義）
        /// </summary>
        public unsafe long? GetFrameIndex(ManagedFrame frame)
        {
            if (frame.Frame == null) return null;

            long pts = frame.Frame->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
            {
                pts = frame.Frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE) return null;
            }

            // 観測して係数を更新
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

            // ★ rawFps ではなく videoFps (補正後) を使用
            long idxRaw = ffmpeg.av_rescale_q(
                pts,
                decoder.VideoStream.time_base,
                new AVRational { num = videoFps.den, den = videoFps.num }
            );

            return idxRaw / Math.Max(1, frameIndexFactor);
        }



        // 連番が 0,2,4,… になるのを補正するための係数（既定 1 = 補正なし）
        private volatile int frameIndexFactor = 1;

        // 観測用の直近 PTS
        private long? lastObservedPts = null;

        // GCD ユーティリティ
        private static int Gcd(int a, int b)
        {
            if (a <= 0) return b;
            if (b <= 0) return a;
            while (b != 0) { int t = a % b; a = b; b = t; }
            return Math.Abs(a);
        }

        // PTS を観測して frameIndexFactor を更新
        private void UpdateFrameIndexFactor(long pts)
        {
            if (lastObservedPts is long prev)
            {
                long dPts = pts - prev;
                if (dPts > 0)
                {
                    // PTS 差を「フレーム増分」に換算（tb → fps）
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

        // シーク開始時など、観測をリセットしたいとき
        private void ResetIndexObservation()
        {
            lastObservedPts = null;
            // 係数は保持しても良いが、リセットしたい場合は以下を有効化
            // frameIndexFactor = 1;
        }




        /// <summary>
        /// PTS から生フレームインデックスを取得（null なら未定義）
        /// </summary>
        public unsafe long? GetRawFrameIndex(ManagedFrame frame)
        {
            if (frame == null || frame.Frame == null)
                return null;

            long pts = frame.Frame->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                return null;

            AVRational timeBase = decoder.VideoStream.time_base;
            AVRational frameRate = rawFps;

            long frameIndex = ffmpeg.av_rescale_q(
                pts,
                timeBase,
                new AVRational { num = frameRate.den, den = frameRate.num }
            );

            return frameIndex;
        }

        /// <summary>
        /// CPUフレームバッファが一定量溜まるまで待機
        /// </summary>
        private async Task WaitForBuffer()
        {
            // CPUフレームキューを監視
            while ((cpuFrames.Count < CPU_FRAME_TARGET / 2 || isSeeking) &&
                   (playbackState == PlaybackState.Playing ||
                    playbackState == PlaybackState.Paused ||
                    playbackState == PlaybackState.SeekBuffering))
            {
                await Task.Delay(waitTime);
            }
        }



        private long seekPrefetchEndFrameIndex = -1;
        private bool endedStreamVideo = false;
        private bool endedStreamAudio = false;

        // ---- 新しいフレーム管理キュー ----
        private readonly ConcurrentQueue<ManagedFrame> gpuFrames = new();
        private readonly ConcurrentQueue<ManagedFrame> cpuFrames = new();

        private const int GPU_FRAME_TARGET = 20;
        private const int CPU_FRAME_TARGET = 10;

        // プロデューサ制御フラグ/TCS
        private volatile bool producerPaused = false;
        private TaskCompletionSource<bool>? producerQuiescedTcs;

        // プロデューサを停止（「停止完了」を待つ）
        private async Task PauseProducerAsync()
        {
            // すでに停止要求済みなら、いまのTCSを待つ
            if (producerPaused && producerQuiescedTcs != null)
            {
                await producerQuiescedTcs.Task;
                return;
            }

            producerPaused = true;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref producerQuiescedTcs, tcs);

            // ループ側が停止に入り次第、TCSが完了される
            await tcs.Task;
        }

        // プロデューサを再開
        private void ResumeProducer()
        {
            producerPaused = false;
            Interlocked.Exchange(ref producerQuiescedTcs, null);
        }

        // 「停止を保証した上で」安全に操作を行うユーティリティ
        private async Task WithProducerPausedAsync(Func<Task> action)
        {
            // 転送ループも足並みを止めたいので isSeeking を立てる
            isSeeking = true;
            try
            {
                await PauseProducerAsync();
                await action(); // ここで Reset/Flush/Seek などのクリティカル処理を安全に実行
            }
            finally
            {
                ResumeProducer();
                isSeeking = false;
            }
        }


        /// <summary>
        /// デコーダから次のフレームを1枚読み出し、インデックスを計算する。
        /// minFrameIndex が指定されている場合、それより古いフレームは破棄するが、
        /// 後ろ方向シークでは破棄せず受け入れる。
        /// </summary>
        private async Task<ManagedFrame?> TryReadNextFrameAsync(long? minFrameIndex = null)
        {
            FrameReadResult result;
            ManagedFrame? frame = null;

            await decoderLock.WaitAsync().ConfigureAwait(false);
            try
            {
                (result, frame) = decoder.TryReadFrame();
            }
            finally
            {
                decoderLock.Release();
            }

            if (result == FrameReadResult.FrameNotReady ||
                result == FrameReadResult.EndOfStream ||
                frame == null)
                return null;

            // ---- インデックス計算 ----
            long idx;
            long? maybeIdx = GetFrameIndexUsingBestEffort(frame);
            if (maybeIdx.HasValue)
            {
                idx = maybeIdx.Value;
            }
            else
            {
                long prev = Interlocked.Read(ref lastEnqueuedFrameIndex);
                idx = prev >= 0 ? prev + 1 : 0;
            }

            frame.Index = idx;

#if DEBUG
            unsafe
            {
                //Console.WriteLine($"[TryReadNextFrame] PTS={frame.Frame->pts}, best_effort={frame.Frame->best_effort_timestamp}, calculatedIdx={idx}, min={minFrameIndex?.ToString() ?? "null"}");
            }
#endif

            // ---- シークガード ----
            if (minFrameIndex.HasValue && idx < minFrameIndex.Value)
            {
#if DEBUG
                Console.WriteLine($"[SkipFrame] idx={idx} < min={minFrameIndex.Value} → frame disposed");
#endif
                frame.Dispose();
                return null;
            }


            return frame;
        }



        // GPUフレームキューへの投入（重複チェック込み）
        private void EnqueueGpuFrame(ManagedFrame frame)
        {
            if (Interlocked.Read(ref lastEnqueuedFrameIndex) == frame.Index)
            {
                frame.Dispose();
                return;
            }

            gpuFrames.Enqueue(frame);
            Interlocked.Exchange(ref lastEnqueuedFrameIndex, frame.Index);
        }

        private readonly TaskCompletionSource<bool> producerStoppedTcs = new();

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

                    if (gpuFrames.Count >= GPU_FRAME_TARGET)
                    {
                        await Task.Delay(1);
                        continue;
                    }

                    var frame = await TryReadNextFrameAsync(seekPrefetchEndFrameIndex >= 0 ? seekPrefetchEndFrameIndex : null);
                    if (frame == null)
                    {
                        await Task.Delay(1);
                        continue;
                    }

                    // ここでインデックスが古いなら破棄して continue
                    if (seekPrefetchEndFrameIndex >= 0 && frame.Index < seekPrefetchEndFrameIndex)
                    {
                        frame.Dispose();
                        continue;
                    }

                    EnqueueGpuFrame(frame);
                    //Console.Write("GPU Enqueue : ");
                    //Console.WriteLine(frame.Index);
                }
            }
            finally
            {
                producerQuiescedTcs?.TrySetResult(true);
            }
        }




        /// <summary>
        /// ffmpeg デコーダを内部的にリセット/シーク（decoderLock 保持）
        /// ※ 呼び出し前に必ず WithProducerPausedAsync でループ停止を保証すること
        /// </summary>
        private async Task ResetDecoderCoreAsync(long? seekPts = null)
        {
            await decoderLock.WaitAsync();
            try
            {
                unsafe
                {
                    if (seekPts.HasValue)
                    {
                        int result = ffmpeg.av_seek_frame(
                            decoder.FormatContextPointer,
                            decoder.VideoStream.index,
                            seekPts.Value,
                            ffmpeg.AVSEEK_FLAG_BACKWARD
                        );
                        if (result < 0)
                            throw new InvalidOperationException($"av_seek_frame 失敗 PTS={seekPts}");
                    }

                    // デコーダをフラッシュ
                    ffmpeg.avcodec_flush_buffers(decoder.VideoCodecContextPointer);
                    if (decoder.AudioCodecContextPointer != null)
                        ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);

                    // ★ デマルチ/送信キューも完全クリア（ここが重要）
                    decoder.ClearInternalQueues();
                }
            }
            finally
            {
                decoderLock.Release();
            }
        }


        /// <summary>
        /// ループ停止を保証した上で Reset + Flush を実行する高レベルAPI
        /// </summary>
        private async Task ResetDecoderAndFlushQueuesAsync(long? seekPts = null)
        {
            await WithProducerPausedAsync(async () =>
            {
                FlushQueues();
                Interlocked.Exchange(ref lastEnqueuedFrameIndex, -1);
                endedStreamVideo = false;

                // 観測をリセット
                ResetIndexObservation();

                await ResetDecoderCoreAsync(seekPts);

                FlushQueues();
            });
        }



        private async Task GpuToCpuTransferLoop()
        {
            while (playbackState != PlaybackState.Stopped)
            {
                if (isSeeking)
                {
                    await Task.Delay(30);
                    continue;
                }

                // CPUバッファが少ないときに転送実行
                while (cpuFrames.Count < CPU_FRAME_TARGET && gpuFrames.TryDequeue(out var gpuFrame))
                {
                    try
                    {
                        if (gpuFrame.IsGpuFrame)
                            unsafe { gpuFrame.GetCpuFrame(); }

                        cpuFrames.Enqueue(gpuFrame);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GPU→CPU転送失敗] {ex.Message}");
                        gpuFrame.Dispose();
                    }
                }

                await Task.Delay(1);
            }
        }

        public ManagedFrame? DequeueCpuFrame()
        {
            if (cpuFrames.TryDequeue(out var frame))
                return frame;

            return null;
        }


        public void FlushQueues()
        {
            while (gpuFrames.TryDequeue(out var g)) g.Dispose();
            while (cpuFrames.TryDequeue(out var c)) c.Dispose();
        }




        // ---- 音声フレーム読み込み ----
        private async Task ReadAudioFrames()
        {
            const int maxRetry = 100;
            const int retryDelayMs = 10;
            int retryCount = 0;

            while (playbackState != PlaybackState.Stopped)
            {
                if (isSeeking)
                {
                    await Task.Delay(50);
                    continue;
                }

                // Pausedでもバッファが不足していれば読み込み続行
                bool canReadAudio = (playbackState != PlaybackState.Paused) || audioPlayer.BufferedDuration.TotalMilliseconds < 500;
                if (!canReadAudio)
                {
                    await Task.Delay(50);
                    continue;
                }

                if (audioPlayer.BufferedDuration.TotalSeconds >= 10)
                {
                    await Task.Delay(50);
                    continue;
                }

                ManagedFrame audioFrame = null;
                FrameReadResult result;

                await decoderLock.WaitAsync();
                try
                {
                    (result, audioFrame) = decoder.TryReadAudioFrame();
                }
                finally
                {
                    decoderLock.Release();
                }

                if (result == FrameReadResult.FrameAvailable && audioFrame != null)
                {
                    unsafe
                    {
                        long framePts = audioFrame.Frame->pts;
                        if (framePts == ffmpeg.AV_NOPTS_VALUE)
                            framePts = audioFrame.Frame->best_effort_timestamp;

                        // シーク後の古いフレームは破棄
                        if (seekPrefetchEndFrameIndex >= 0 && framePts <= seekPrefetchEndFrameIndex)
                        {
                            audioFrame.Dispose();
                            continue;
                        }

                        if (audioFrame.Frame == null || audioFrame.Frame->nb_samples <= 0)
                        {
                            audioFrame.Dispose();
                            continue;
                        }
                    }

                    var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                    audioPlayer.AddAudioData(audioData.AsMemory().Span);
                    audioFrame.Dispose();
                    retryCount = 0;
                }
                else if (result == FrameReadResult.FrameNotReady)
                {
                    await Task.Delay(retryDelayMs);
                    retryCount++;
                    if (retryCount > maxRetry) return;
                }
                else if (result == FrameReadResult.EndOfStream)
                {
                    endedStreamAudio = true;
                }
            }
        }



    }
}