using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;

namespace CSharpFFPlayer
{
    public class AudioFrameConveter
    {
        /// <summary>
        /// ラップされた ManagedFrame から目的の形式に変換された AudioData を生成する。
        /// </summary>
        public static unsafe AudioData ConvertTo<TOut>(ManagedFrame frame) where TOut : OutputFormat, new()
        {
            return ConvertTo<TOut>(frame.Frame);
        }

        /// <summary>
        /// 生の AVFrame から目的の形式に変換された AudioData を生成する。
        /// </summary>
        public static unsafe AudioData ConvertTo<TOut>(AVFrame* frame) where TOut : OutputFormat, new()
        {
            var output = new TOut();
            SwrContext* context = ffmpeg.swr_alloc();

            if (context == null)
            {
                throw new Exception("swr_alloc に失敗しました");
            }

            try
            {
                // 入出力のチャンネルレイアウトを設定（今回は同じものを使用）
                AVChannelLayout inLayout = frame->ch_layout;
                AVChannelLayout outLayout = inLayout;

                // サンプルレートとフォーマットを指定してコンテキストを初期化
                if (ffmpeg.swr_alloc_set_opts2(&context,
                        &outLayout, output.AVSampleFormat, frame->sample_rate,
                        &inLayout, (AVSampleFormat)frame->format, frame->sample_rate,
                        0, null) < 0)
                {
                    throw new Exception("swr_alloc_set_opts2 に失敗しました");
                }

                if (ffmpeg.swr_init(context) < 0)
                {
                    throw new Exception("swr_init に失敗しました");
                }

                // 出力バッファのサイズを計算
                int sampleSize = output.SizeOf;
                int bufferSize = frame->nb_samples * frame->ch_layout.nb_channels * sampleSize;

                // 出力バッファを確保
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                byte* ptr = (byte*)buffer.ToPointer();

                // サンプルの変換を実行
                int convertedSamples = ffmpeg.swr_convert(
                    context, &ptr, frame->nb_samples,
                    frame->extended_data, frame->nb_samples);

                if (convertedSamples < 0)
                {
                    Marshal.FreeHGlobal(buffer);
                    throw new Exception("swr_convert に失敗しました");
                }

                // 正常に変換できた場合の AudioData を返却
                return new AudioData
                {
                    Samples = convertedSamples,
                    SampleRate = frame->sample_rate,
                    Channel = frame->ch_layout.nb_channels,
                    SizeOf = sampleSize,
                    Data = buffer
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

    /// <summary>
    /// 音声データのバッファとメタ情報を保持するクラス
    /// </summary>
    public class AudioData : IDisposable
    {
        public int Samples { get; set; }
        public int SampleRate { get; set; }
        public int Channel { get; set; }
        public int SizeOf { get; set; }

        public nint Data { get; set; }

        /// <summary>
        /// バッファを ReadOnlySpan<byte> として取得する。
        /// </summary>
        public unsafe ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Data.ToPointer(), Samples * Channel * SizeOf);
        }

        /// <summary>
        /// バッファを ReadOnlyMemory<byte> として取得する（内部的には配列に変換）
        /// </summary>
        public ReadOnlyMemory<byte> AsMemory()
        {
            return ToByteArray();
        }

        /// <summary>
        /// バッファを byte[] にコピーして取得する。
        /// </summary>
        public byte[] ToByteArray()
        {
            int length = Samples * Channel * SizeOf;
            byte[] result = new byte[length];
            Marshal.Copy(Data, result, 0, length);
            return result;
        }

        /// <summary>
        /// バッファのメモリを安全に解放する。
        /// </summary>
        public void Dispose()
        {
            if (Data != nint.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = nint.Zero;
            }
        }
    }

    /// <summary>
    /// 出力フォーマット情報の基底クラス（型ごとに AVSampleFormat とサイズを定義）
    /// </summary>
    public abstract class OutputFormat
    {
        public abstract AVSampleFormat AVSampleFormat { get; }
        public abstract int SizeOf { get; }
    }

    /// <summary>
    /// 16bit PCM（符号付き）形式の出力定義
    /// </summary>
    public class PCMInt16Format : OutputFormat
    {
        public override AVSampleFormat AVSampleFormat => AVSampleFormat.AV_SAMPLE_FMT_S16;
        public override int SizeOf => sizeof(short); // ushort → short（正確には signed PCM）
    }
}
