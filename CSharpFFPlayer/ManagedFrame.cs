using FFmpeg.AutoGen;

namespace CSharpFFPlayer
{
    /// <summary>
    /// ラップされた <see cref="AVFrame"/> を表現します。解放漏れを防止します。
    /// </summary>
    public unsafe class ManagedFrame : IDisposable
    {
        private AVFrame* frame;
        private bool isDisposed = false;

        public ManagedFrame(AVFrame* frame)
        {
            this.frame = frame;
        }

        public AVFrame* Frame => frame;

        ~ManagedFrame()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Dispose pattern implementation
        private void Dispose(bool disposing)
        {
            if (isDisposed) return;

            if (frame != null)
            {
                AVFrame* temp = frame;
                ffmpeg.av_frame_free(&temp);
                frame = null;
            }

            isDisposed = true;
        }

        public int Width => frame->width;
        public int Height => frame->height;
        public AVPixelFormat PixelFormat => (AVPixelFormat)frame->format;

        public long EstimatedSizeInBytes
        {
            get
            {
                int bytesPerPixel = PixelFormat switch
                {
                    AVPixelFormat.AV_PIX_FMT_RGBA => 4,
                    AVPixelFormat.AV_PIX_FMT_RGB24 => 3,
                    AVPixelFormat.AV_PIX_FMT_YUV420P => 1, // Yは全体、UVは1/4ずつ
                    AVPixelFormat.AV_PIX_FMT_YUV444P => 3,
                    AVPixelFormat.AV_PIX_FMT_YUV420P10LE => 2, // 10bit planar
                    AVPixelFormat.AV_PIX_FMT_YUV444P10LE => 6,
                    _ => 4 // 仮置き
                };

                return (long)Width * Height * bytesPerPixel;
            }
        }
    }
}
