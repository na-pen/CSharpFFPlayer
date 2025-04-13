using NAudio.Wave;
using System;

namespace CSharpFFPlayer
{
    /// <summary>
    /// 多チャンネル音声入力をステレオに変換する ISampleProvider 実装。
    /// 現在は3ch以上の場合、左=ch0, 右=ch1を使用。将来的にミックスダウン対応予定。
    /// </summary>
    public class StereoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int sourceChannels;

        /// <summary>
        /// 出力フォーマットは IEEE Float, ステレオ, 元と同じサンプルレート。
        /// </summary>
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        /// <summary>
        /// コンストラクタ。2ch以上の ISampleProvider を受け取り、ステレオ形式に変換する。
        /// </summary>
        /// <param name="source">多チャンネルの ISampleProvider</param>
        /// <exception cref="ArgumentException">入力チャンネル数が2未満の場合</exception>
        public StereoSampleProvider(ISampleProvider source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (source.WaveFormat.Channels < 2)
                throw new ArgumentException("StereoSampleProvider requires at least 2 channels", nameof(source));

            this.source = source;
            sourceChannels = source.WaveFormat.Channels;
        }

        /// <summary>
        /// 入力ストリームから音声を読み取り、ステレオ（左=ch0、右=ch1）に変換してバッファへ格納する。
        /// </summary>
        /// <param name="buffer">出力先バッファ</param>
        /// <param name="offset">出力バッファの開始位置</param>
        /// <param name="count">出力に要求するサンプル数（ステレオ換算）</param>
        /// <returns>書き込んだサンプル数（ステレオなので常に2の倍数）</returns>
        public int Read(float[] buffer, int offset, int count)
        {
            // 読み込むステレオサンプル数
            int stereoSampleCount = count / 2;

            // 内部バッファの準備（すべてのチャンネル分）
            float[] sourceBuffer = new float[stereoSampleCount * sourceChannels];

            // 入力から読み取り
            int readSamples = source.Read(sourceBuffer, 0, sourceBuffer.Length);
            int framesRead = readSamples / sourceChannels;

            // ステレオ出力用ループ
            for (int i = 0; i < framesRead; i++)
            {
                // 左チャンネル = ch0、右チャンネル = ch1
                buffer[offset++] = sourceBuffer[i * sourceChannels];       // 左
                buffer[offset++] = sourceBuffer[i * sourceChannels + 1];   // 右
            }

            return framesRead * 2; // ステレオ出力（left + right）
        }
    }
}
