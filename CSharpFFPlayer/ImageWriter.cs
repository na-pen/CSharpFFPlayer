using FFmpeg.AutoGen;
using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CSharpFFPlayer
{
    public class ImageWriter : IDisposable
    {
        private readonly WriteableBitmap writeableBitmap;
        private readonly Int32Rect rect;
        private readonly FrameConveter frameConveter;

        private readonly ConcurrentQueue<ManagedFrame> renderQueue = new();
        private bool isRenderPending = false; // UIスレッドに描画要求を出したかどうか

        public ImageWriter(int width, int height, WriteableBitmap writeableBitmap, FrameConveter frameConveter)
        {
            this.writeableBitmap = writeableBitmap ?? throw new ArgumentNullException(nameof(writeableBitmap));
            this.frameConveter = frameConveter ?? throw new ArgumentNullException(nameof(frameConveter));

            rect = new Int32Rect(0, 0, width, height);
            frameConveter.SetDestinationStride(writeableBitmap.BackBufferStride);
        }

        /// <summary>
        /// フレーム到着時に即描画要求を出す
        /// </summary>
        public void EnqueueFrame(ManagedFrame newFrame)
        {
            // 古いフレームを破棄して最新のみ保持
            while (renderQueue.TryDequeue(out var old))
                old.Dispose();

            renderQueue.Enqueue(newFrame);

            // UIスレッドに描画要求（多重呼び出し防止）
            if (!isRenderPending)
            {
                isRenderPending = true;
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(RenderLatestFrame),
                    System.Windows.Threading.DispatcherPriority.Render
                );
            }
        }

        private void RenderLatestFrame()
        {
            isRenderPending = false;

            if (!renderQueue.TryDequeue(out var latest))
                return;

            try
            {
                if (latest.IsGpuFrame)
                {
                    unsafe
                    {
                        latest.GetCpuFrame();
                        if (latest.Frame == null)
                        {
                            // GPU→CPU 転送失敗なら破棄して return
                            return;
                        }
                    }
                }

                writeableBitmap.Lock();
                try
                {
                    unsafe
                    {
                        byte* bufferPtr = (byte*)writeableBitmap.BackBuffer.ToPointer();
                        frameConveter.ConvertFrameDirect(latest, bufferPtr);
                        writeableBitmap.AddDirtyRect(rect);
                    }
                }
                finally
                {
                    writeableBitmap.Unlock();
                }
            }
            finally
            {
                latest.Dispose();

                // 次が残っていれば再度描画を要求
                if (!renderQueue.IsEmpty)
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        new Action(RenderLatestFrame),
                        System.Windows.Threading.DispatcherPriority.Render
                    );
                }
            }
        }


        public void ClearQueue()
        {
            while (renderQueue.TryDequeue(out var frame))
                frame.Dispose();
        }

        public void Dispose()
        {
            ClearQueue();
        }
    }
}
