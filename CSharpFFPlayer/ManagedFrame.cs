using FFmpeg.AutoGen;

namespace CSharpFFPlayer
{
    /// <summary>
    /// AVFrame を安全に管理するためのラッパークラスです。
    /// 明示的な Dispose 呼び出し、または GC によって確実にリソースを解放する。
    /// </summary>
    public unsafe class ManagedFrame : IDisposable
    {
        private AVFrame* frame;
        private bool isDisposed;

        /// <summary>
        /// 指定された AVFrame をラップして管理する。
        /// </summary>
        /// <param name="frame">解放対象の AVFrame ポインタ</param>
        public ManagedFrame(AVFrame* frame)
        {
            this.frame = frame;
        }

        /// <summary>
        /// 内部に保持している AVFrame ポインタを取得する。
        /// </summary>
        public AVFrame* Frame => frame;

        /// <summary>
        /// デストラクタ。Dispose が呼ばれていない場合に AVFrame を解放する。
        /// </summary>
        ~ManagedFrame()
        {
            Dispose(false);
        }

        /// <summary>
        /// 管理されている AVFrame を解放する。
        /// 明示的な解放が必要な場合はこのメソッドを使用してください。
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose パターンに基づき、AVFrame の解放処理を行う。
        /// </summary>
        /// <param name="disposing">マネージドリソースの解放を要求するかどうか（未使用）</param>
        private void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            if (frame != null)
            {
                // AVFrame の解放
                AVFrame* temp = frame;
                ffmpeg.av_frame_free(&temp);
                frame = null;
            }

            isDisposed = true;
        }
    }
}
