using FFmpeg.AutoGen;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace CSharpFFPlayer
{
    /// <summary>
    /// デコード済みフレームを WPF の WriteableBitmap に描画するクラス。
    /// </summary>
    public class ImageWriter
    {
        private readonly Int32Rect rect;
        private readonly WriteableBitmap writeableBitmap;
        private Effect _effect = new();

        /// <summary>
        /// 描画領域と WriteableBitmap を初期化する。
        /// </summary>
        /// <param name="width">描画領域の幅</param>
        /// <param name="height">描画領域の高さ</param>
        /// <param name="writeableBitmap">描画先の WriteableBitmap</param>
        public ImageWriter(int width, int height, WriteableBitmap writeableBitmap)
        {
            if (writeableBitmap == null)
                throw new ArgumentNullException(nameof(writeableBitmap), "描画先の WriteableBitmap が null です");

            rect = new Int32Rect(0, 0, width, height);
            this.writeableBitmap = writeableBitmap;
        }

        /// <summary>
        /// デコード済みのフレームを WriteableBitmap に描画する。
        /// </summary>
        /// <param name="frame">描画するフレーム（デコード済み）</param>
        /// <param name="frameConveter">YUV→RGB 変換コンバータ</param>
        public unsafe void WriteFrame(ManagedFrame frame, FrameConveter frameConveter,EffectType effect)
        {
            // 引数の null チェック
            if (frame == null)
                throw new ArgumentNullException(nameof(frame), "フレームが null です");

            if (frameConveter == null)
                throw new ArgumentNullException(nameof(frameConveter), "フレーム変換器が null です");

            // WriteableBitmap のロックを行い、バッファポインタを取得
            writeableBitmap.Lock();
            try
            {
                // バッファポインタを取得して、YUV→RGB 変換描画を行う
                byte* bufferPtr = (byte*)writeableBitmap.BackBuffer.ToPointer();
                int expectedBytes = frameConveter.ExpectedBufferSize(); // ← 後述
                int actualBytes = writeableBitmap.BackBufferStride * writeableBitmap.PixelHeight;

                if (expectedBytes > actualBytes)
                    throw new InvalidOperationException($"バッファサイズが不足しています。必要={expectedBytes}, 実際={actualBytes}");
                frameConveter.ConvertFrameDirect(frame, bufferPtr);

                //エフェクトの指定があれば、ビットマップに対して画像処理をかける　　　
                if (effect != EffectType.None)
                {
                    int stride = writeableBitmap.BackBufferStride;
                    int width = writeableBitmap.PixelWidth;
                    int height = writeableBitmap.PixelHeight;

                    switch (effect)
                    {
                        case EffectType.Posterize:
                            _effect.ApplyPosterize(bufferPtr, stride, width, height);
                            break;
                        case EffectType.Monochrome:
                            _effect.ApplyMonochrome(bufferPtr, stride, width, height);
                            break;
                    }
                }

                // 更新領域を明示的に指定して再描画を通知
                writeableBitmap.AddDirtyRect(rect);
            }
            finally
            {
                // アンロック処理は必ず実行する
                writeableBitmap.Unlock();
            }
        }

        /// <summary>
        /// デコード済みのフレームを WriteableBitmap に描画する。
        /// </summary>
        /// <param name="frame">描画するフレーム（デコード済み）</param>
        /// <param name="frameConveter">YUV→RGB 変換コンバータ</param>
        public unsafe void WriteFrame(AVFrame* frame, FrameConveter frameConveter)
        {
            // 引数の null チェック
            if (frame == null)
                throw new ArgumentNullException(nameof(frame), "フレームが null です");

            if (frameConveter == null)
                throw new ArgumentNullException(nameof(frameConveter), "フレーム変換器が null です");

            // WriteableBitmap のロックを行い、バッファポインタを取得
            writeableBitmap.Lock();
            try
            {
                // バッファポインタを取得して、YUV→RGB 変換描画を行う
                byte* bufferPtr = (byte*)writeableBitmap.BackBuffer.ToPointer();
                frameConveter.ConvertFrameDirect(frame, bufferPtr);

                // 更新領域を明示的に指定して再描画を通知
                writeableBitmap.AddDirtyRect(rect);
            }
            finally
            {
                // アンロック処理は必ず実行する
                writeableBitmap.Unlock();
            }

        }
    }
}
