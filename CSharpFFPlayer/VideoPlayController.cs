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
        private static readonly AVPixelFormat ffPixelFormat = AVPixelFormat.AV_PIX_FMT_BGR24;
        private static readonly PixelFormat wpfPixelFormat = PixelFormats.Bgr24;

        private TimeSpan pausedDuration = TimeSpan.Zero;
        private DateTime pauseStartTime;

        private MemoryStream audioStream = new MemoryStream();

        private long audioReadPosition = 0;
        private readonly object audioLock = new object();

        private Decoder decoder;
        private ImageWriter imageWriter;
        private FrameConveter frameConveter;

        private bool isPlaying = false;
        private bool isPaused = false;
        bool isBuffering = false;
        public bool isSeeking { get; set; } = false;
        public bool IsPlaying => isPlaying;
        public bool IsPaused => isPaused;

        private bool isFrameEnded;
        private const int frameCap = 100;
        private const int waitTime = 150;

        private uint decodedFrames = 0;

        double fps = 0;
        double baseFrameDurationMs = 0;
        double baseFrameDurationSeconds = 0;

        private ConcurrentQueue<ManagedFrame> frames = new ConcurrentQueue<ManagedFrame>();
        private Task playTask;

        private AudioPlayer audioPlayer;

        public VideoPlayController()
        {
        }

        public void OpenFile(string path)
        {
            decoder = new Decoder();
            decoder.OpenFile(path);
            decoder.InitializeDecoders(false);
        }

        public unsafe WriteableBitmap CreateBitmap(int dpiX, int dpiY)
        {
            if (decoder is null)
            {
                throw new InvalidOperationException("動画を開いてから描画先を作成してください。");
            }

            ManagedFrame managedFrame = null;
            FrameReadResult result = FrameReadResult.FrameNotReady;

            // 最初のフレームが来るまで数回リトライ（最大30回）
            for (int i = 0; i < 30; i++)
            {
                (result, managedFrame) = decoder.TryReadFrame();
                if (result == FrameReadResult.FrameAvailable)
                    break;

                // 少し待つ（10ms程度）
                Task.Delay(10).Wait();
            }

            if (result != FrameReadResult.FrameAvailable || managedFrame == null)
            {
                throw new InvalidOperationException("最初のフレームの取得に失敗しました。");
            }

            AVFrame* frame = managedFrame.Frame;
            int width = frame->width;
            int height = frame->height;
            AVPixelFormat srcFormat = (AVPixelFormat)frame->format;

            // WPF用のWriteableBitmapを生成
            WriteableBitmap writeableBitmap = new WriteableBitmap(width, height, dpiX, dpiY, wpfPixelFormat, null);
            imageWriter = new ImageWriter(width, height, writeableBitmap);

            // フレーム変換器の初期化
            frameConveter = new FrameConveter();
            frameConveter.Configure(
                width,
                height,
                srcFormat,
                width,
                height,
                ffPixelFormat
            );

            managedFrame.Dispose(); // フレームを解放

            return writeableBitmap;
        }

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
                audioPlayer.Init(waveFormat, volume: 0.5f, latency: 200);

                playTask = PlayInternal();
            }
            await playTask;
        }

        public void Pause()
        {
            isPaused = true;
            audioPlayer?.Pause();
            pauseStartTime = DateTime.Now;
        }

        public void Stop()
        {
            isPlaying = false;
            isPaused = false;

            // 残ったフレームを確実にDispose
            while (frames.TryDequeue(out var remainingFrame))
            {
                remainingFrame.Dispose();
            }

            frames.Clear();
            audioPlayer?.Dispose();
            decoder.Dispose();
        }

        // PlayInternal側での定期的Dispose（安全策）
        private async Task PlayInternal()
        {
            Task.Run(() => ReadFrames());
            Task.Run(() => ReadAudioFrames());

            await WaitForBuffer();

            fps = decoder.VideoStream.r_frame_rate.num / (double)decoder.VideoStream.r_frame_rate.den;
            baseFrameDurationMs = 1000.0 / fps;
            baseFrameDurationSeconds = baseFrameDurationMs / 1000.0;
            TimeSpan frameDuration = TimeSpan.FromMilliseconds(baseFrameDurationMs);

            int frameIndex = 0;
            isFrameEnded = false;
            bool resumed = false;

            DateTime playbackStartTime = DateTime.Now;


            Queue<double> boostHistory = new();
            const int smoothingWindow = 15;

            audioPlayer.Start();

            while (isPlaying)
            {
                // バッファ不足チェック
                if (!isBuffering && frames.Count < frameCap / 4 && !isFrameEnded)
                {
                    isBuffering = true;
                    Console.WriteLine("[一時停止] バッファ不足により自動停止");
                    audioPlayer?.Pause();
                }

                if (isBuffering && frames.Count >= frameCap / 1.2)
                {
                    isBuffering = false;
                    Console.WriteLine("[再開] バッファ回復により自動再開");
                    audioPlayer?.Resume();
                }

                if (isPaused || isBuffering)
                {
                    resumed = false;
                    await Task.Delay(isPaused ? 150 : 1000);
                    continue;
                }

                if (!resumed)
                {
                    var position = TimeSpan.FromSeconds((double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond);
                    var (frameNumber, timeInFrame) = GetCurrentFrameInfo(fps, position);
                    int skipCount = frameNumber - frameIndex;

                    Console.WriteLine($"[再開] フレーム: {frameNumber}, フレーム内経過: {timeInFrame.TotalMilliseconds:F2}ms");

                    for (int i = 0; i < skipCount && frames.TryDequeue(out var skippedFrame); i++, frameIndex++)
                    {
                        skippedFrame.Dispose();
                    }

                    resumed = true;
                    audioPlayer?.Resume();
                    playbackStartTime = DateTime.Now - position;
                }

                // フレーム描画
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

                    Console.WriteLine("映像のバッファが足りません");
                    await Task.Delay(1000);
                    continue;
                }

                // フレーム溢れ対処
                while (frames.Count > frameCap && frames.TryDequeue(out var oldFrame))
                {
                    oldFrame.Dispose();
                    frameIndex++;
                }

                // 音声との同期補正
                var audioPos = TimeSpan.FromSeconds((double)audioPlayer.GetPosition() / audioPlayer.AverageBytesPerSecond);
                var (idealFrameIndex, timeInFrame2) = GetCurrentFrameInfo(fps, audioPos);

                int frameDiff = idealFrameIndex - frameIndex;

                if (frameIndex > 0)
                {
                    double rawBoost = (timeInFrame2.TotalSeconds + frameDiff * baseFrameDurationSeconds) / baseFrameDurationSeconds;
                    double boostFactor = Math.Clamp(rawBoost, 0.7, 20.0);

                    if (frameDiff >= 3)
                    {
                        int skipCount = frameDiff >= 10 ? frameDiff :
                                        frameDiff >= 5 ? Math.Min(5, frameDiff) :
                                        Math.Min(2, frameDiff);

                        Console.WriteLine($"[同期補正] frameIndex: {frameIndex} → {idealFrameIndex}（差: {frameDiff}） → Skipping {skipCount} frames");

                        for (int i = 0; i < skipCount && frames.TryDequeue(out var skippedFrame); i++)
                        {
                            skippedFrame.Dispose();
                            frameIndex++;
                        }

                        Console.WriteLine($"[補正完了] Skipped {skipCount} frames");

                        frameDiff = idealFrameIndex - frameIndex; // skip 後に再評価
                        rawBoost = (timeInFrame2.TotalSeconds + frameDiff * baseFrameDurationSeconds) / baseFrameDurationSeconds;
                        boostFactor = Math.Clamp(rawBoost, 0.1, 20.0);
                    }
                    else if (frameDiff < 0)
                    {
                        // 映像が音声より先に進んでいる → 再生を意図的に遅らせる
                        double delayMs = (-frameDiff * baseFrameDurationMs - timeInFrame2.TotalMilliseconds)*1.1;
                        frameDuration = TimeSpan.FromMilliseconds(baseFrameDurationMs + delayMs);

                        Console.WriteLine($"[同期調整: 先行] frame: {frameIndex}, decoded: {decodedFrames}, frameDiff: {frameDiff}, delay追加: {delayMs:F2}ms → 合計: {frameDuration.TotalMilliseconds:F2}ms");
                    }
                    else if(frameDiff > 0)
                    {
                        double d =  (timeInFrame2.TotalSeconds + frameDiff * baseFrameDurationSeconds) * 900;
                        double delayMs = baseFrameDurationMs - timeInFrame2.TotalSeconds - d;
                        frameDuration = TimeSpan.FromMilliseconds(Math.Max(delayMs, baseFrameDurationMs / (frameDiff + 2)));

                        //Console.WriteLine($"[再生] frame: {frameIndex}, decoded: {decodedFrames}, frameDiff: {frameDiff}, delayMs: {d:F3}, frameDuration: {frameDuration.TotalMilliseconds:F2}ms");
                    }
                    else
                    {
                        double d = timeInFrame2.TotalSeconds + frameDiff * baseFrameDurationSeconds;
                        double delayMs = baseFrameDurationMs - timeInFrame2.TotalSeconds - d;
                        frameDuration = TimeSpan.FromMilliseconds(Math.Max(delayMs, baseFrameDurationMs / (frameDiff + 2)));

                        //Console.WriteLine($"[再生] frame: {frameIndex}, decoded: {decodedFrames}, frameDiff: {frameDiff}, delayMs: {d:F3}, frameDuration: {frameDuration.TotalMilliseconds:F2}ms");
                    }
                }

                await Task.Delay(frameDuration);
            }

        }




        public static (int frameNumber, TimeSpan timeInFrame) GetCurrentFrameInfo(double fps, TimeSpan playbackTime)
        {
            double totalSeconds = playbackTime.TotalSeconds;
            int frameNumber = (int)(totalSeconds * fps);
            double frameStartTime = frameNumber / fps;
            double timeInFrame = totalSeconds - frameStartTime;
            return (frameNumber, TimeSpan.FromSeconds(timeInFrame));
        }


        private async Task WaitForBuffer()
        {
            while (frames.Count < frameCap && isPlaying)
            {
                await Task.Delay(waitTime);
            }
        }
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
                //Console.WriteLine($"{frame.Width}x{frame.Height}, Format: {frame.PixelFormat}, Estimated: {frame.EstimatedSizeInBytes / (1024 * 1024)} MB");
                sw.Stop();

                switch (result)
                {
                    case FrameReadResult.FrameAvailable:
                        // ⚠️ Stop() が走って再生中でない場合 → Dispose してスキップ
                        if (!isPlaying)
                        {
                            frame.Dispose(); // ← ここが重要！！！
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
                        Console.WriteLine("[終了] ストリームの終端です");
                        isFrameEnded = true;
                        return;
                }

                if (frames.Count > frameCap * 0.8)
                {
                    await Task.Delay(1);
                }
            }
        }


        private async Task ReadAudioFrames()
        {
            while (isPlaying)
            {
                if (isPaused)
                {
                    await Task.Delay(100); // 一時停止中は待機
                    continue;
                }

                var audioFrame = decoder.ReadAudioFrame();
                if (audioFrame is null)
                    break;

                using (var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame))
                {
                    byte[] data = audioData.AsMemory().ToArray();

                    // バッファがいっぱいの場合は少し待機
                    while (audioPlayer.BufferedDuration.TotalSeconds >= 10)
                    {
                        //Debug.WriteLine("バッファが一杯なので音声供給を待機しています...");
                        await Task.Delay(100);
                    }

                    audioPlayer.AddAudioData(data);
                }

                audioFrame.Dispose();
            }
        }

    }
}
