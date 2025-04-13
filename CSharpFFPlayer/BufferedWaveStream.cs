using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpFFPlayer
{
    public class BufferedWaveStream : WaveStream
    {
        private readonly Stream audioSource;
        private readonly WaveFormat waveFormat;

        public BufferedWaveStream(Stream sourceStream, WaveFormat format)
        {
            audioSource = sourceStream;
            waveFormat = format;
        }

        public override WaveFormat WaveFormat => waveFormat;

        public override long Length => audioSource.Length;

        public override long Position
        {
            get => audioSource.Position;
            set => audioSource.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return audioSource.Read(buffer, offset, count);
        }
    }
}
