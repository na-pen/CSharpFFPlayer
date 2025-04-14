using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Threading.Tasks;

namespace CSharpFFPlayer
{
    class AudioPlayer : IDisposable
    {
        private WasapiOut output;
        private BufferedWaveProvider bufferedWaveProvider;
        private IWaveProvider finalProvider;

        /// <summary>
        /// 現在のバッファに保持されている音声の持続時間（ミリ秒単位）
        /// </summary>
        public TimeSpan BufferedDuration => bufferedWaveProvider?.BufferedDuration ?? TimeSpan.Zero;

        /// <summary>
        /// 出力フォーマットに基づく1秒あたりの平均バイト数
        /// </summary>
        public int AverageBytesPerSecond => output?.OutputWaveFormat.AverageBytesPerSecond ?? 0;

        public AudioPlayer() { }

        /// <summary>
        /// オーディオ出力の初期化処理。必要なWaveFormatやボリューム、遅延時間（ミリ秒）を設定する。
        /// </summary>
        public void Init(WaveFormat? inputFormat = null, float volume = 1.0f, int latencyMs = 200)
        {
            var deviceEnumerator = new MMDeviceEnumerator();
            var mmDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var waveFormat = inputFormat ?? mmDevice.AudioClient.MixFormat;

            bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(30000),
                DiscardOnBufferOverflow = false
            };

            finalProvider = CreateOutputProvider(waveFormat, volume);

            output = new WasapiOut(mmDevice, AudioClientShareMode.Shared, false, latencyMs);
            output.Init(finalProvider);

            Console.WriteLine($"[Audio] Initialized with device: {mmDevice.FriendlyName}");
        }

        /// <summary>
        /// 出力用プロバイダを作成する。フォーマットに応じてダウンミックスや音量調整を行う。
        /// </summary>
        private IWaveProvider CreateOutputProvider(WaveFormat format, float volume)
        {
            if (bufferedWaveProvider == null)
                throw new InvalidOperationException("BufferedWaveProvider is not initialized.");

            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var sampleProvider = bufferedWaveProvider.ToSampleProvider();

                if (format.Channels > 2)
                    sampleProvider = new StereoSampleProvider(sampleProvider);

                var volumeSample = new SampleChannel(sampleProvider.ToWaveProvider(), true) { Volume = volume };
                return volumeSample.ToWaveProvider();
            }

            if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                if (format.Channels > 2)
                {
                    var stereoProvider = new StereoSampleProvider(bufferedWaveProvider.ToSampleProvider());
                    var volumeSample = new SampleChannel(stereoProvider.ToWaveProvider(), true) { Volume = volume };
                    return volumeSample.ToWaveProvider();
                }

                return new VolumeWaveProvider16(bufferedWaveProvider) { Volume = volume };
            }

            throw new NotSupportedException($"Unsupported WaveFormat: {format.Encoding} {format.BitsPerSample}bit");
        }

        private long playbackOffsetBytes = 0;

        /// <summary>
        /// シーク直後などに呼び出して、現在の再生バイト数を基準として記録します。
        /// </summary>
        public void ResetPlaybackTime()
        {
            playbackOffsetBytes = output?.GetPosition() ?? 0;
        }

        /// <summary>
        /// シークで任意の再生バイト位置にリセットする場合（手動オフセット指定）
        /// </summary>
        public void ResetPlaybackTime(long targetByteOffset)
        {
            playbackOffsetBytes = targetByteOffset;
        }

        /// <summary>
        /// 現在の補正込み再生バイト数を返します（累積位置）
        /// </summary>
        public long GetPosition()
        {
            long current = output?.GetPosition() ?? 0;
            return current - playbackOffsetBytes;
        }

        /// <summary>
        /// 現在の累積再生時間（TimeSpan）を返します。
        /// </summary>
        public TimeSpan GetPositionTime()
        {
            int bytesPerSec = AverageBytesPerSecond;
            if (bytesPerSec <= 0) return TimeSpan.Zero;

            return TimeSpan.FromSeconds((double)GetPosition() / bytesPerSec);
        }

        /// <summary>
        /// 音声バッファをクリアし、再生位置オフセットもリセットします。
        /// </summary>
        public void ResetBuffer()
        {
            if (bufferedWaveProvider != null)
            {
                // 現在のバッファに格納されているサンプルを読み飛ばして削除
                while (bufferedWaveProvider.BufferedBytes > 0)
                {
                    byte[] dummy = new byte[bufferedWaveProvider.BufferedBytes];
                    bufferedWaveProvider.Read(dummy, 0, dummy.Length);
                }
            }
        }


        /// <summary>
        /// 音声の再生を開始する
        /// </summary>
        public void Start()
        {
            if (output == null)
            {
                Console.WriteLine("[Audio] Cannot start: output is not initialized.");
                return;
            }

            try
            {
                output.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Error on Start: {ex.Message}");
            }
        }

        /// <summary>
        /// 音声の一時停止処理。再生中のみ適用される。
        /// </summary>
        public void Pause()
        {
            if (output?.PlaybackState == PlaybackState.Playing)
            {
                try
                {
                    output.Pause();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Audio] Error on Pause: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 一時停止状態からの再開
        /// </summary>
        public void Resume()
        {
            if (output?.PlaybackState == PlaybackState.Paused)
            {
                try
                {
                    output.Play();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Audio] Error on Resume: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// PCMストリームからデータを一定量ずつ読み取り、バッファに追加する（非同期）
        /// </summary>
        public async Task FeedStreamAsync(Stream bufferedPCMStream)
        {
            if (bufferedPCMStream == null || bufferedWaveProvider == null)
            {
                Console.WriteLine("[Audio] Stream is null or not initialized.");
                return;
            }

            byte[] buffer = new byte[bufferedWaveProvider.WaveFormat.AverageBytesPerSecond];

            try
            {
                int bytesRead;
                while ((bytesRead = await bufferedPCMStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    bufferedWaveProvider.AddSamples(buffer, 0, bytesRead);
                    await Task.Delay(50); // 過剰な供給を防ぐための短い待機
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Error during stream feed: {ex.Message}");
            }
        }

        /// <summary>
        /// 音声のバイト列データを直接バッファに追加する
        /// </summary>
        public void AddAudioData(ReadOnlySpan<byte> audioData)
        {
            if (bufferedWaveProvider == null) return;

            try
            {
                bufferedWaveProvider.AddSamples(audioData.ToArray(), 0, audioData.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Error while adding data: {ex.Message}");
            }
        }

        /// <summary>
        /// 出力の破棄処理
        /// </summary>
        public void Dispose()
        {
            output?.Dispose();
        }
    }
}
