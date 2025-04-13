using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpFFPlayer
{
    public class StereoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int sourceChannels;

        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public StereoSampleProvider(ISampleProvider source)
        {
            if (source.WaveFormat.Channels < 2)
                throw new ArgumentException("StereoSampleProvider requires at least 2 channels");

            this.source = source;
            sourceChannels = source.WaveFormat.Channels;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesPerFrame = sourceChannels;
            int stereoSamplesRequested = count / 2; // left + right
            float[] sourceBuffer = new float[stereoSamplesRequested * sourceChannels];

            int read = source.Read(sourceBuffer, 0, stereoSamplesRequested * sourceChannels);
            int samplesRead = read / sourceChannels;

            for (int i = 0; i < samplesRead; i++)
            {
                buffer[offset++] = sourceBuffer[i * sourceChannels];     // left
                buffer[offset++] = sourceBuffer[i * sourceChannels + 1]; // right
            }

            return samplesRead * 2;
        }
    }
}
