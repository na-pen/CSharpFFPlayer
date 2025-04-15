using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;

namespace CSharpFFPlayer
{
    class AudioPlayerV2 : IDisposable
    {
        private IWavePlayer output;
        private WaveStream reader;

        public bool IsPlaying => output.PlaybackState.Equals(PlaybackState.Playing);
        public bool IsPaused => output.PlaybackState.Equals(PlaybackState.Paused);

        /// <summary>
        /// 音声ファイルを開いて再生準備します。
        /// </summary>
        /// <param name="filePath">動画ファイルのパス</param>
        public void Init(string filePath)
        {
            try
            {
                reader = new MediaFoundationReader(filePath); // 音声ストリーム抽出
                output = new WasapiOut(AudioClientShareMode.Shared, 200); // 共有モードで低遅延出力
                output.Init(reader);
                Console.WriteLine($"[AudioV2] Initialized with: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioV2] Init failed: {ex.Message}");
                Dispose();
            }
        }

        /// <summary>
        /// 再生開始または再開
        /// </summary>
        public void Play()
        {
            if (output == null) return;

            try
            {
                output.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioV2] Play error: {ex.Message}");
            }
        }

        /// <summary>
        /// 一時停止
        /// </summary>
        public void Pause()
        {
            try
            {
                output?.Pause();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioV2] Pause error: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止して先頭に巻き戻す
        /// </summary>
        public void Stop()
        {
            try
            {
                output?.Stop();
                if (reader != null)
                    reader.Position = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioV2] Stop error: {ex.Message}");
            }
        }

        /// <summary>
        /// 現在の再生位置を取得（秒）
        /// </summary>
        public TimeSpan GetPosition() => reader?.CurrentTime ?? TimeSpan.Zero;

        /// <summary>
        /// 再生位置をシーク（秒）
        /// </summary>
        public void Seek(TimeSpan position)
        {
            if (reader != null && reader.CanSeek)
            {
                reader.CurrentTime = position;
            }
        }

        public void Dispose()
        {
            output?.Stop();
            output?.Dispose();
            reader?.Dispose();
        }
    }
}
