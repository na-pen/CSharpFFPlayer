using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using FFmpeg.AutoGen;
using NAudio.Wave;

namespace CSharpFFPlayer
{
    public class VideoPlayController
    {
        // FFmpeg で使用するピクセル形式（BGR24）
        private static readonly AVPixelFormat ffPixelFormat = AVPixelFormat.AV_PIX_FMT_BGR24;

        // WPF におけるピクセル形式（Bgr24）
        private static readonly PixelFormat wpfPixelFormat = PixelFormats.Bgr24;

        private TimeSpan pausedDuration = TimeSpan.Zero;
        private DateTime pauseStartTime;

        // 音声バッファ用ストリームとロック
        private MemoryStream audioStream = new MemoryStream();
        private long audioReadPosition = 0;
        private readonly object audioLock = new object();

        // 映像・音声のデコードや描画、変換処理関連
        private Decoder decoder;
        private ImageWriter imageWriter;
        private FrameConveter frameConveter;

        // 再生状態の管理
        private bool isPlaying = false;
        private bool isPaused = false;
        private bool isBuffering = false;
        public bool isSeeking { get; set; } = false;
        public bool IsPlaying => isPlaying;
        public bool IsPaused => isPaused;

        private bool isFrameEnded;
        private const int frameCap = 100; // 最大フレームバッファ数
        private const int waitTime = 150; // バッファ待機時間(ms)

        private uint decodedFrames = 0; // デコード済みフレーム数

        private double fps = 0; // フレームレート
        private double baseFrameDurationMs = 0; // 1フレームあたりの表示時間（ミリ秒）

        private ConcurrentQueue<ManagedFrame> frames = new ConcurrentQueue<ManagedFrame>();
        private Task playTask;

        private AudioPlayer audioPlayer;

        private bool isEndOfStream = false;

        /// <summary>
        /// ファイルを開いて FFmpeg デコーダーを初期化
        /// </summary>
        public void OpenFile(string path)
        {
            decoder = new Decoder();
            decoder.OpenFile(path);
            decoder.InitializeDecoders(false);
        }

        /// <summary>
        /// 最初のフレームを取得し、WPF 描画用の WriteableBitmap を作成する
        /// </summary>
        public unsafe WriteableBitmap CreateBitmap(int dpiX, int dpiY)
        {
            if (decoder is null)
                throw new InvalidOperationException("動画を開いてから描画先を作成してください。");

            ManagedFrame managedFrame = null;
            FrameReadResult result = FrameReadResult.FrameNotReady;

            // フレーム取得（最大30回リトライ）
            for (int i = 0; i < 30; i++)
            {
                (result, managedFrame) = decoder.TryReadFrame();
                if (result == FrameReadResult.FrameAvailable)
                    break;

                Task.Delay(10).Wait();
            }

            if (result != FrameReadResult.FrameAvailable || managedFrame == null)
                throw new InvalidOperationException("最初のフレームの取得に失敗しました。");

            // ① CPUメモリに転送されたフレームを取得（必要に応じて転送される）
            managedFrame.GetCpuFrame();

            // ② 解像度とピクセルフォーマットを取得
            AVFrame* frame = managedFrame.Frame;
            int width = frame->width;
            int height = frame->height;
            AVPixelFormat srcFormat = (AVPixelFormat)frame->format;

            // ③ WPF用のWriteableBitmap作成
            WriteableBitmap writeableBitmap = new WriteableBitmap(width, height, dpiX, dpiY, wpfPixelFormat, null);
            imageWriter = new ImageWriter(width, height, writeableBitmap);

            // ④ フレーム変換器を初期化（GPUフォーマットでないことを保証）
            frameConveter = new FrameConveter();
            frameConveter.Configure(width, height, srcFormat, width, height, ffPixelFormat);

            // ⑤ 一時フレームを破棄
            managedFrame.Dispose();

            return writeableBitmap;
        }


        /// <summary>
        /// 映像の再生を開始する（または一時停止から再開）
        /// </summary>
        public async Task Play()
        {
            if (isPaused)
            {
                isPaused = false;
            }
            else
            {
                isPlaying = true;
                audioPlayer = new AudioPlayer();

                var waveFormat = new WaveFormat(decoder.AudioCodecContext.sample_rate, 16, decoder.AudioCodecContext.ch_layout.nb_channels);
                audioPlayer.Init(waveFormat, volume: 0.5f, latencyMs: 200);

                playTask = PlayInternal();
            }
            await playTask;
        }

        /// <summary>
        /// 一時停止を行う
        /// </summary>
        public void Pause()
        {
            isPaused = true;
            audioPlayer?.Pause();
            pauseStartTime = DateTime.Now;
        }

        /// <summary>
        /// 再生を停止して状態をリセットする
        /// </summary>
        public void Stop()
        {
            isPlaying = false;
            isPaused = false;

            while (frames.TryDequeue(out var remainingFrame))
            {
                remainingFrame.Dispose();
            }

            frames.Clear();
            audioPlayer?.Dispose();
            decoder.Dispose();
            frameConveter?.Dispose();
        }

        /// <summary>
        /// 映像・音声の再生を内部的に実行するメインループ
        /// </summary>
        private async Task PlayInternal()
        {
            // フレームと音声のデコードを並列で開始
            _ = Task.Run(() => ReadFrames());
            _ = Task.Run(() => ReadAudioFrames());

            // バッファが一定量たまるまで待機
            await WaitForBuffer();

            // フレームレートの計算（1000/33 の場合のみ補正→Windowsのエンコードエラーの模様）
            var avg = decoder.VideoStream.avg_frame_rate;
            fps = (avg.num == 1000 && avg.den == 33) ? 29.97 : avg.num / (double)avg.den;
            baseFrameDurationMs = 1000.0 / fps;
            TimeSpan frameDuration = TimeSpan.FromMilliseconds(baseFrameDurationMs);

            int frameIndex = 0;
            isFrameEnded = false;
            bool resumed = false;

            DateTime playbackStartTime = DateTime.Now;
            Queue<double> boostHistory = new();

            audioPlayer.Start();

            var stopwatch = new Stopwatch();

            while (isPlaying)
            {
                // 映像バッファが不足 → 自動一時停止
                if (!isBuffering && frames.Count < frameCap / 4 && !isFrameEnded)
                {
                    isBuffering = true;
                    audioPlayer?.Pause();
                }

                // 映像バッファが回復 → 自動再開
                if (isBuffering && frames.Count >= frameCap / 1.2)
                {
                    isBuffering = false;
                    audioPlayer?.Resume();
                }

                // 明示的な一時停止またはバッファ待機中
                if (isPaused || isBuffering)
                {
                    resumed = false;
                    await Task.Delay(isPaused ? 150 : 100);
                    continue;
                }

                // 再開直後：現在の音声位置にフレームを同期
                if (!resumed)
                {
                    var position = TimeSpan.FromSeconds((double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond);
                    var (frameNumber, _) = GetCurrentFrameInfo(fps, position);
                    int skipCount = frameNumber - frameIndex;

                    for (int i = 0; i < skipCount && frames.TryDequeue(out var skipped); i++, frameIndex++)
                        skipped.Dispose();

                    resumed = true;
                    audioPlayer?.Resume();
                    playbackStartTime = DateTime.Now - position;
                }


                // 次のフレームを描画
                if (frames.TryDequeue(out var frame))
                {
                    // GPUフレームが未転送で残っていた場合 → スキップ（エラー防止）
                    if (frame.IsGpuFrame)
                    {
                        Console.WriteLine("[描画スキップ] GPU未転送フレームをスキップしました");
                        frame.Dispose();
                        continue;
                    }

                    stopwatch.Restart();

                    imageWriter.WriteFrame(frame, frameConveter);
                    frame.Dispose();
                    frameIndex++;

                    stopwatch.Stop();
                }
                else
                {
                    if (isFrameEnded)
                    {
                        Stop();
                        return;
                    }

                    await Task.Delay(30); // 復帰高速化
                    continue;
                }

                // バッファ過多 → 古いフレームを破棄
                while (frames.Count > frameCap && frames.TryDequeue(out var old))
                {
                    old.Dispose();
                    frameIndex++;
                }

                // 同期補正処理
                var audioPos = TimeSpan.FromSeconds((double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond);
                var (idealFrameIndex, timeInFrame2) = GetCurrentFrameInfo(fps, audioPos);
                int frameDiff = idealFrameIndex - frameIndex;

                double adjustedDelayMs = baseFrameDurationMs;

                if (frameIndex > 0)
                {
                    double offsetMs = timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs;

                    if (frameDiff >= 3)
                    {
                        int skip = frameDiff >= 10 ? frameDiff :
                                   frameDiff >= 5 ? Math.Min(5, frameDiff) :
                                   Math.Min(2, frameDiff);

                        for (int i = 0; i < skip && frames.TryDequeue(out var skipFrame); i++, frameIndex++)
                            skipFrame.Dispose();

                        Console.WriteLine($"[スキップ] {skip}フレーム, 差: {frameDiff}, 時刻差: {timeInFrame2.TotalMilliseconds:F2}ms");

                        // 再計算
                        frameDiff = idealFrameIndex - frameIndex;
                        offsetMs = timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs;
                    }
                    else if (frameDiff < 0)
                    {
                        double delayMs = (-offsetMs) * 1.3;
                        adjustedDelayMs = baseFrameDurationMs + delayMs;

                        //Console.WriteLine($"[待機] 差: {frameDiff}, 時刻差: {timeInFrame2.TotalMilliseconds:F2}ms, 遅延: {delayMs:F2}ms");
                    }
                    else if (frameDiff > 0)
                    {
                        double d = offsetMs * (1000 - frameDiff * 150);
                        adjustedDelayMs = Math.Max(baseFrameDurationMs - d, baseFrameDurationMs / (frameDiff + 2));
                    }
                    else
                    {
                        double delayMs = baseFrameDurationMs - offsetMs;
                        adjustedDelayMs = Math.Max(delayMs, baseFrameDurationMs / 2);
                    }
                }

                // 実描画時間を除いて残り時間だけ待機
                long used = stopwatch.ElapsedMilliseconds;
                int remaining = Math.Max(1, (int)(adjustedDelayMs - used));
                await Task.Delay(remaining);
            }

        }


        /// <summary>
        /// 指定された再生時間におけるフレーム番号とフレーム内の経過時間を取得
        /// </summary>
        public static (int frameNumber, TimeSpan timeInFrame) GetCurrentFrameInfo(double fps, TimeSpan playbackTime)
        {
            double totalMilliseconds = playbackTime.TotalMilliseconds;
            int frameNumber = (int)(totalMilliseconds / (1000.0 / fps));
            double frameStartTime = frameNumber * (1000.0 / fps);
            double timeInFrame = totalMilliseconds - frameStartTime;
            return (frameNumber, TimeSpan.FromMilliseconds(timeInFrame));
        }

        /// <summary>
        /// フレームバッファが満たされるまで待機
        /// </summary>
        private async Task WaitForBuffer()
        {
            while (frames.Count < frameCap && isPlaying)
                await Task.Delay(waitTime);
        }

        private static readonly SemaphoreSlim transferLimiter = new(4); // 同時転送制限

        /// <summary>
        /// 非同期に映像フレームを読み込み、GPU → CPU 転送を制御しつつキューに格納する。
        /// </summary>
        private async Task ReadFrames()
        {
            int transferredCount = 0;
            int totalFramesToTransfer = 0;

            while (isPlaying)
            {
                // キューの上限に達している場合は少し待機してスキップ
                if (frames.Count >= frameCap)
                {
                    await Task.Delay(1);
                    continue;
                }

                var sw = Stopwatch.StartNew();
                var (result, frame) = decoder.TryReadFrame();
                sw.Stop();

                if (result == FrameReadResult.FrameAvailable)
                {
                    if (!isPlaying)
                    {
                        frame.Dispose();
                        return;
                    }

                    frames.Enqueue(frame);
                    decodedFrames++;
                    Interlocked.Increment(ref totalFramesToTransfer); // デコード済みフレーム数カウント

                    if (sw.ElapsedMilliseconds > baseFrameDurationMs && fps != 0.0)
                    {
                        Console.WriteLine($"[警告] フレームデコードに {sw.ElapsedMilliseconds}ms (>1/{fps}) かかりました");
                    }
                }
                else if (result == FrameReadResult.FrameNotReady)
                {
                    await Task.Delay(1);
                }
                else if (result == FrameReadResult.EndOfStream)
                {
                }

                // 転送処理（frameCapが十分ある時は非同期、少ないときは即時）

                const int preTransferThreshold = 10;
                int threshold = Math.Min(preTransferThreshold, frames.Count);
                bool prevIsFrameEnded = isFrameEnded;
                isFrameEnded = result == FrameReadResult.EndOfStream;
                if (!prevIsFrameEnded && isFrameEnded)
                {
                    Console.WriteLine("[EOF] 映像ストリームの終端を検出しました");
                }

                foreach (var f in frames.Take(threshold))
                {
                    if (!f.IsGpuFrame) continue; // すでに転送済み

                    if (frames.Count > preTransferThreshold)
                    {
                        // バッファが安定 → 非同期転送
                        _ = Task.Run(async () =>
                        {
                            await transferLimiter.WaitAsync();
                            try
                            {
                                unsafe { f.GetCpuFrame(); }
                                Interlocked.Increment(ref transferredCount);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[転送失敗] {ex.Message}");
                            }
                            finally
                            {
                                transferLimiter.Release();
                            }
                        });
                    }
                    else
                    {
                        // バッファ不足時 → 即時同期転送
                        try
                        {
                            unsafe { f.GetCpuFrame(); }
                            Interlocked.Increment(ref transferredCount);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[即時転送失敗] {ex.Message}");
                        }
                    }
                }

                // 転送終了判定：バッファ少量＋EOF受信＋全転送完了時のみ終了
                if (frames.Count < preTransferThreshold &&
                    isEndOfStream &&
                    Volatile.Read(ref transferredCount) >= Volatile.Read(ref totalFramesToTransfer))
                {
                    Console.WriteLine("映像フレームの終端（すべて転送済み）");
                    return;
                }



                // 高負荷緩和のための軽いスローダウン
                if (frames.Count > frameCap * 0.8)
                {
                    await Task.Delay(1);
                }
            }
        }





        /// <summary>
        /// 非同期に音声フレームを読み取り、AudioPlayer に供給する
        /// </summary>
        private async Task ReadAudioFrames()
        {
            while (isPlaying)
            {
                if (isPaused)
                {
                    await Task.Delay(100);
                    continue;
                }

                var audioFrame = decoder.ReadAudioFrame();
                if (audioFrame is null) break;

                using (var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame))
                {
                    byte[] data = audioData.AsMemory().ToArray();

                    while (audioPlayer.BufferedDuration.TotalSeconds >= 10)
                    {
                        await Task.Delay(100);
                    }

                    audioPlayer.AddAudioData(data);
                }

                audioFrame.Dispose();
            }
        }
    }
}
