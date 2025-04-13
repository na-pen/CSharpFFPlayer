using NAudio.Wave;
using NAudio.CoreAudioApi;
using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave.SampleProviders;
using FFmpegPlayer;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CSharpFFPlayer
{
    class AudioPlayer : IDisposable
    {
        private WasapiOut output;
        public TimeSpan BufferedDuration => bufferedWaveProvider.BufferedDuration;
        private BufferedWaveProvider bufferedWaveProvider;
        public int AverageBytesPerSecond => output.OutputWaveFormat.AverageBytesPerSecond;

        public AudioPlayer() { }

        public void Init(WaveFormat? inputFormat = null, float volume = 1.0f, int latency = 200)
        {
            var mmDevice = new MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var mixFormat = mmDevice.AudioClient.MixFormat;
            WaveFormat waveFormat = inputFormat ?? mixFormat;

            bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(30),
                DiscardOnBufferOverflow = false,
            };

            IWaveProvider finalProvider;

            if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                // float 形式の場合
                var sampleProvider = bufferedWaveProvider.ToSampleProvider();

                // 3ch以上 → 2chへダウンミックス（先頭2chのみ使用）
                if (waveFormat.Channels > 2)
                {
                    sampleProvider = new StereoSampleProvider(sampleProvider);
                }

                var volumeSample = new SampleChannel(sampleProvider.ToWaveProvider(), true)
                {
                    Volume = volume
                };

                finalProvider = volumeSample.ToWaveProvider();
            }
            else if (waveFormat.Encoding == WaveFormatEncoding.Pcm && waveFormat.BitsPerSample == 16)
            {
                // 16bit PCM の場合
                IWaveProvider pcmProvider = bufferedWaveProvider;

                if (waveFormat.Channels > 2)
                {
                    // PCMのチャンネル数が3ch以上 → floatに変換してダウンミックス
                    var floatProvider = bufferedWaveProvider.ToSampleProvider();
                    var stereoProvider = new StereoSampleProvider(floatProvider);
                    var volumeSample = new SampleChannel(stereoProvider.ToWaveProvider(), true)
                    {
                        Volume = volume
                    };

                    finalProvider = volumeSample.ToWaveProvider();
                }
                else
                {
                    finalProvider = new VolumeWaveProvider16(bufferedWaveProvider)
                    {
                        Volume = volume
                    };
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported WaveFormat: {waveFormat.Encoding} {waveFormat.BitsPerSample}bit");
            }

            output = new WasapiOut(mmDevice, AudioClientShareMode.Shared, false, latency);
            output.Init(finalProvider);

            Console.WriteLine($"Using audio device: {mmDevice.FriendlyName}");
        }

        public long GetPosition()
        {
            return output.GetPosition();
        }

        public void Start()
        {
            output.Play();
        }

        public void Pause()
        {
            if (output?.PlaybackState == PlaybackState.Playing)
            {
                output.Pause();
            }
        }

        public void Resume()
        {
            if (output?.PlaybackState == PlaybackState.Paused)
            {
                output.Play();
            }
        }

        public async Task FeedStreamAsync(Stream bufferedPCMStream)
        {
            byte[] buffer = new byte[bufferedWaveProvider.WaveFormat.AverageBytesPerSecond];

            int bytesRead;
            while ((bytesRead = await bufferedPCMStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                bufferedWaveProvider.AddSamples(buffer, 0, bytesRead);
                await Task.Delay(50);
            }
        }
        public void AddAudioData(ReadOnlySpan<byte> audioData)
        {
            bufferedWaveProvider.AddSamples(audioData.ToArray(), 0, audioData.Length);
        }


        public void Dispose()
        {
            output?.Dispose();
        }
    }
}