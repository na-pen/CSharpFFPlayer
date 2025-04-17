using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpFFPlayer
{
    public class VideoInfo
    {
        public string FilePath { get; set; }
        public TimeInfo Duration { get; set; }
        public int StreamCount { get; set; }
        public BitRateInfo BitRate { get; set; }

        public List<VideoStreamInfo> VideoStreams { get; set; } = new();
        public List<AudioStreamInfo> AudioStreams { get; set; } = new();
        public List<OtherStreamInfo> OtherStreams { get; set; } = new();
    }

    public struct TimeInfo
    {
        public double Milliseconds { get; set; }

        public TimeSpan ToTimeSpan()
        {
            return TimeSpan.FromMilliseconds(Milliseconds);
        }
    }

    public struct BitRateInfo
    {
        public long BitsPerSecond { get; set; }
    }

    public class VideoStreamInfo
    {
        public int Index { get; set; }
        public string CodecName { get; set; }
        public ResolutionInfo Resolution { get; set; }
        public double Fps { get; set; }
        public Rational TimeBase { get; set; }
    }

    public struct ResolutionInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class AudioStreamInfo
    {
        public int Index { get; set; }
        public string CodecName { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public Rational TimeBase { get; set; }
    }

    public class OtherStreamInfo
    {
        public int Index { get; set; }
        public string StreamType { get; set; }
    }

    public struct Rational
    {
        public int Num { get; set; }
        public int Den { get; set; }
    }
}
