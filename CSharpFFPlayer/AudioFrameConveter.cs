using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CSharpFFPlayer
{
    public class AudioFrameConveter
    {
        public static unsafe AudioData ConvertTo<TOut>(ManagedFrame frame) where TOut : OutputFormat, new()
        {
            return ConvertTo<TOut>(frame.Frame);
        }

        public static unsafe AudioData ConvertTo<TOut>(AVFrame* frame) where TOut : OutputFormat, new()
        {
            var output = new TOut();

            SwrContext* context = ffmpeg.swr_alloc();
            if (context == null)
                throw new Exception("swr_alloc failed");

            try
            {
                // 入出力チャンネルレイアウト
                AVChannelLayout inLayout = frame->ch_layout;
                AVChannelLayout outLayout = inLayout;

                // context に設定
                ffmpeg.swr_alloc_set_opts2(&context,
                    &outLayout, output.AVSampleFormat, frame->sample_rate,
                    &inLayout, (AVSampleFormat)frame->format, frame->sample_rate,
                    0, null);

                if (ffmpeg.swr_init(context) < 0)
                {
                    throw new Exception("swr_init failed");
                }

                int size = output.SizeOf;
                int bufferSize = frame->nb_samples * frame->ch_layout.nb_channels * size;

                var buffer = Marshal.AllocHGlobal(bufferSize);
                byte* ptr = (byte*)buffer.ToPointer();

                int convertedSamples = ffmpeg.swr_convert(context, &ptr, frame->nb_samples, frame->extended_data, frame->nb_samples);
                if (convertedSamples < 0)
                {
                    Marshal.FreeHGlobal(buffer); // 失敗時は解放
                    throw new Exception("swr_convert failed");
                }

                return new AudioData()
                {
                    Samples = convertedSamples,
                    SampleRate = frame->sample_rate,
                    Channel = frame->ch_layout.nb_channels,
                    SizeOf = size,
                    Data = buffer,
                };
            }
            finally
            {
                if (context != null)
                {
                    ffmpeg.swr_free(&context);
                }
            }
        }
        


    }

    public class AudioData : IDisposable
    {
        public int Samples { get; set; }
        public int SampleRate { get; set; }
        public int Channel { get; set; }
        public int SizeOf { get; set; }

        public nint Data { get; set; }

        public unsafe ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Data.ToPointer(), Samples * Channel * SizeOf);
        }

        // 追加
        public ReadOnlyMemory<byte> AsMemory()
        {
            return ToByteArray();
        }

        public byte[] ToByteArray()
        {
            int length = Samples * Channel * SizeOf;
            byte[] result = new byte[length];
            Marshal.Copy(Data, result, 0, length);
            return result;
        }

        public void Dispose()
        {
        if (Data != nint.Zero)
        {
            Marshal.FreeHGlobal(Data);
            Data = nint.Zero;
        }
    }
    }


    public abstract class OutputFormat
    {
        public abstract AVSampleFormat AVSampleFormat { get; }

        public abstract int SizeOf { get; }
    }

    public class PCMInt16Format : OutputFormat
    {
        public PCMInt16Format() { }

        public override AVSampleFormat AVSampleFormat => AVSampleFormat.AV_SAMPLE_FMT_S16;
        public override int SizeOf => sizeof(ushort);
    }
}
