using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics.Eventing.Reader;

namespace CSharpFFPlayer
{
    class AudioPlayer : IDisposable
    {
        private WasapiOut output;
        private BufferedWaveProvider bufferedWaveProvider;
        private IWaveProvider finalProvider;
        private long offsetBytes = 0;

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

        /// <summary>
        /// 現在の補正付き再生位置（バイト単位）
        /// </summary>
        public long GetPosition(bool includeOffset = true)
        {
            long pos = output?.GetPosition() ?? 0;
            return includeOffset ? pos + offsetBytes : pos;
        }

        /// <summary>
        /// 音声バッファをクリアし、オフセットも初期化
        /// </summary>
        public void ResetBuffer()
        {
            offsetBytes = 0;

            if (bufferedWaveProvider != null)
            {
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
        /// 再生位置を「絶対バイト数」で補正します（シーク時）
        /// </summary>
        public void SetAbsolutePosition(long absolutePositionBytes)
        {
            long currentRaw = output?.GetPosition() ?? 0;
            offsetBytes = absolutePositionBytes - currentRaw;

            Console.WriteLine($"[Audio] SetAbsolutePosition → Absolute={absolutePositionBytes}, Raw={currentRaw}, Offset={offsetBytes}");
        }

        /// <summary>
        /// 再生一時停止。recordOffset = true の場合は一時停止時点までの位置を記録
        /// </summary>
        public void Pause(bool recordOffset = false)
        {
            try
            {
                if (recordOffset && output?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    long current = output.GetPosition();
                    offsetBytes += current;
                    Console.WriteLine($"[Audio] Pause → offsetBytes += {current} → {offsetBytes}");
                }

                if (output?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    output.Pause();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Error on Pause: {ex.Message}");
            }
        }


        /// <summary>
        /// 一時停止状態からの再開
        /// </summary>
        public void Resume()
        {
            if (output?.PlaybackState == NAudio.Wave.PlaybackState.Paused)
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
