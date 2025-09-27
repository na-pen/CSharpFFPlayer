using FFmpeg.AutoGen;

public unsafe class ManagedFrame : IDisposable
{
    private AVFrame* frame;
    private bool isDisposed;

    public ManagedFrame(AVFrame* frame) { this.frame = frame; }

    public AVFrame* Frame => frame;

    private readonly object transferLock = new();
    private volatile bool isGpuFrame = true;
    public bool IsGpuFrame => isGpuFrame;

    public long Index = -1;

    /// <summary>
    /// GPUフレームならCPUへ転送する。既にCPUなら何もしない。
    /// 成功: true / 失敗: false
    /// </summary>
    public bool GetCpuFrame()
    {
        if (frame == null) return false;

        // すでにCPUなら即true
        if (!isGpuFrame)
            return true;

        lock (transferLock)
        {
            if (frame == null) return false;
            if (!isGpuFrame) return true;

            var pixFmt = (AVPixelFormat)frame->format;
            bool isGPU =
                pixFmt == AVPixelFormat.AV_PIX_FMT_D3D11 ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_QSV ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_CUDA ||
                pixFmt == AVPixelFormat.AV_PIX_FMT_VAAPI;

            if (!isGPU)
            {
                // 元からCPU
                isGpuFrame = false;
                return true;
            }

            // HW→SW 転送
            AVFrame* swFrame = ffmpeg.av_frame_alloc();
            if (swFrame == null)
                return false;

            int ret = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
            if (ret < 0)
            {
                ffmpeg.av_frame_free(&swFrame);
                return false;
            }

            // サイズ・タイミングの補完（転送で埋まっていることが多いが防御的に）
            swFrame->width = frame->width;
            swFrame->height = frame->height;
            swFrame->pts = (frame->pts != ffmpeg.AV_NOPTS_VALUE) ? frame->pts : frame->best_effort_timestamp;

            // 重要: ここで swFrame->format を「勝手に」書き換えない！
            // hw_frames_ctx->sw_format が正です。描画側で srcFormat に合わせて変換してください。

            // 旧フレームを解放して置き換え
            AVFrame* old = frame;
            ffmpeg.av_frame_free(&old);
            frame = swFrame;

            isGpuFrame = false;
            return true;
        }
    }

    ~ManagedFrame() { Dispose(false); }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        lock (transferLock)
        {
            if (isDisposed) return;
            if (frame != null)
            {
                try
                { 
                    fixed (AVFrame** framePtr = &frame) 
                    {
                        ffmpeg.av_frame_free(framePtr);
                    } 
                    frame = null; 
                }
                catch (AccessViolationException ex) 
                { 
                    Console.WriteLine($"[Dispose Error] AVFrame 解放中にアクセス違反: {ex.Message}");
                    frame = null; 
                }
                catch (Exception ex) 
                { 
                    Console.WriteLine($"[Dispose Error] 例外: {ex.Message}");
                    frame = null; 
                }
            }
            isDisposed = true;
        }
    }
}
