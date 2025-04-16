using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using FFmpeg.AutoGen;
using NAudio.Wave;
using System.Runtime.Intrinsics.X86;
using System.Drawing.Imaging;
using System.Windows.Controls;
using System.Threading.Channels;
using System.Diagnostics.Eventing.Reader;
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
        public bool IsPlaying => playbackState == PlaybackState.Playing || playbackState == PlaybackState.EndedStream;
        public bool IsPaused => playbackState == PlaybackState.Paused;
        public bool IsSeeking => playbackState == PlaybackState.Seeking;
        public bool IsBuffering => playbackState == PlaybackState.Buffering;
        public bool IsEnded => playbackState == PlaybackState.Ended;

        private MemoryStream audioStream = new MemoryStream();
        private readonly object audioLock = new object();

        private Decoder decoder;
        private ImageWriter imageWriter;
        private FrameConveter frameConveter;

        private const int frameCap = 100;
        private const int waitTime = 150;

        private uint decodedFrames = 0;
        private AVRational rawFps;
        private AVRational videoFps;
        private double fps = 0;
        private double baseFrameDurationMs = 0;

        private ConcurrentQueue<ManagedFrame> frames = new ConcurrentQueue<ManagedFrame>();
        private Task playTask;

        private AudioPlayer audioPlayer;
        private int frameIndex = 0;
        public int FrameIndex => frameIndex;

        private static readonly SemaphoreSlim decoderLock = new(1, 1);
        private readonly SemaphoreSlim seekLock = new SemaphoreSlim(1, 1);

        private double durationMs = 0;

        /// <summary>
        /// ファイルを開いて FFmpeg デコーダーを初期化
        /// </summary>
        public void OpenFile(string path)
        {
            decoder = new Decoder();
            durationMs = decoder.OpenFile(path);
            decoder.InitializeDecoders(false);
            playbackState = PlaybackState.Stopped;
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

            managedFrame.GetCpuFrame();

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
            if (playbackState != PlaybackState.Stopped)
            {
                playbackState = PlaybackState.Playing;
                //audioPlayer?.Resume();
            }
            else
            {
                playbackState = PlaybackState.Playing;
                audioPlayer = new AudioPlayer();

                var waveFormat = new WaveFormat(decoder.AudioCodecContext.sample_rate, 16, decoder.AudioCodecContext.ch_layout.nb_channels);
                audioPlayer.Init(waveFormat, volume: 0.5f, latencyMs: 200);

                playTask = PlayInternal();
            }

            if (playTask != null)
                await playTask;
        }

        /// <summary>
        /// 一時停止を行う
        /// </summary>
        public void Pause()
        {
            if (playbackState.Equals(PlaybackState.Playing))
            {
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


        public async Task<bool> SeekToExactFrameAsync(long targetFrameIndex)
        {
            if (!await seekLock.WaitAsync(0)) // すでにシーク中ならスキップ
            {
                Console.WriteLine("[シーク] すでにシーク中です。重複実行は無視されました。");
                return false;
            }

            try
            {
                unsafe
                {
                    if (decoder == null || fps <= 0.0 || decoder.VideoStreamPointer == null)
                    {
                        Console.WriteLine("[エラー] シーク前提条件が不十分です。");
                        return false;
                    }
                }

                audioPlayer.Pause(true);
                playbackState = PlaybackState.Seeking;
                targetFrameIndex = (targetFrameIndex <= 0) ? 1 : targetFrameIndex ;

                var videoStream = decoder.VideoStream;
                AVRational videoTimeBase = videoStream.time_base;

                long target = ffmpeg.av_rescale_q(targetFrameIndex, rawFps, videoTimeBase);
                long targetPts = ffmpeg.av_rescale_q(
    targetFrameIndex -1,
    new AVRational { num = rawFps.den, den = rawFps.num }, // → 秒単位の time_base
    videoTimeBase
);

                // ==== シーク処理 ====
                await decoderLock.WaitAsync();
                try
                {
                    unsafe
                    {
                        int result = ffmpeg.av_seek_frame(
                            decoder.FormatContextPointer,
                            videoStream.index,
                            targetPts,
                            ffmpeg.AVSEEK_FLAG_BACKWARD
                        );

                        if (result < 0)
                        {
                            Console.WriteLine($"[エラー] 映像PTS {targetPts} フレーム{targetFrameIndex} へのシークに失敗しました");
                            playbackState = PlaybackState.Paused;
                            return false;
                        }

                        ffmpeg.avcodec_flush_buffers(decoder.VideoCodecContextPointer);
                        if (decoder.AudioCodecContextPointer != null)
                            ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);
                    }
                }
                finally
                {
                    decoderLock.Release();
                }

                // ==== 映像バッファをクリア ====
                int disposedFrames = 0;
                while (frames.TryDequeue(out var oldFrame))
                {
                    oldFrame.Dispose();
                    disposedFrames++;
                }
                Console.WriteLine($"[バッファ破棄] {disposedFrames} フレームを破棄");

                // ==== フレーム読み飛ばし ====
                const int maxSkip = 1000;
                int skipped = 0;
                ManagedFrame matchedFrame = null;

                while (skipped++ < maxSkip)
                {
                    ManagedFrame frame = null;
                    FrameReadResult readResult = FrameReadResult.FrameNotReady;

                    for (int i = 0; i < 30; i++)
                    {
                        await decoderLock.WaitAsync();
                        try
                        {
                            (readResult, frame) = decoder.TryReadFrame();
                        }
                        finally
                        {
                            decoderLock.Release();
                        }

                        if (readResult == FrameReadResult.FrameAvailable || readResult == FrameReadResult.EndOfStream)
                            break;

                        await Task.Delay(10);
                    }

                    if (readResult == FrameReadResult.EndOfStream)
                    {
                        Console.WriteLine($"[シーク失敗] ストリーム終端に達しました（{skipped}/{targetFrameIndex}）");
                        playbackState = PlaybackState.Paused;
                        return false;
                    }

                    if (readResult == FrameReadResult.FrameAvailable)
                    {
                        var ptsFrameIndex = GetRawFrameIndex(frame);
                        if (ptsFrameIndex == null)
                        {
                            frame.Dispose();
                            continue;
                        }

                        if (ptsFrameIndex.Value != targetFrameIndex -1)
                        {
                            Console.WriteLine($"[読み飛ばし] {ptsFrameIndex.Value}");
                            frame.Dispose();
                            continue;
                        }

                        matchedFrame = frame;
                        break;
                    }
                }

                if (matchedFrame == null)
                {
                    Console.WriteLine("[シーク失敗] 指定されたフレームに到達できませんでした");
                    playbackState = PlaybackState.Paused;
                    return false;
                }

                frames.Enqueue(matchedFrame);
                frameIndex = (int)GetFrameIndex(matchedFrame);

                await transferLimiter.WaitAsync();
                try
                {
                    unsafe { matchedFrame.GetCpuFrame(); }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[転送失敗] {ex.Message}");
                }
                finally
                {
                    transferLimiter.Release();
                }

                imageWriter.WriteFrame(matchedFrame, frameConveter);
                Console.WriteLine($"[シーク完了] 実フレーム: {frameIndex}");

                await decoderLock.WaitAsync();
                try
                {
                    unsafe
                    {
                        // ==== 音声シーク ====
                        if (decoder.AudioStreamPointer != null)
                        {
                            var audioStream = decoder.AudioStream;
                            AVRational audioTimeBase = audioStream.time_base;
                            //long audioPts2 = (long)(((double)targetFrameIndex * (double)audioTimeBase.den) / (((double)audioTimeBase.num) * (double)fps));
                            long audioPts = ffmpeg.av_rescale_q(
    frameIndex,
    new AVRational { num = videoFps.den, den = videoFps.num },
    audioTimeBase
);

                            int audioResult = ffmpeg.av_seek_frame(
                                decoder.FormatContextPointer,
                                audioStream.index,
                                audioPts,
                                ffmpeg.AVSEEK_FLAG_BACKWARD
                            );

                            if (audioResult >= 0)
                            {
                                ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);
                            }
                            else
                            {
                                Console.WriteLine($"[警告] 音声シーク失敗 (PTS: {audioPts})");
                            }
                            long targetAudioPts = audioPts;
                            skipped = 0;

                            while (skipped++ < maxSkip)
                            {
                                ManagedFrame audioFrame = null;
                                FrameReadResult readResult = FrameReadResult.FrameNotReady;

                                for (int i = 0; i < 30; i++)
                                {
                                    (readResult, audioFrame) = decoder.TryReadAudioFrame();

                                    if (readResult == FrameReadResult.FrameAvailable || readResult == FrameReadResult.EndOfStream)
                                        break;

                                    Task.Delay(10).Wait();
                                }

                                if (readResult == FrameReadResult.EndOfStream)
                                {
                                    Console.WriteLine($"[シーク失敗] ストリーム終端に達しました（{skipped}/{targetFrameIndex}）");
                                    playbackState = PlaybackState.Paused;
                                    return false;
                                }

                                if (readResult == FrameReadResult.FrameAvailable)
                                {
                                    if (audioFrame.Frame->pts + (audioTimeBase.den / (double)audioTimeBase.num) >= targetAudioPts)
                                    {
                                        audioPlayer.ResetBuffer(); // バッファリセット

                                        using var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                                        byte[] data = audioData.AsMemory().ToArray();

                                        long ptsDiff = targetAudioPts - audioFrame.Frame->pts;
                                        int skipBytes = 0;

                                        if (ptsDiff > 0)
                                        {
                                            // スキップバイト数 = (PTS差 × 秒単位変換 × サンプルレート × チャンネル × サイズ)
                                            skipBytes = (int)Math.Round(
                                                (ptsDiff * audioTimeBase.num *
                                                audioData.SampleRate *
                                                audioData.Channel *
                                                audioData.SizeOf)
                                                 / (double)audioTimeBase.den
                                            );

                                            if (skipBytes >= audioData.TotalSize)
                                            {
                                                // 完全にスキップされるなら不要
                                                audioFrame.Dispose();
                                                continue;
                                            }

                                            ReadOnlySpan<byte> sliced = new Span<byte>((void*)audioData.Data, audioData.TotalSize).Slice(skipBytes);
                                            audioPlayer.AddAudioData(sliced);
                                        }
                                        else
                                        {
                                            ReadOnlySpan<byte> full = new Span<byte>((void*)audioData.Data, audioData.TotalSize);
                                            audioPlayer.AddAudioData(full);
                                        }

                                        // 絶対再生位置（先頭からのバイト）をセット
                                        /*
                                        double ptsSeconds = audioFrame.Frame->pts * audioTimeBase.num / (double)audioTimeBase.den;
                                        long basePositionBytes = (long)(ptsSeconds * audioPlayer.AverageBytesPerSecond);

                                        long absoluteBytes = basePositionBytes + skipBytes;

                                        audioPlayer.SetAbsolutePosition(absoluteBytes);*/
                                        long ptsMicros = ffmpeg.av_rescale_q(audioFrame.Frame->pts, audioTimeBase, new AVRational { num = 1, den = 1000000 });
                                        TimeSpan ptsTime = TimeSpan.FromMilliseconds(ptsMicros / 1000.0);
                                        long basePositionBytes = (long)(ptsTime.TotalSeconds * audioPlayer.AverageBytesPerSecond);
                                        long absoluteBytes = basePositionBytes + skipBytes;
                                        audioPlayer.SetAbsolutePosition(absoluteBytes);




                                        Console.WriteLine($"[Audioシーク] PTS {audioFrame.Frame->pts} / {audioPts} （skip: {skipBytes}）に成功");

                                        audioFrame.Dispose();
                                        break;
                                    }
                                    audioFrame.Dispose();
                                    continue;
                                }
                                audioFrame.Dispose();

                            }


                        }

                    }
                }
                finally
                {
                    decoderLock.Release();
                }


                playbackState = PlaybackState.SeekBuffering;

                // 再開準備
                await WaitForBuffer();


                //(frameIndex, _) = GetCurrentFrameInfo(fps, TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond));
                Console.WriteLine($"[シーク完了] フレーム {frameIndex}/{GetCurrentFrameInfo(fps, TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond))} に正常移動（frames.Count={frames.Count}）");
                Console.WriteLine($"Audio : {TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond)}, Movie : {TimeSpan.FromSeconds(frameIndex / fps)}");
                playbackState = PlaybackState.Paused;
            }
            finally
            {
                seekLock.Release(); // 最後にロック解除
            }
            return true;

        }



        public long GetTotalFrameCount()
        {
            return (long)(fps * (durationMs / 1000.0));
        }


        /// <summary>
        /// 映像・音声の再生を内部的に実行するメインループ
        /// </summary>
        private async Task PlayInternal()
        {
            _ = Task.Run(() => ReadFrames());
            _ = Task.Run(() => ReadAudioFrames());

            await WaitForBuffer();

            rawFps = decoder.VideoStream.avg_frame_rate;
            if (rawFps.num == 1000 && rawFps.den == 33)
            {
                videoFps.num = 30000;
                videoFps.den = 1001;

                fps = 29.97;
            }
            else
            { 
                videoFps = rawFps;
                fps = (double)videoFps.num / (double)videoFps.den;
                fps = (double)videoFps.num / (double)videoFps.den;
            }
            baseFrameDurationMs = 1000.0 / fps;
            TimeSpan frameDuration = TimeSpan.FromMilliseconds(baseFrameDurationMs);

            playbackState = PlaybackState.Playing;
            bool resumed = false;
            DateTime playbackStartTime = DateTime.Now;
            Queue<double> boostHistory = new();

            audioPlayer.Start();

            var stopwatch = new Stopwatch();

            while (playbackState != PlaybackState.Stopped && playbackState != PlaybackState.Ended)
            {
                // バッファ不足 → 自動一時停止
                if (playbackState == PlaybackState.Playing && frames.Count < frameCap / 4)
                {
                    playbackState = PlaybackState.Buffering;
                    audioPlayer?.Pause();
                }

                // バッファ回復 → 自動再開
                if (playbackState == PlaybackState.Buffering && frames.Count >= frameCap / 1.2)
                {
                    playbackState = PlaybackState.Playing;
                    //audioPlayer?.Resume();
                }

                // 一時停止・バッファ中などで待機
                if (playbackState == PlaybackState.Paused || playbackState == PlaybackState.Buffering || playbackState == PlaybackState.SeekBuffering)
                {
                    resumed = false;
                    await Task.Delay(playbackState == PlaybackState.Paused ? 150 : 100);
                    continue;
                }

                if (playbackState == PlaybackState.Seeking)
                {
                    await Task.Delay(100);
                    continue;
                }

                // 再開直後は音声位置に合わせて同期
                if (!resumed)
                {
                    var position = TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond);
                    var (frameNumber, _) = GetCurrentFrameInfo(fps, position);

                    int skipCount = frameNumber - frameIndex;

                    for (int i = 0; i < skipCount && frames.TryDequeue(out var skipped); i++, frameIndex++)
                        skipped.Dispose();

                    resumed = true;
                    audioPlayer?.Resume();
                }

                if (frames.TryDequeue(out var frame))
                {
                    unsafe
                    {
                        if (frame.Frame == null || frame.Frame->buf[0] == null)
                        {
                            Console.WriteLine("[警告] デコードされたフレームにバッファが存在しません。破棄します");
                            frame.Dispose();
                            continue;
                        }
                    }


                    if (frame.IsGpuFrame)
                    {
                        Console.WriteLine("[描画スキップ] GPU未転送フレームをスキップしました");
                        frame.Dispose();
                        continue;
                    }

                    stopwatch.Restart();

                    imageWriter.WriteFrame(frame, frameConveter);

                    long? ptsFrameIndex = GetFrameIndex(frame);
                    if (ptsFrameIndex != null)
                    {
                        //Console.WriteLine($"[描画] フレーム番号: {frameIndex}, {ptsFrameIndex}");
                        frameIndex = (int)ptsFrameIndex + 1;
                    }
                    else
                    {
                        frameIndex++;
                    }

                    frame.Dispose();
                    stopwatch.Stop();
                }
                else
                {
                    if (playbackState == PlaybackState.Ended)
                    {
                        Stop();
                        return;
                    }
                    playbackState = PlaybackState.Paused;
                    await Task.Delay(30);
                    continue;
                }

                // フレームバッファの整理
                while (frames.Count > frameCap && frames.TryDequeue(out var old))
                {
                    old.Dispose();
                    frameIndex++;
                }

                // ==== 音声との同期補正 ====
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

                        frameDiff = idealFrameIndex - frameIndex;
                        offsetMs = timeInFrame2.TotalMilliseconds + frameDiff * baseFrameDurationMs;
                    }
                    else if (frameDiff < 0)
                    {
                        double delayMs = (-offsetMs) * 1.3;
                        adjustedDelayMs = baseFrameDurationMs + delayMs;

                        Console.WriteLine($"[待機] 想定: {idealFrameIndex}, 実際: {frameIndex}, 差: {frameDiff}, 遅延: {delayMs:F2}ms");
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

                // 描画処理にかかった時間を除いて残り待機
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
        /// フレームのPTSからフレームインデックスを計算
        /// </summary>
        public unsafe long? GetFrameIndex(ManagedFrame frame)
        {
            if (frame != null)
            {
                if (frame.Frame->pts == ffmpeg.AV_NOPTS_VALUE)
                    return null;

                AVRational timeBase = decoder.VideoStream.time_base;

                long frameIndex = ffmpeg.av_rescale_q(
                    frame.Frame->pts,
                    timeBase,
                    new AVRational { num = videoFps.den, den = videoFps.num } // ← 秒単位に変換 → フレームに換算
                );

                return frameIndex;
            }
            return 0;
        }

        public unsafe long? GetRawFrameIndex(ManagedFrame frame)
        {
            if (frame != null)
            {
                if (frame.Frame->pts == ffmpeg.AV_NOPTS_VALUE)
                    return null;

                AVRational timeBase = decoder.VideoStream.time_base;
                AVRational frameRate = rawFps;

                long frameIndex = ffmpeg.av_rescale_q(
                    frame.Frame->pts,
                    timeBase,
                    new AVRational { num = frameRate.den, den = frameRate.num } // ← 秒単位に変換 → フレームに換算
                );

                return frameIndex;
            }
            return 0;
        }

        /// <summary>
        /// フレームバッファが満たされるまで待機
        /// </summary>
        private async Task WaitForBuffer()
        {
            while (frames.Count < frameCap && (playbackState == PlaybackState.Playing || playbackState == PlaybackState.Paused || playbackState == PlaybackState.SeekBuffering))
            {
                await Task.Delay(waitTime);
            }
        }


        private static readonly SemaphoreSlim transferLimiter = new(4); // 同時転送制限

        /// <summary>
        /// 非同期に映像フレームを読み込み、GPU → CPU 転送を制御しつつキューに格納する。
        /// </summary>
        private async Task ReadFrames()
        {
            int transferredCount = 0;
            int totalFramesToTransfer = 0;
            const int preTransferThreshold = 10;

            while (playbackState != PlaybackState.Stopped && playbackState != PlaybackState.Ended)
            {
                // 一時停止中などの場合は一時待機
                if (playbackState == PlaybackState.Paused || playbackState == PlaybackState.Seeking)
                {
                    await Task.Delay(100);
                    continue;
                }

                if (frames.Count >= frameCap)
                {
                    await Task.Delay(1);
                    continue;
                }

                FrameReadResult result;
                ManagedFrame frame = null;
                var sw = Stopwatch.StartNew();

                // ==== FFmpeg に対する読み取りを排他制御 ====
                await decoderLock.WaitAsync();
                try
                {
                    (result, frame) = decoder.TryReadFrame();
                    //Console.WriteLine($"Get New Frame {GetFrameIndex(frame)}");
                }
                finally
                {
                    decoderLock.Release();
                    sw.Stop();
                }

                if (result == FrameReadResult.FrameAvailable)
                {
                    long? a = GetFrameIndex(frame);
                    if (playbackState == PlaybackState.Stopped)
                    {
                        frame.Dispose();
                        return;
                    }
                    if (((0 <= a) && a <= frameIndex))
                    {
                        frame.Dispose();
                        continue;
                    }


                    frames.Enqueue(frame);
                    decodedFrames++;
                    Interlocked.Increment(ref totalFramesToTransfer);

                    if (sw.ElapsedMilliseconds > baseFrameDurationMs && fps != 0.0)
                    {
                        Console.WriteLine($"[警告] フレームデコードに {sw.ElapsedMilliseconds}ms (>1/{fps}) かかりました");
                    }
                }
                else if (result == FrameReadResult.FrameNotReady)
                {
                    await Task.Delay(1);
                }

                if (result == FrameReadResult.EndOfStream && !playbackState.Equals(PlaybackState.EndedStream))
                {
                    Console.WriteLine("[EOF] 映像ストリームの終端を検出しました");
                    playbackState = (playbackState != PlaybackState.SeekBuffering) ? PlaybackState.EndedStream : playbackState;
                }

                // ==== 転送処理 ====
                int threshold = Math.Min(preTransferThreshold, frames.Count);

                foreach (var f in frames.Take(threshold))
                {
                    if (!f.IsGpuFrame) continue;

                    if (frames.Count > preTransferThreshold)
                    {
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

                if (frames.Count < preTransferThreshold &&
                    playbackState == PlaybackState.EndedStream &&
                    Volatile.Read(ref transferredCount) >= Volatile.Read(ref totalFramesToTransfer))
                {
                    Console.WriteLine("映像フレームの終端（すべて転送済み）");
                    // playbackState = PlaybackState.Ended;
                    // return;
                    continue;
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
            const int maxRetry = 100;
            const int retryDelayMs = 10;
            int retryCount = 0;

            while (playbackState != PlaybackState.Stopped && playbackState != PlaybackState.Ended)
            {
                // 一時停止・バッファ待機・シーク中など
                if (playbackState == PlaybackState.Paused || playbackState == PlaybackState.Buffering || playbackState == PlaybackState.Seeking)
                {
                    await Task.Delay(100);
                    continue;
                }

                // バッファが十分な場合は待機（10秒分）
                if (audioPlayer.BufferedDuration.TotalSeconds >= 10)
                {
                    await Task.Delay(100);
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

                switch (result)
                {
                    case FrameReadResult.FrameAvailable:
                        unsafe
                        {
                            if (audioFrame.Frame == null || audioFrame.Frame->nb_samples <= 0)
                            {
                                Console.WriteLine("[Audio] 無効なフレームが読み込まれました（nb_samples <= 0）");
                                audioFrame.Dispose();
                                return;
                            }
                        }

                        using (var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame))
                        {
                            byte[] data = audioData.AsMemory().ToArray();
                            audioPlayer.AddAudioData(data);
                        }
                        audioFrame.Dispose();
                        retryCount = 0; // 成功したのでリセット
                        break;

                    case FrameReadResult.FrameNotReady:
                        await Task.Delay(retryDelayMs);
                        retryCount++;
                        if (retryCount > maxRetry)
                        {
                            Console.WriteLine("[Audio] フレーム取得リトライ上限に達しました。");
                            return;
                        }
                        break;

                    case FrameReadResult.EndOfStream:
                        Console.WriteLine("[Audio] End of stream reached.");
                        await Task.Delay(100);
                        break;
                }
            }
        }



    }
}
