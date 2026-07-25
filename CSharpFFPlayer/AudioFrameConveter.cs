using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CSharpFFPlayer
{
    public unsafe class AudioFrameConveter
    {
        // SwrContext をフレーム毎に確保・破棄すると swr_init のテーブル構築コストが
        // 毎フレーム発生する。入出力フォーマットは 1 ファイル内で変わらないため、
        // 条件が変わったときだけ作り直して使い回す。
        private static readonly object swrLock = new();
        private static SwrContext* cachedContext = null;
        private static int cachedInFormat = -1;
        private static int cachedSampleRate = -1;
        private static int cachedChannels = -1;
        private static AVChannelOrder cachedChannelOrder = default;
        private static AVSampleFormat cachedOutFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;

        /// <summary>出力フォーマット記述子をインスタンス化し直さないためのキャッシュ。</summary>
        private static class FormatCache<T> where T : OutputFormat, new()
        {
            public static readonly T Instance = new();
        }

        /// <summary>
        /// ラップされた ManagedFrame から目的の形式に変換された AudioData を生成する。
        /// </summary>
        public static AudioData ConvertTo<TOut>(ManagedFrame frame) where TOut : OutputFormat, new()
        {
            return ConvertTo<TOut>(frame.Frame);
        }

        /// <summary>
        /// 生の AVFrame から目的の形式に変換された AudioData を生成する。
        /// </summary>
        public static AudioData ConvertTo<TOut>(AVFrame* frame) where TOut : OutputFormat, new()
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            var output = FormatCache<TOut>.Instance;

            // キャッシュした SwrContext を複数スレッド（再生ループ／シーク）から
            // 触るため、変換ごと直列化する。変換自体は短時間で終わる。
            lock (swrLock)
            {
                SwrContext* context = GetOrCreateContext(frame, output.AVSampleFormat);

                int sampleSize = output.SizeOf;
                int channels = frame->ch_layout.nb_channels;
                int bufferSize = frame->nb_samples * channels * sampleSize;

                // 出力バッファを確保（AudioData.Dispose / ファイナライザで解放）
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                byte* ptr = (byte*)buffer.ToPointer();

                int convertedSamples = ffmpeg.swr_convert(
                    context, &ptr, frame->nb_samples,
                    frame->extended_data, frame->nb_samples);

                if (convertedSamples < 0)
                {
                    Marshal.FreeHGlobal(buffer);
                    throw new Exception("swr_convert に失敗しました");
                }

                return new AudioData
                {
                    Samples = convertedSamples,
                    SampleRate = frame->sample_rate,
                    Channel = channels,
                    SizeOf = sampleSize,
                    Data = buffer,
                    TotalSize = convertedSamples * channels * sampleSize
                };
            }
        }

        /// <summary>
        /// 入出力条件が前回と同じならキャッシュした SwrContext を返す。
        /// 違う場合のみ作り直す。※ swrLock を保持した状態で呼ぶこと。
        /// </summary>
        private static SwrContext* GetOrCreateContext(AVFrame* frame, AVSampleFormat outFormat)
        {
            int inFormat = frame->format;
            int sampleRate = frame->sample_rate;
            int channels = frame->ch_layout.nb_channels;
            AVChannelOrder order = frame->ch_layout.order;

            if (cachedContext != null &&
                cachedInFormat == inFormat &&
                cachedSampleRate == sampleRate &&
                cachedChannels == channels &&
                cachedChannelOrder == order &&
                cachedOutFormat == outFormat)
            {
                return cachedContext;
            }

            FreeCachedContextCore();

            SwrContext* context = null;
            AVChannelLayout inLayout = frame->ch_layout;
            AVChannelLayout outLayout = inLayout;

            if (ffmpeg.swr_alloc_set_opts2(&context,
                    &outLayout, outFormat, sampleRate,
                    &inLayout, (AVSampleFormat)inFormat, sampleRate,
                    0, null) < 0)
            {
                throw new Exception("swr_alloc_set_opts2 に失敗しました");
            }

            if (ffmpeg.swr_init(context) < 0)
            {
                ffmpeg.swr_free(&context);
                throw new Exception("swr_init に失敗しました");
            }

            cachedContext = context;
            cachedInFormat = inFormat;
            cachedSampleRate = sampleRate;
            cachedChannels = channels;
            cachedChannelOrder = order;
            cachedOutFormat = outFormat;

            Console.WriteLine($"[Audio] SwrContext を構築: {(AVSampleFormat)inFormat} {sampleRate}Hz {channels}ch -> {outFormat}");
            return context;
        }

        /// <summary>キャッシュしている SwrContext を解放する（ファイルを閉じるときなど）。</summary>
        public static void ReleaseCachedContext()
        {
            lock (swrLock)
            {
                FreeCachedContextCore();
            }
        }

        private static void FreeCachedContextCore()
        {
            if (cachedContext == null) return;

            SwrContext* tmp = cachedContext;
            ffmpeg.swr_free(&tmp);

            cachedContext = null;
            cachedInFormat = -1;
            cachedSampleRate = -1;
            cachedChannels = -1;
            cachedOutFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;
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

        private nint _data;

        /// <summary>変換後 PCM を保持するアンマネージドバッファ。</summary>
        public nint Data { get => _data; set => _data = value; }

        public int TotalSize{ get; set; }

        /// <summary>
        /// バッファを ReadOnlySpan<byte> として取得する（コピーなし）。
        /// フレーム毎に呼ぶ経路ではこちらを使うこと。
        /// </summary>
        public unsafe ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Data.ToPointer(), Samples * Channel * SizeOf);
        }

        /// <summary>
        /// バッファを ReadOnlyMemory<byte> として取得する。
        /// ※内部で byte[] にコピーするため、フレーム毎の経路では AsSpan() を使うこと。
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
            FreeBuffer();
            GC.SuppressFinalize(this);
        }

        // Dispose 漏れでアンマネージドバッファが取り残されないための保険。
        // 通常は using / Dispose で回収されるためファイナライザは走らない。
        ~AudioData() => FreeBuffer();

        private void FreeBuffer()
        {
            nint p = Interlocked.Exchange(ref _data, nint.Zero);
            if (p != nint.Zero) Marshal.FreeHGlobal(p);
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
