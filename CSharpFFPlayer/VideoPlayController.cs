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

            for (int i = 0; i < 30; i++)
            {
                (result, managedFrame) = decoder.TryReadFrame();
                if (result == FrameReadResult.FrameAvailable)
                    break;

                Task.Delay(10).Wait();
            }

            if (result != FrameReadResult.FrameAvailable || managedFrame == null)
                throw new InvalidOperationException("最初のフレームの取得に失敗しました。");

            AVFrame* frame = managedFrame.Frame;
            int width = frame->width;
            int height = frame->height;
            AVPixelFormat srcFormat = (AVPixelFormat)frame->format;

            WriteableBitmap writeableBitmap = new WriteableBitmap(width, height, dpiX, dpiY, wpfPixelFormat, null);
            imageWriter = new ImageWriter(width, height, writeableBitmap);

            frameConveter = new FrameConveter();
            frameConveter.Configure(width, height, srcFormat, width, height, ffPixelFormat);

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

            // 再生メインループ
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

                // 明示的な一時停止またはバッファ待機中は処理をスキップ
                if (isPaused || isBuffering)
                {
                    resumed = false;
                    await Task.Delay(isPaused ? 150 : 1000);
                    continue;
                }

                // 再開直後に、現在の音声位置に合わせてフレームスキップ処理を行う
                if (!resumed)
                {
                    var position = TimeSpan.FromSeconds((double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond);
                    var (frameNumber, timeInFrame) = GetCurrentFrameInfo(fps, position);
                    int skipCount = frameNumber - frameIndex;

                    for (int i = 0; i < skipCount && frames.TryDequeue(out var skippedFrame); i++, frameIndex++)
                        skippedFrame.Dispose();

                    resumed = true;
                    audioPlayer?.Resume();
                    playbackStartTime = DateTime.Now - position;
                }

                // 次のフレームを描画（なければバッファ待機）
                if (frames.TryDequeue(out var frame))
                {
                    imageWriter.WriteFrame(frame, frameConveter);
                    frame.Dispose();
                    frameIndex++;
                }
                else
                {
                    if (isFrameEnded)
                    {
                        Stop();
                        return;
                    }

                    // フレームが足りない場合は待機
                    await Task.Delay(1000);
                    continue;
                }

                // フレームバッファが多すぎる場合、古いフレームを破棄して追いつく
                while (frames.Count > frameCap && frames.TryDequeue(out var oldFrame))
                {
                    oldFrame.Dispose();
                    frameIndex++;
                }

                // 音声と映像の同期補正処理
                var audioPos = TimeSpan.FromSeconds((double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond);
                var (idealFrameIndex, timeInFrame2) = GetCurrentFrameInfo(fps, audioPos);
                int frameDiff = idealFrameIndex - frameIndex;

                if (frameIndex > 0)
                {
                    // 補正係数計算（将来的なスムージングに使用可）
                    double rawBoost = (timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs) / baseFrameDurationMs;
                    double boostFactor = Math.Clamp(rawBoost, 0.7, 20.0);

                    // 映像が音声より大きく遅れている場合（3フレーム以上）
                    if (frameDiff >= 3)
                    {
                        int skipCount = frameDiff >= 10 ? frameDiff :
                                        frameDiff >= 5 ? Math.Min(5, frameDiff) :
                                        Math.Min(2, frameDiff);

                        for (int i = 0; i < skipCount && frames.TryDequeue(out var skippedFrame); i++)
                        {
                            skippedFrame.Dispose();
                            frameIndex++;
                        }

                        Console.WriteLine($"[スキップ] skippedFrames: {skipCount}, frameIndex: {frameIndex}, idealFrameIndex: {idealFrameIndex}, 差: {frameDiff}, 時刻差: {timeInFrame2.TotalMilliseconds:F2}ms");

                        frameDiff = idealFrameIndex - frameIndex;
                        rawBoost = (timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs) / baseFrameDurationMs;
                        boostFactor = Math.Clamp(rawBoost, 0.1, 20.0);
                    }
                    // 映像が音声より先行している場合 → 意図的に遅延を加える
                    else if (frameDiff < 0)
                    {
                        double delayMs = (-frameDiff * baseFrameDurationMs - timeInFrame2.TotalMilliseconds) * 1.1;
                        frameDuration = TimeSpan.FromMilliseconds(baseFrameDurationMs + delayMs);

                        Console.WriteLine($"[待機] frameIndex: {frameIndex}, idealFrameIndex: {idealFrameIndex}, 差: {frameDiff}, 時刻差: {timeInFrame2.TotalMilliseconds:F2}ms, delay: {delayMs:F2}ms");
                    }
                    // 少し遅れているがスキップ不要 → 少し短めに再生
                    else if (frameDiff > 0)
                    {
                        double d = (timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs) * (1000 - frameDiff * 150);
                        double delayMs = baseFrameDurationMs - d;
                        frameDuration = TimeSpan.FromMilliseconds(Math.Max(delayMs, baseFrameDurationMs / (frameDiff + 2)));
                    }
                    // 完全に同期しているとき
                    else
                    {
                        double d = timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs;
                        double delayMs = baseFrameDurationMs - d;
                        frameDuration = TimeSpan.FromMilliseconds(Math.Max(delayMs, baseFrameDurationMs / (frameDiff + 2)));
                    }
                }

                // フレーム間待機
                await Task.Delay(frameDuration);
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

        /// <summary>
        /// 非同期に映像フレームを読み込みキューに格納する
        /// </summary>
        private async Task ReadFrames()
        {
            while (isPlaying)
            {
                if (frames.Count >= frameCap)
                {
                    await Task.Delay(1);
                    continue;
                }

                var sw = Stopwatch.StartNew();
                var (result, frame) = decoder.TryReadFrame();
                sw.Stop();

                switch (result)
                {
                    case FrameReadResult.FrameAvailable:
                        if (!isPlaying)
                        {
                            frame.Dispose();
                            return;
                        }

                        frames.Enqueue(frame);
                        decodedFrames++;

                        if (sw.ElapsedMilliseconds > baseFrameDurationMs && fps != 0.0)
                        {
                            Console.WriteLine($"[警告] フレームデコードに {sw.ElapsedMilliseconds}ms (>1/{fps}) かかりました");
                        }
                        break;

                    case FrameReadResult.FrameNotReady:
                        await Task.Delay(1);
                        break;

                    case FrameReadResult.EndOfStream:
                        isFrameEnded = true;
                        return;
                }

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
