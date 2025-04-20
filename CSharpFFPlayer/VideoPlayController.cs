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

        //private ConcurrentQueue<ManagedFrame> frames = new ConcurrentQueue<ManagedFrame>();
        private Task playTask;

        private AudioPlayer audioPlayer;
        private int frameIndex = 1;
        public int FrameIndex => frameIndex;

        private static readonly SemaphoreSlim decoderLock = new(1, 1);
        private readonly SemaphoreSlim seekLock = new SemaphoreSlim(1, 1);

        private VideoInfo videoInfo;
        public VideoInfo VideoInfo => videoInfo;

        /// <summary>
        /// ファイルを開いて FFmpeg デコーダーを初期化
        /// </summary>
        public void OpenFile(string path)
        {
            decoder = new Decoder();
            videoInfo = decoder.OpenFile(path);
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
            videoInfo.VideoStreams.FirstOrDefault().Fps = fps;
            baseFrameDurationMs = 1000.0 / fps;
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

        public async Task<bool> StepForwardOneFrame()
        {

            if (decoder == null || imageWriter == null || frameConveter == null)
                return false;

            // 再生中なら一時停止
            if (playbackState != PlaybackState.Paused)
            {
                playbackState = PlaybackState.Paused;
                audioPlayer?.Pause();
            }

            // 古いフレームを破棄（frameIndex より前のものを削除）
            lock (frames)
            {
                var oldKeys = frames.Keys
                    .TakeWhile(k => k < frameIndex)
                    .ToList(); // 明示的にリスト化（列挙中の削除エラー回避）

                foreach (var key in oldKeys)
                {
                    frames[key].Dispose();
                    frames.Remove(key);
                    Console.WriteLine($"[SkipOldFrame] 古いフレーム {key} を破棄");
                }
            }


            // =======================
            // フレーム探索（バッファのみ）
            // =======================
            ManagedFrame? frame = null;
            bool found = false;

            lock (frames)
            {
                // 古いフレームの削除
                var oldKeys = frames.Keys
                    .TakeWhile(k => k < frameIndex)
                    .ToList();

                foreach (var key in oldKeys)
                {
                    frames[key].Dispose();
                    frames.Remove(key);
                    Console.WriteLine($"[SkipOldFrame] 古いフレーム {key} を破棄");
                }

                // 対象フレームが存在するか確認
                if (frames.TryGetValue(frameIndex, out frame))
                {
                    frames.Remove(frameIndex);
                    found = true;
                }
            }

            if (!found)
            {
                Console.WriteLine($"[Info] バッファに目的のフレーム{frameIndex}がないため即シークします");
                return await SeekToExactFrameAsync(frameIndex);
            }

            // GPU フレームなら CPU へ転送
            unsafe
            {
                if (frame.Frame == null || frame.Frame->buf[0] == null)
                {
                    frame.Dispose();
                    return false;
                }

                if (frame.IsGpuFrame)
                    frame.GetCpuFrame();
            }

            // 描画処理
            imageWriter.WriteFrame(frame, frameConveter);
            Console.WriteLine($"コマ送り : {frameIndex}/{frames.Count}");
            unsafe
            {
                Console.WriteLine($"コマ送り : {frameIndex}/{frames.Count} : {frame.Frame->pts}");
            }
            frame.Dispose();

            await WaitForBuffer();

            // ==== 音声補正 ====
            await decoderLock.WaitAsync();
            try
            {
                unsafe
                {
                    if (decoder.AudioStreamPointer != null)
                    {
                        var audioStream = decoder.AudioStream;
                        AVRational audioTimeBase = audioStream.time_base;
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

                        // 音声読み出し
                        const int maxSkip = 1000;
                        int skipped = 0;

                        while (skipped++ < maxSkip)
                        {
                            var (readResult, audioFrame) = decoder.TryReadAudioFrame();

                            if (readResult == FrameReadResult.EndOfStream)
                            {
                                Console.WriteLine($"[Audioシーク失敗] ストリーム終端に到達");
                                return false;
                            }

                            if (readResult != FrameReadResult.FrameAvailable || audioFrame.Frame == null)
                            {
                                audioFrame?.Dispose();
                                Task.Delay(5);
                                continue;
                            }

                            if (audioFrame.Frame->pts + (audioTimeBase.den / (double)audioTimeBase.num) >= audioPts)
                            {
                                audioPlayer.ResetBuffer(); // バッファリセット

                                using var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                                byte[] data = audioData.AsMemory().ToArray();

                                long ptsDiff = audioPts - audioFrame.Frame->pts;
                                int skipBytes = 0;

                                if (ptsDiff > 0)
                                {
                                    skipBytes = (int)Math.Round(
                                        (ptsDiff * audioTimeBase.num *
                                         audioData.SampleRate *
                                         audioData.Channel *
                                         audioData.SizeOf)
                                        / (double)audioTimeBase.den
                                    );

                                    if (skipBytes >= audioData.TotalSize)
                                    {
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
                        }
                    }
                }
            }
            finally
            {
                decoderLock.Release();
            }

            frameIndex++;
            return true;
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

            // すべてのフレームを破棄
            lock (frames)
            {
                foreach (var pair in frames)
                {
                    pair.Value.Dispose();
                }
                frames.Clear();
            }

            Console.WriteLine("再生を終了しました");

            audioPlayer?.Dispose();
            decoder?.Dispose();
            frameConveter?.Dispose();
        }


        /*
        public async Task<bool> SeekToExactFrameAsync(long targetFrameIndex)
        {
            if (!await seekLock.WaitAsync(0))
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
                targetFrameIndex = Math.Max(1, targetFrameIndex);

                var videoStream = decoder.VideoStream;
                AVRational videoTimeBase = videoStream.time_base;

                long targetPts = ffmpeg.av_rescale_q(
                    targetFrameIndex -1,
                    new AVRational { num = rawFps.den, den = rawFps.num },
                    videoTimeBase
                );

                // ==== 映像シーク ====
                await decoderLock.WaitAsync();
                try
                {
                    ManagedFrame? matchedFrame = null;

                    lock (frames)
                    {
                        if (frames.TryGetValue(targetFrameIndex, out matchedFrame))
                        {
                            frames.Remove(targetFrameIndex);
                            frameIndex = (int)targetFrameIndex;
                        }
                    }

                    if (matchedFrame != null)
                    {
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
                        matchedFrame.Dispose();

                        Console.WriteLine($"[バッファ内] フレーム {frameIndex} を使用（シーク不要）");

                        // 音声シークもここで行うと完全な同期になる

                        playbackState = PlaybackState.Paused;
                        return true;
                    }

                }
                finally
                {
                    decoderLock.Release(); // WaitForBuffer前に開放
                }

                playbackState = PlaybackState.SeekBuffering;
                await WaitForBuffer();
                // ==== フレームバッファの到着を待機 ====
                bool matched = false;
                const int timeoutMs = 3000;
                var start = DateTime.Now;

                while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
                {
                    ManagedFrame? matchedFrame = null;

                    lock (frames)
                    {
                        if (frames.TryGetValue(targetFrameIndex, out matchedFrame))
                        {
                            frames.Remove(targetFrameIndex);
                            frameIndex = (int)targetFrameIndex;
                        }
                    }

                    if (matchedFrame != null)
                    {
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
                        matchedFrame.Dispose();

                        matched = true;
                        break;
                    }


                    await Task.Delay(10);
                }

                if (!matched)
                {
                    Console.WriteLine($"[シーク失敗] 指定されたフレーム{targetFrameIndex}に到達できませんでした");
                    playbackState = PlaybackState.Paused;
                    return false;
                }

                // ==== 音声シーク ====
                await decoderLock.WaitAsync();
                try
                {
                    unsafe
                    {
                        if (decoder.AudioStreamPointer != null)
                        {
                            var audioStream = decoder.AudioStream;
                            AVRational audioTimeBase = audioStream.time_base;

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
                                ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);
                            else
                                Console.WriteLine($"[警告] 音声シーク失敗 (PTS: {audioPts})");

                            // 音声フレーム到達待機・再構築
                            const int maxSkip = 1000;
                            int skipped = 0;

                            while (skipped++ < maxSkip)
                            {
                                var (readResult, audioFrame) = decoder.TryReadAudioFrame();

                                if (readResult == FrameReadResult.EndOfStream)
                                {
                                    Console.WriteLine("[Audio] ストリーム終端に到達");
                                    return false;
                                }

                                if (readResult != FrameReadResult.FrameAvailable || audioFrame.Frame == null)
                                {
                                    audioFrame?.Dispose();
                                    Task.Delay(5).Wait();
                                    continue;
                                }

                                if (audioFrame.Frame->pts + (audioTimeBase.den / (double)audioTimeBase.num) >= audioPts)
                                {
                                    audioPlayer.ResetBuffer();

                                    using var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                                    byte[] data = audioData.AsMemory().ToArray();

                                    long ptsDiff = audioPts - audioFrame.Frame->pts;
                                    int skipBytes = 0;

                                    if (ptsDiff > 0)
                                    {
                                        skipBytes = (int)Math.Round(
                                            (ptsDiff * audioTimeBase.num *
                                             audioData.SampleRate *
                                             audioData.Channel *
                                             audioData.SizeOf)
                                            / (double)audioTimeBase.den
                                        );

                                        if (skipBytes >= audioData.TotalSize)
                                        {
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
                            }
                        }
                    }
                }
                finally
                {
                    decoderLock.Release();
                }

                Console.WriteLine($"[シーク完了] フレーム {frameIndex}/{GetCurrentFrameInfo(fps, TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond))} に正常移動（frames.Count={frames.Count}）");
                Console.WriteLine($"Audio : {TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond)}, Movie : {TimeSpan.FromSeconds(frameIndex / fps)}");
                playbackState = PlaybackState.Paused;
            }
            finally
            {
                seekLock.Release();
            }

            return true;
        }
        */
        public async Task<bool> SeekToExactFrameAsync(long targetFrameIndex)
        {
            if (!await seekLock.WaitAsync(0))
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
                targetFrameIndex = Math.Max(targetFrameIndex, 1);

                // ==== バッファ内に目的のフレームがあるかチェック ====
                ManagedFrame matchedFrame = null;
                lock (frames)
                {
                    if (frames.TryGetValue(targetFrameIndex, out matchedFrame))
                    {
                        frames.Remove(targetFrameIndex);
                        Console.WriteLine($"[シーク省略] バッファ内にフレーム {targetFrameIndex} が存在しました。");
                    }
                }

                if (matchedFrame == null)
                {
                    // ==== FFmpeg シーク処理 ====
                    var videoStream = decoder.VideoStream;
                    AVRational videoTimeBase = videoStream.time_base;

                    long targetPts = ffmpeg.av_rescale_q(
                        targetFrameIndex,
                        new AVRational { num = rawFps.den, den = rawFps.num },
                        videoTimeBase
                    );

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

                    // ==== バッファクリア ====
                    lock (frames)
                    {
                        foreach (var frame in frames.Values)
                            frame.Dispose();
                        frames.Clear();
                    }

                    Console.WriteLine("[バッファ破棄] すべてのフレームを破棄");

                    // ==== フレーム読み飛ばし ====
                    const int maxSkip = 1000;
                    int skipped = 0;

                    while (skipped++ < maxSkip)
                    {
                        FrameReadResult readResult;
                        ManagedFrame frame = null;

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

                        var ptsFrameIndex = GetFrameIndex(frame);
                        if (ptsFrameIndex == targetFrameIndex)
                        {
                            matchedFrame = frame;
                            break;
                        }

                        frame?.Dispose();
                    }

                    if (matchedFrame == null)
                    {
                        Console.WriteLine("[シーク失敗] 指定フレームに到達できませんでした");
                        playbackState = PlaybackState.Paused;
                        return false;
                    }
                }

                // ==== GPU 転送・描画 ====
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
                matchedFrame.Dispose();

                // ==== 音声シーク ====
                await decoderLock.WaitAsync();
                try
                {
                    unsafe
                    {
                        if (decoder.AudioStreamPointer != null)
                        {
                            var audioStream = decoder.AudioStream;
                            AVRational audioTimeBase = audioStream.time_base;

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
                                ffmpeg.avcodec_flush_buffers(decoder.AudioCodecContextPointer);

                            audioPlayer.ResetBuffer();

                            const int maxSkip = 1000;
                            int skipped = 0;

                            while (skipped++ < maxSkip)
                            {
                                var (readResult, audioFrame) = decoder.TryReadAudioFrame();

                                if (readResult == FrameReadResult.EndOfStream)
                                    return false;

                                if (readResult == FrameReadResult.FrameAvailable)
                                {
                                    if (audioFrame.Frame->pts + (audioTimeBase.den / (double)audioTimeBase.num) >= audioPts)
                                    {
                                        using var audioData = AudioFrameConveter.ConvertTo<PCMInt16Format>(audioFrame);
                                        byte[] data = audioData.AsMemory().ToArray();

                                        long ptsDiff = audioPts - audioFrame.Frame->pts;
                                        int skipBytes = 0;

                                        if (ptsDiff > 0)
                                        {
                                            skipBytes = (int)Math.Round(
                                                (ptsDiff * audioTimeBase.num * audioData.SampleRate * audioData.Channel * audioData.SizeOf)
                                                / (double)audioTimeBase.den);

                                            if (skipBytes < audioData.TotalSize)
                                            {
                                                ReadOnlySpan<byte> sliced = new Span<byte>((void*)audioData.Data, audioData.TotalSize).Slice(skipBytes);
                                                audioPlayer.AddAudioData(sliced);
                                            }
                                        }
                                        else
                                        {
                                            ReadOnlySpan<byte> full = new Span<byte>((void*)audioData.Data, audioData.TotalSize);
                                            audioPlayer.AddAudioData(full);
                                        }

                                        long ptsMicros = ffmpeg.av_rescale_q(audioFrame.Frame->pts, audioTimeBase, new AVRational { num = 1, den = 1000000 });
                                        TimeSpan ptsTime = TimeSpan.FromMilliseconds(ptsMicros / 1000.0);
                                        long basePositionBytes = (long)(ptsTime.TotalSeconds * audioPlayer.AverageBytesPerSecond);
                                        long absoluteBytes = basePositionBytes + skipBytes;
                                        audioPlayer.SetAbsolutePosition(absoluteBytes);

                                        audioFrame.Dispose();
                                        break;
                                    }
                                    audioFrame.Dispose();
                                }
                            }
                        }
                    }
                }
                finally
                {
                    decoderLock.Release();
                }

                playbackState = PlaybackState.SeekBuffering;
                await WaitForBuffer();

                Console.WriteLine($"[シーク完了] フレーム {frameIndex}/{GetCurrentFrameInfo(fps, TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond))} に正常移動（frames.Count={frames.Count}）");
                Console.WriteLine($"Audio : {TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond)}, Movie : {TimeSpan.FromSeconds(frameIndex / fps)}");

                playbackState = PlaybackState.Paused;
                frameIndex = (int)targetFrameIndex + 1;
                return true;
            }
            finally
            {
                seekLock.Release();
            }
        }


        public double CalculateCurrentTimeMs(long currentFrame)
        {
            if (videoInfo == null || videoInfo.VideoStreams.Count == 0)
                return 0;

            var stream = videoInfo.VideoStreams[0]; // 最初の映像ストリームを使用
            var timeBase = stream.TimeBase;

            if (timeBase.Den == 0)
                return 0;

            // PTS = currentFrame * (time_base.num / time_base.den)
            double seconds = currentFrame * ((double)timeBase.Num / timeBase.Den);
            return seconds * 1000.0;
        }

        public long GetTotalFrameCount()
        {
            return (long)(fps * (videoInfo.Duration.Milliseconds / 1000.0));
        }


        /// <summary>
        /// 映像・音声の再生を内部的に実行するメインループ
        /// </summary>
        private async Task PlayInternal()
        {
            _ = Task.Run(() => ReadFrames());
            _ = Task.Run(() => ReadAudioFrames());

            await WaitForBuffer();
            await SeekToExactFrameAsync(2);
            frameIndex = 3;
            TimeSpan frameDuration = TimeSpan.FromMilliseconds(baseFrameDurationMs);
            playbackState = PlaybackState.Playing;
            DateTime playbackStartTime = DateTime.Now;
            Queue<double> boostHistory = new();

            //frameIndex++; // 最初に1フレーム進める
            bool resumed = false;
            audioPlayer.Start();

            var stopwatch = new Stopwatch();

            while (playbackState != PlaybackState.Stopped && playbackState != PlaybackState.Ended)
            {
                if (playbackState == PlaybackState.Playing && frames.Count < frameCap / 4)
                {
                    playbackState = PlaybackState.Buffering;
                    Console.WriteLine("[Buffer] バッファ不足 → 一時停止");
                    audioPlayer?.Pause();
                }

                if (playbackState == PlaybackState.Buffering && frames.Count >= frameCap / 1.2)
                {
                    playbackState = PlaybackState.Playing;
                }

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


                // 再開直後に音声位置と同期
                if (!resumed)
                {
                    /*
                    var position = TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond);
                    var (syncFrameIndex, _) = GetCurrentFrameInfo(fps, position);

                    lock (frames)
                    {
                        var toRemove = frames.Keys.Where(k => k < syncFrameIndex).ToList();

                        foreach (var key in toRemove)
                        {
                            frames[key].Dispose();
                            frames.Remove(key);
                            Console.WriteLine($"[破棄] {key}（音声と同期のため）");
                        }

                        frameIndex = syncFrameIndex;
                    }
                    */
                    var framePtsList = GetBufferedFramePtsList();

                    Console.WriteLine("Buffered Frames:");
                    foreach (var (index, pts) in framePtsList)
                    {
                        Console.WriteLine($"FrameIndex: {index}, PTS: {pts}");
                    }

                    resumed = true;
                    audioPlayer.Resume();
                }

                // frameIndex に一致するフレームを取得
                ManagedFrame? matchedFrame = null;

                lock (frames)
                {
                    if (frames.TryGetValue(frameIndex, out var frame))
                    {
                        unsafe
                        {
                            if (frame.Frame == null || frame.Frame->buf[0] == null)
                            {
                                frame.Dispose();
                                frames.Remove(frameIndex);
                            }
                            else
                            {
                                matchedFrame = frame;
                                frames.Remove(frameIndex);
                            }
                        }
                    }
                }


                if (matchedFrame == null)
                {
                    Console.WriteLine($"[シーク] バッファ内にフレーム {frameIndex} が見つからないためシーク");
                    await SeekToExactFrameAsync(frameIndex);
                    playbackState = PlaybackState.Paused;
                    continue;
                }

                // GPU→CPU 転送
                unsafe
                {
                    if (matchedFrame.IsGpuFrame)
                    {
                        Console.WriteLine("[転送] GPUフレームをCPUに転送");
                        matchedFrame.GetCpuFrame();
                    }
                }

                // === 描画処理 ===
                stopwatch.Restart();
                imageWriter.WriteFrame(matchedFrame, frameConveter);

                unsafe
                {

                    Console.WriteLine($"[描画] フレーム番号: {frameIndex} : {matchedFrame.Frame->pts}");
                }
                matchedFrame.Dispose();



                // ==== 音声との同期補正 ====
                var audioPos = TimeSpan.FromSeconds(audioPlayer.GetPosition() / (double)audioPlayer.AverageBytesPerSecond);
                var (idealFrameIndex, timeInFrame) = GetCurrentFrameInfo(fps, audioPos);
                int frameDiff = idealFrameIndex - frameIndex;

                double adjustedDelayMs = baseFrameDurationMs;

                if (frameDiff != 0)
                {
                    double offsetMs = timeInFrame.TotalMilliseconds + frameDiff * baseFrameDurationMs;

                    if (frameDiff >= 3)
                    {
                        int skip = frameDiff >= 10 ? frameDiff :
                                   frameDiff >= 5 ? Math.Min(5, frameDiff) :
                                   Math.Min(2, frameDiff);

                        int actuallySkipped = 0;

                        lock (frames)
                        {
                            var keysToRemove = frames.Keys
                                .Where(k => k > frameIndex && k <= frameIndex + skip)
                                .Take(skip)
                                .ToList();

                            foreach (var key in keysToRemove)
                            {
                                if (frames.Remove(key, out var skipFrame))
                                {
                                    skipFrame.Dispose();
                                    actuallySkipped++;
                                    frameIndex = (int)key; // 更新は最後のキー
                                }
                            }
                        }

                        Console.WriteLine($"[スキップ補正] {actuallySkipped}フレーム, 差: {frameDiff}, 時間差: {timeInFrame.TotalMilliseconds:F2}ms");
                    }
                    else if (frameDiff < 0)
                    {
                        double delayMs = (-offsetMs) * 1.3;
                        adjustedDelayMs = baseFrameDurationMs + delayMs;
                        Console.WriteLine($"[待機補正] 実: {frameIndex}, 理想: {idealFrameIndex}, 遅延: {delayMs:F2}ms");
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

                // ==== 待機処理（描画にかかった時間を差し引く）====
                long used = stopwatch.ElapsedMilliseconds;
                int remaining = Math.Max(1, (int)(adjustedDelayMs - used));
                await Task.Delay(remaining);

                // ==== フレームバッファ過剰時の整理（古いフレームを削除）====
                lock (frames)
                {
                    while (frames.Count > frameCap)
                    {
                        var first = frames.First();
                        frames.Remove(first.Key);
                        first.Value.Dispose();
                    }
                }
                frameIndex++;

            }

            Stop();
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
        /// フレームのPTSからフレームインデックスを計算（FirstVideoPtsを考慮）
        /// </summary>
        public unsafe long? GetFrameIndex(ManagedFrame frame)
        {
            if (frame == null || frame.Frame == null || frame.Frame->pts == ffmpeg.AV_NOPTS_VALUE)
                return null;

            AVRational timeBase = decoder.VideoStream.time_base;

            long framePts = frame.Frame->pts;

            // 最初のPTSとの差分（= 映像経過時間）
            long relativePts = framePts;

            if (decoder.FirstVideoPts.HasValue)
            {
                relativePts -= decoder.FirstVideoPts.Value;
                //Console.WriteLine(decoder.FirstVideoPts.Value);
                // 負の値は不正なので無効扱い
                if (relativePts < 0)
                    return null;
            }

            long frameIndex = ffmpeg.av_rescale_q(
                relativePts,
                timeBase,
                new AVRational { num = videoFps.den, den = videoFps.num }  // 秒 → フレームに換算
            );

            return frameIndex;
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
        private SortedDictionary<long, ManagedFrame> frames = new(); // 映像フレームバッファ

        private async Task ReadFrames()
        {
            int transferredCount = 0;
            int totalFramesToTransfer = 0;
            const int preTransferThreshold = 20;

            while (playbackState != PlaybackState.Stopped && playbackState != PlaybackState.Ended)
            {
                if (playbackState == PlaybackState.Seeking)
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

                await decoderLock.WaitAsync();
                try
                {
                    (result, frame) = decoder.TryReadFrame();
                }
                finally
                {
                    decoderLock.Release();
                    sw.Stop();
                }

                if (result == FrameReadResult.FrameAvailable)
                {
                    long? index = GetFrameIndex(frame);

                    if (playbackState == PlaybackState.Stopped || index == null)
                    {
                        frame.Dispose();
                        continue;
                    }

                    if (index <= frameIndex)
                    {
                        // 古いフレームは破棄
                        frame.Dispose();
                        continue;
                    }

                    lock (frames)
                    {
                        if (!frames.ContainsKey(index.Value))
                        {
                            frames.Add(index.Value, frame);
                            decodedFrames++;
                            Interlocked.Increment(ref totalFramesToTransfer);
                        }
                        else
                        {
                            frame.Dispose(); // 重複防止
                        }
                    }

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

                // ==== 転送処理（先頭から preTransferThreshold 件）====
                List<ManagedFrame> toTransfer = null;
                lock (frames)
                {
                    toTransfer = frames.Values.Take(preTransferThreshold).Where(f => f.IsGpuFrame).ToList();
                }

                foreach (var f in toTransfer)
                {
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
                    continue;
                }

                if (frames.Count > frameCap * 0.8)
                {
                    await Task.Delay(1);
                }
            }
        }





        /// <summary>
        /// 現在バッファに格納されているフレームの index と PTS の対応一覧を取得します。
        /// </summary>
        public List<(long FrameIndex, long Pts)> GetBufferedFramePtsList()
        {
            var list = new List<(long FrameIndex, long Pts)>();

            lock (frames)
            {
                foreach (var kvp in frames.OrderBy(k => k.Key))
                {
                    unsafe
                    {
                        if (kvp.Value.Frame != null && kvp.Value.Frame->pts != ffmpeg.AV_NOPTS_VALUE)
                        {
                            list.Add((kvp.Key, kvp.Value.Frame->pts));
                        }
                    }
                }
            }

            return list;
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
                // バッファ待機・シーク中など
                if (playbackState == PlaybackState.Buffering || playbackState == PlaybackState.Seeking)
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
