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
        // 描画ループ / Producer / 転送ループから読まれるため volatile
        private volatile RenderTargetType targetType;

        /// <summary>現在の描画ターゲット。</summary>
        public RenderTargetType CurrentRenderTarget => targetType;

        /// <summary>
        /// 今この場で描画ターゲットを切り替えられるか。
        /// 停止中は decoder が破棄済み、再生終了後は PlayInternal が抜けており
        /// 切り替えても再生を再開できないため、いずれも不可とする。
        /// </summary>
        public bool CanSwitchRenderTarget =>
            decoder != null && imageWriter != null &&
            playbackState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering;

        // 描画ターゲット差し替えと「UI への提示」を相互排他にするゲート。
        // ※この lock の内側では絶対に await しない（UI スレッドとのデッドロック回避）。
        // ※Stop() による decoder 破棄はこのゲートでは守られない。
        //   切り替えは Playing/Paused/Buffering のみ許可することで回避している。
        private readonly object renderSwapGate = new();

        // 切り替えの二重実行防止
        private int switchingRenderTarget = 0;

        // 描画ターゲット再構築のためにキャッシュしておく情報
        private int frameWidth;
        private int frameHeight;
        private AVPixelFormat srcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        private int bitmapDpiX = 96;
        private int bitmapDpiY = 96;

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
        private const int NO_FRAME_WAIT_MS = 2;          // 提示できるフレームが無いときの待機
        private const int LATE_RESYNC_FRAMES = 3;        // この枚数以上遅れたら理想時刻を再同期
        private const int DRAW_LOOP_PARK_MS = 10;        // 描画ループが停止するまでの余裕

        /// <summary>
        /// フレーム毎の [Draw] ログを出すか。
        /// ペーシングの検証用。Console 出力は描画ループ上では重いため、
        /// 確認が済んだら false にしてよい。
        /// </summary>
        public bool VerboseDrawLog { get; set; } = true;

        // --- 状態 ---
        private PlaybackState playbackState = PlaybackState.Stopped;
        public bool IsPlaying => playbackState == PlaybackState.Playing;
        public bool IsPaused => playbackState == PlaybackState.Paused;
        public bool IsBuffering => playbackState == PlaybackState.Buffering;
        public bool IsEnded => playbackState == PlaybackState.Ended;

        // --- デコーダ/描画 ---
        private Decoder decoder;
        private UnifiedImageWriter? imageWriter;
        private FrameConveter? frameConveter;

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

        // コマ送りで映像だけ進めた結果、音声位置がずれているか。
        // コマ送りのたびに音声シークを走らせると AVFormatContext（映像と共用）が
        // 動いてしまうため、再開時にまとめて合わせる。
        private volatile bool needsAudioResync = false;

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

                // 描画ターゲット再構築のために保持しておく
                frameWidth = managedFrame.Frame->width;
                frameHeight = managedFrame.Frame->height;
                srcPixFmt = (AVPixelFormat)managedFrame.Frame->format;
                bitmapDpiX = dpiX;
                bitmapDpiY = dpiY;
            }

            var (source, writer, conveter) = CreateRenderTargetCore(targetType, dpiX, dpiY);
            frameConveter = conveter;
            imageWriter = writer;

            managedFrame.Index = GetFrameIndex(managedFrame) ?? -1;
            if (IsCpuTarget)
            {
                // 初回フレームを投げて UI 側へ
                imageWriter.EnqueueFrame(managedFrame);
                cpuFrames.Enqueue(managedFrame);
            }
            else
            {
                // 初回フレームは GPU キューで扱う
                gpuFrames.Enqueue(managedFrame);
            }

            return source;
        }

        // ----------------------------------
        // 内部：描画ターゲット一式（変換器 + ImageSource + Writer）の生成
        // ----------------------------------
        private (ImageSource source, UnifiedImageWriter writer, FrameConveter conveter)
            CreateRenderTargetCore(RenderTargetType type, int dpiX, int dpiY)
        {
            if (frameWidth <= 0 || frameHeight <= 0 || srcPixFmt == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new InvalidOperationException("フレーム情報が未取得です。CreateBitmapAsync を先に呼んでください。");

            // FrameConveter は使い回さず毎回新規に作る
            // （WriteableBitmap 経路で設定された dstStride が BGRA 経路に持ち越されるのを防ぐ）
            var conveter = new FrameConveter();

            if (type == RenderTargetType.WriteableBitmap)
            {
                // CPU(BGR24) で描画
                conveter.Configure(frameWidth, frameHeight, srcPixFmt, frameWidth, frameHeight, CpuFfPixFmt);
                var wb = new WriteableBitmap(frameWidth, frameHeight, dpiX, dpiY, WpfPixFmt, null);
                var writer = new UnifiedImageWriter(RenderTargetType.WriteableBitmap, frameWidth, frameHeight, conveter, wb);
                Log($"CreateRenderTarget: WriteableBitmap {frameWidth}x{frameHeight}");
                return (wb, writer, conveter);
            }
            else
            {
                // D3DImage(BGRA) で描画
                conveter.Configure(frameWidth, frameHeight, srcPixFmt, frameWidth, frameHeight, D3dFfPixFmt);
                var di = new D3DImage();
                var writer = new UnifiedImageWriter(RenderTargetType.D3DImage, frameWidth, frameHeight, conveter, di: di);
                Log($"CreateRenderTarget: D3DImage {frameWidth}x{frameHeight}");
                return (di, writer, conveter);
            }
        }

        // ----------------------------------
        // 公開：描画ターゲット（通常 / D3D）の切り替え
        // ----------------------------------
        /// <summary>
        /// 再生中／一時停止中に描画経路を切り替える。
        /// 新しい ImageSource は <paramref name="attach"/> 経由で UI に渡す
        /// （旧 Writer を破棄する前に差し替える必要があるため）。
        /// </summary>
        /// <returns>切り替えを行った場合 true。既に同じターゲットなら false。</returns>
        public async Task<bool> SwitchRenderTargetAsync(RenderTargetType newTarget, Action<ImageSource> attach)
        {
            ArgumentNullException.ThrowIfNull(attach);

            if (decoder == null || imageWriter == null)
                throw new InvalidOperationException("CreateBitmapAsync 後に呼び出してください。");

            // Stop() 済みだと decoder が破棄されているため切り替え不可
            if (playbackState == PlaybackState.Stopped)
                throw new InvalidOperationException("停止中は描画ターゲットを切り替えられません。");

            if (targetType == newTarget) return false;

            if (Interlocked.CompareExchange(ref switchingRenderTarget, 1, 0) != 0)
            {
                Warn("[Switch] 二重実行は無視されました。");
                return false;
            }

            try
            {
                bool wasPlaying = IsPlaying;
                Pause();

                long resumeFrameIndex = frameIndex;
                Log($"[Switch] {targetType} -> {newTarget} (frameIndex={resumeFrameIndex}, wasPlaying={wasPlaying})");

                UnifiedImageWriter? oldWriter = null;
                FrameConveter? oldConveter = null;

                await WithProducerPausedAsync(() =>
                {
                    FlushQueues();
                    Interlocked.Exchange(ref lastEnqueuedFrameIndex, -1);

                    // 先に新しい一式を作ってから差し替える
                    var (source, writer, conveter) = CreateRenderTargetCore(newTarget, bitmapDpiX, bitmapDpiY);

                    lock (renderSwapGate)
                    {
                        oldWriter = imageWriter;
                        oldConveter = frameConveter;

                        imageWriter = writer;
                        frameConveter = conveter;
                        targetType = newTarget;
                    }

                    // UI の Image.Source を新しいものに差し替えてから旧リソースを破棄する
                    attach(source);

                    return Task.CompletedTask;
                });

                oldWriter?.Dispose();
                oldConveter?.Dispose();

                // 元の位置へ戻してバッファを作り直す
                await SeekToExactFrameAsync(resumeFrameIndex);

                if (wasPlaying) await Play();

                Log($"[Switch 完了] target={targetType}");
                return true;
            }
            finally
            {
                Interlocked.Exchange(ref switchingRenderTarget, 0);
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
                // コマ送りで映像だけ進めていた場合、ここで映像・音声とも現在位置に揃える。
                // （コマ送り毎に音声シークすると共用の AVFormatContext が動いてしまう）
                if (needsAudioResync)
                {
                    Log("[Play] コマ送り分の位置を再同期します。");
                    await SeekToExactFrameAsync(frameIndex);
                }

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

            // 描画リソースは差し替えゲートの内側で破棄し、参照も切っておく
            // （破棄済みの D3D9 デバイスを掴んだままにしないため）
            lock (renderSwapGate)
            {
                imageWriter?.Dispose();
                imageWriter = null;
                frameConveter?.Dispose();
                frameConveter = null;
            }

            audioPlayer?.Dispose();
            decoder?.Dispose();

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

                // 4) UI に提示（CPU/D3D 共通）。
                //    提示処理でフレームは消費されるためキューには戻さない。
                //    以前は CPU 経路で「表示用」と「キュー用」に同じフレームを二重登録しており、
                //    また D3D 経路では表示せずキューに積むだけだったため、
                //    一時停止中にシークしても画面が更新されなかった。
                if (targetFrame != null)
                {
                    PresentSingleFrame(targetFrame, targetFrameIndex);
                }
                else
                {
                    Warn($"[Seek] 目標フレーム {targetFrameIndex} を取得できませんでした。");
                    frameIndex = (int)targetFrameIndex;
                }

                // 5) 音声も合わせる
                await SeekAudioAsync(frameIndex);
                needsAudioResync = false;

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
        // 公開：コマ送り
        // ----------------------------------
        /// <summary>
        /// コマ送り。一時停止したまま次のフレームを 1 枚だけ表示する。
        /// 再生中に呼ばれた場合は一時停止してから進める。
        /// バッファに次のフレームが無ければシークで取りに行く。
        /// </summary>
        public async Task<bool> StepForwardAsync()
        {
            if (decoder == null || imageWriter == null || frameConveter == null) return false;

            if (playbackState is PlaybackState.Stopped or PlaybackState.Ended)
            {
                Warn("[Step] 停止中／再生終了後はコマ送りできません。");
                return false;
            }

            long target = frameIndex + 1;
            bool needSeek = false;

            if (!await seekLock.WaitAsync(0))
            {
                Warn("[Step] シーク／コマ送りの二重実行は無視されました。");
                return false;
            }

            try
            {
                // 再生中なら止める。描画ループが今のフレームを出し終えるまで待つ。
                if (playbackState != PlaybackState.Paused)
                {
                    Pause();
                    await Task.Delay((int)baseFrameDurationMs + DRAW_LOOP_PARK_MS);
                }

                // 停止するまでに描画ループが進んでいる可能性があるので取り直す
                target = frameIndex + 1;

                ManagedFrame? next = null;
                await WithProducerPausedAsync(() =>
                {
                    next = DequeueFrameAtLeast(target);
                    return Task.CompletedTask;
                });

                if (next != null)
                {
                    if (!PresentSingleFrame(next, target)) return false;

                    // 音声位置は動かしていないので、再開時にまとめて合わせる
                    needsAudioResync = true;
                    Log($"[Step] コマ送り → frameIndex={frameIndex}");
                    return true;
                }

                needSeek = true;
            }
            finally
            {
                seekLock.Release();
            }

            // バッファに無かった場合はシークで取りに行く（音声もここで同期される）。
            // SeekToExactFrameAsync も seekLock を取るため、必ず解放してから呼ぶこと。
            if (needSeek)
            {
                Log($"[Step] バッファに {target} が無いためシークします。");
                return await SeekToExactFrameAsync(target);
            }

            return false;
        }

        /// <summary>
        /// アクティブなキューから Index が minIndex 以上のフレームを 1 枚取り出す。
        /// それより古いフレームは破棄する。見つからなければ null。
        /// </summary>
        private ManagedFrame? DequeueFrameAtLeast(long minIndex)
        {
            while (true)
            {
                var f = DequeueForTarget();
                if (f == null) return null;

                // Index 不明(-1)のフレームはそのまま採用する
                if (f.Index >= 0 && f.Index < minIndex) { f.Dispose(); continue; }
                return f;
            }
        }

        /// <summary>
        /// フレームを 1 枚だけ UI に提示し、frameIndex を更新する。
        /// フレームは提示処理側で解放されるため、呼び出し後に参照してはならない。
        /// </summary>
        private bool PresentSingleFrame(ManagedFrame frame, long fallbackIndex)
        {
            unsafe
            {
                if (frame.Frame == null) { frame.Dispose(); return false; }

                // CPU ターゲットなら CPU フレーム化してから渡す
                if (IsCpuTarget && frame.IsGpuFrame)
                {
                    frame.GetCpuFrame();
                    if (frame.Frame == null) { frame.Dispose(); return false; }
                }
            }

            long idx = frame.Index;
            EnqueueForUi(frame);   // ここでフレームは解放される
            frameIndex = (int)(idx < 0 ? fallbackIndex : idx);
            return true;
        }

        // ----------------------------------
        // 内部：音声シーク
        // ----------------------------------
        private async Task SeekAudioAsync(int videoFrameIndex)
        {
            if (audioPlayer == null)
            {
                Warn("[音声シーク] AudioPlayer 未初期化のためスキップします。");
                return;
            }

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
                double tickToMs = 1000.0 / Stopwatch.Frequency;

                // 「次にフレームを提示すべき理想時刻(ms)」を絶対値で保持する。
                // 1 フレームごとに baseFrameDurationMs を加算していくため、
                // Task.Delay の整数丸め誤差やスリープのブレが累積しない。
                // float だと長時間再生で刻み幅が足りなくなるので double。
                double nextPresentMs = sw.ElapsedTicks * tickToMs;

                // 提示を伴わない待機（一時停止・再開同期・フレーム欠落）のあとは
                // 理想時刻を取り直す。取り直さないと「遅れた分の一括追いつき」で
                // フレームが早送り再生されてしまう。
                bool clockDirty = false;

                // 実測のフレーム間隔（ログ用）
                long lastTicks = sw.ElapsedTicks;

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
                        clockDirty = true;
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

                        // ここまでで 100ms 以上待っているため理想時刻を取り直す
                        clockDirty = true;
                    }

                    // 提示を伴わない待機のあとは理想時刻の基準を取り直す
                    if (clockDirty)
                    {
                        long anchor = sw.ElapsedTicks;
                        nextPresentMs = anchor * tickToMs;
                        lastTicks = anchor;
                        clockDirty = false;
                    }

                    // --- フレーム取得→提示 ---
                    var mf = DequeueForTarget();
                    if (mf == null)
                    {
                        // キューが空。Task.Delay(0) は同期完了しビジーループになるため必ず待つ。
                        // 理想時刻は進めないので、フレームが供給されれば遅れを取り戻せる。
                        await Task.Delay(NO_FRAME_WAIT_MS);
                        continue;
                    }

                    bool presented = false;
                    unsafe
                    {
                        if (mf.Frame == null)
                        {
                            mf.Dispose();
                        }
                        else
                        {
                            try
                            {
                                if (IsCpuTarget && mf.IsGpuFrame)
                                {
                                    mf.GetCpuFrame();
                                }

                                if (mf.Frame == null)
                                {
                                    mf.Dispose();
                                }
                                else
                                {
                                    // UIへ提示（CPU/D3D を吸収）
                                    EnqueueForUi(mf);

                                    frameIndex = (int)(mf.Index < 0 ? frameIndex + 1 : mf.Index);
                                    presented = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Err($"[Present] {ex.Message}");
                                mf.Dispose();
                            }
                        }
                    }

                    if (!presented)
                    {
                        // 提示できなかったフレームでは理想時刻を進めない
                        clockDirty = true;
                        await Task.Delay(NO_FRAME_WAIT_MS);
                        continue;
                    }

                    // --- 音声との微調整 ---
                    double aSec = (double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond;
                    var (idealIdx2, inFrame2) = GetCurrentFrameInfo(TimeSpan.FromSeconds(aSec));
                    int diff = idealIdx2 - frameIndex;
                    // 音声に対する映像のズレ(ms)。正なら映像が遅れている。
                    double offset = inFrame2.TotalMilliseconds + diff * baseFrameDurationMs;

                    // 理想時刻は 1 フレーム分だけ進める。
                    // offset はこの後の待ち時間にのみ効かせる（両方に適用すると補正が二重になり発振する）。
                    nextPresentMs += baseFrameDurationMs;

                    long nowTicks = sw.ElapsedTicks;
                    double nowMs = nowTicks * tickToMs;
                    double err = nowMs - nextPresentMs;   // 正なら理想時刻より遅れている

                    // 大きく遅れた場合は理想時刻を現在に引き戻す（遅れ分の一括追いつきを防ぐ）
                    if (err > baseFrameDurationMs * LATE_RESYNC_FRAMES)
                    {
                        Warn($"[Draw] {err:F1}ms 遅延のため理想時刻を再同期");
                        nextPresentMs = nowMs;
                        err = 0;
                    }

                    double waitMs = (nextPresentMs - nowMs) - offset;

                    double usedMs = (nowTicks - lastTicks) * tickToMs;
                    lastTicks = nowTicks;

                    if (VerboseDrawLog)
                        Console.WriteLine($"[Draw] idx={frameIndex} used={usedMs:F2}ms wait={waitMs:F2}ms err={err:F2}ms offset={offset:F2}ms");

                    if (waitMs > 0)
                        await Task.Delay((int)Math.Round(waitMs));
                    else
                        await Task.Yield();   // 遅れている時も必ずスレッドを手放す
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
            // 描画ターゲット差し替えと排他。ここでは await しないこと。
            lock (renderSwapGate)
            {
                if (imageWriter == null || frameConveter == null)
                {
                    frame.Dispose();
                    return;
                }

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
