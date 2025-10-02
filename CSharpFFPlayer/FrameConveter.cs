using FFmpeg.AutoGen;
using System;

namespace CSharpFFPlayer
{
    /// <summary>フレームを変換する（HW→SW済みのCPUフレーム専用）</summary>
    public unsafe class FrameConveter : IDisposable
    {
        private AVPixelFormat srcFormat;
        private int srcWidth;
        private int srcHeight;

        private AVPixelFormat dstFormat;
        private int dstWidth;
        private int dstHeight;

        private int dstStride;                 // WriteableBitmap.BackBufferStride
        private SwsContext* swsContext;

        public void SetDestinationStride(int stride)
        {
            if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));
            dstStride = stride;
        }

        public void Configure(int srcW, int srcH, AVPixelFormat srcFmt,
                              int dstW, int dstH, AVPixelFormat dstFmt)
        {
            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
                throw new InvalidOperationException("Configure に無効なサイズが渡されました。");
            if (srcFmt == AVPixelFormat.AV_PIX_FMT_NONE || dstFmt == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new InvalidOperationException("無効なピクセルフォーマットです。");
            if (IsHwFormat(srcFmt))
                throw new InvalidOperationException("GPUフォーマットは直接 sws に渡せません（先に CPU へ転送してください）。");

            srcWidth = srcW;
            srcHeight = srcH;
            srcFormat = srcFmt;

            dstWidth = dstW;
            dstHeight = dstH;
            dstFormat = dstFmt;

            RecreateSws();
        }

        private static bool IsHwFormat(AVPixelFormat f) =>
            f == AVPixelFormat.AV_PIX_FMT_D3D11 ||
            f == AVPixelFormat.AV_PIX_FMT_QSV ||
            f == AVPixelFormat.AV_PIX_FMT_CUDA ||
            f == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
            f == AVPixelFormat.AV_PIX_FMT_VAAPI;

        private void RecreateSws()
        {
            // sws_getCachedContext を使うと安全（古い ctx を内部で再利用/解放）
            swsContext = ffmpeg.sws_getCachedContext(
                swsContext,
                srcWidth, srcHeight, srcFormat,
                dstWidth, dstHeight, dstFormat,
                (int)SwsFlags.SWS_BICUBIC, null, null, null);

            if (swsContext == null)
                throw new InvalidOperationException("sws_getCachedContext に失敗しました。");
        }

        /// <summary>入力フレームの src 条件が変わったら sws を再設定</summary>
        private void EnsureSource(AVFrame* frame)
        {
            var fmt = (AVPixelFormat)frame->format;

            // HWフレームは受け付けない（必ず CPU へ転送済みにしてから呼ぶ）
            var desc = ffmpeg.av_pix_fmt_desc_get(fmt);
            if ((desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0)
                throw new InvalidOperationException("HWフレームは ConvertFrameDirect に渡せません。");

            if (fmt == srcFormat && frame->width == srcWidth && frame->height == srcHeight)
                return;

            srcFormat = fmt;
            srcWidth = frame->width;
            srcHeight = frame->height;

            RecreateSws();
        }

        public void ConvertFrameDirect(ManagedFrame frame, byte* buffer) =>
            ConvertFrameDirect(frame.Frame, buffer);

        public void ConvertFrameDirect(AVFrame* frame, byte* buffer)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (swsContext == null)
                throw new InvalidOperationException("Configure 後に呼び出してください。");
            if (dstStride <= 0)
                throw new InvalidOperationException("SetDestinationStride(writeableBitmap.BackBufferStride) を先に呼び出してください。");

            // 入力に合わせて sws を再設定（解像度/フォーマット変化へ追従）
            EnsureSource(frame);

            if (frame->data[0] == null)
            {
                Console.WriteLine("[Warn] frame->data[0] が null のためスキップ");
                return;
            }

            // 出力平面（BGR24 など packed は plane 0 のみ）
            byte_ptrArray4 dstData = default;
            int_array4 dstLinesize = default;
            dstData[0] = buffer;
            dstLinesize[0] = dstStride;

            // 実フレームの高さを使う（srcHeight でも同値になるが、こちらが安全）
            int inHeight = frame->height;

            int scaled = ffmpeg.sws_scale(
                swsContext,
                frame->data,
                frame->linesize,
                0,
                inHeight,
                dstData,
                dstLinesize);

            if (scaled <= 0)
                Console.WriteLine("[Warn] sws_scale が失敗しました（scaled={0})", scaled);
        }

        public void Dispose()
        {
            ffmpeg.sws_freeContext(swsContext);
            swsContext = null;
        }
    }
}
