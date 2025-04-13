using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CSharpFFPlayer
{
    public class ImageWriter
    {
        private readonly Int32Rect rect;
        private readonly WriteableBitmap writeableBitmap;

        public ImageWriter(int width, int height, WriteableBitmap writeableBitmap)
        {
            rect = new Int32Rect(0, 0, width, height);
            this.writeableBitmap = writeableBitmap ?? throw new ArgumentNullException(nameof(writeableBitmap));
        }

        /// <summary>
        /// デコード済みのフレームを WriteableBitmap に描画します。
        /// </summary>
        /// <param name="frame">描画するフレーム</param>
        /// <param name="frameConveter">フレーム変換器</param>
        public unsafe void WriteFrame(ManagedFrame frame, FrameConveter frameConveter)
        {
            if (frame == null || frameConveter == null)
                throw new ArgumentNullException("frame または frameConveter が null です");

            writeableBitmap.Lock();
            try
            {
                byte* bufferPtr = (byte*)writeableBitmap.BackBuffer.ToPointer();
                frameConveter.ConvertFrameDirect(frame, bufferPtr);
                writeableBitmap.AddDirtyRect(rect);
            }
            finally
            {
                writeableBitmap.Unlock();
            }
        }
    }
}
