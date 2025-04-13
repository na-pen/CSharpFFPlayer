using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;

namespace CSharpFFPlayer
{
    /// <summary>
    /// AVFrame を安全に管理し、必要に応じて GPU → CPU 転送を行うラッパークラスです。
    /// </summary>
    public unsafe class ManagedFrame : IDisposable
    {
        private AVFrame* frame;
        private bool isDisposed;

        /// <summary>
        /// 指定された AVFrame をラップして管理します。
        /// </summary>
        /// <param name="frame">管理対象の AVFrame*</param>
        public ManagedFrame(AVFrame* frame)
        {
            this.frame = frame;
        }

        /// <summary>
        /// 内部のオリジナルフレーム（GPU or CPU）を取得します。
        /// </summary>
        public AVFrame* Frame => frame;

        private readonly object transferLock = new(); //disposeとCPU転送が同時に呼ばれることを防ぐ
        private volatile bool isGpuFrame = true; //GPUフレームかどうか
        public bool IsGpuFrame => isGpuFrame;

        /// <summary>
        /// CPU 上に転送済みのフレームを取得します。
        /// 既に CPU 上ならそのまま返します。未転送の場合は転送を行います。
        /// </summary>
        public void GetCpuFrame()
        {
            // GPUでなければロック不要（高速パス）
            if (!isGpuFrame || frame == null)
                return;

            lock (transferLock)
            {
                if (frame == null) return;
                if (frame == null)
                    return;

                AVPixelFormat pixFmt = (AVPixelFormat)frame->format;

                bool isGPU = pixFmt == AVPixelFormat.AV_PIX_FMT_D3D11 ||
                             pixFmt == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                             pixFmt == AVPixelFormat.AV_PIX_FMT_QSV ||
                             pixFmt == AVPixelFormat.AV_PIX_FMT_CUDA ||
                             pixFmt == AVPixelFormat.AV_PIX_FMT_VAAPI;

                if (!isGPU)
                {
                    // 既にCPU上
                    isGpuFrame = false;
                    return;
                }

                AVFrame* swFrame = ffmpeg.av_frame_alloc();
                if (swFrame == null)
                    throw new InvalidOperationException("CPUフレーム用バッファの確保に失敗しました。");

                int ret = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
                if (ret < 0)
                {
                    ffmpeg.av_frame_free(&swFrame);
                    var errbuf = stackalloc byte[1024];
                    ffmpeg.av_strerror(ret, errbuf, 1024);
                    throw new InvalidOperationException($"GPUフレームからCPUへの転送に失敗しました: {Marshal.PtrToStringAnsi((nint)errbuf)}");
                }

                swFrame->width = frame->width;
                swFrame->height = frame->height;
                swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;

                AVFrame* temp = frame;
                ffmpeg.av_frame_free(&temp);
                frame = null;
                this.frame = swFrame;
                isGpuFrame = false; // 転送完了後にフラグを更新

                ConvertToNV12Self(); //フォーマット変換
            }
        }


        private void ConvertToNV12Self()
        {
            if (frame == null)
                throw new InvalidOperationException("変換対象フレームが null です。");

            AVPixelFormat currentFormat = (AVPixelFormat)frame->format;
            if (currentFormat == AVPixelFormat.AV_PIX_FMT_NV12)
                return;

            AVFrame* dst = ffmpeg.av_frame_alloc();
            if (dst == null)
                throw new Exception("出力フレームの確保に失敗しました。");

            dst->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            dst->width = frame->width;
            dst->height = frame->height;

            if (ffmpeg.av_frame_get_buffer(dst, 32) < 0)
            {
                ffmpeg.av_frame_free(&dst);
                throw new Exception("出力フレームバッファの確保に失敗しました。");
            }

            SwsContext* sws = ffmpeg.sws_getContext(
                frame->width, frame->height, currentFormat,
                dst->width, dst->height, AVPixelFormat.AV_PIX_FMT_NV12,
                1, null, null, null);

            if (sws == null)
            {
                ffmpeg.av_frame_free(&dst);
                throw new Exception("スケーラコンテキストの作成に失敗しました。");
            }

            int ret = ffmpeg.sws_scale(
                sws,
                frame->data,
                frame->linesize,
                0,
                frame->height,
                dst->data,
                dst->linesize);

            ffmpeg.sws_freeContext(sws);

            if (ret <= 0)
            {
                ffmpeg.av_frame_free(&dst);
                throw new Exception("sws_scale による NV12 変換に失敗しました。");
            }

            // 安全な上書き：中身をコピーし、dst のみ解放
            ffmpeg.av_frame_unref(frame); // 古い中身を解放
            *frame = *dst;                // 中身をコピー（メモリ所有権は frame が持つ）
            ffmpeg.av_frame_free(&dst);   // dst 本体を破棄（中身はコピー済み）
        }



        /// <summary>
        /// デストラクタ。Dispose が呼ばれていない場合に AVFrame を解放します。
        /// </summary>
        ~ManagedFrame()
        {
            Dispose(false);
        }

        /// <summary>
        /// AVFrame を解放します。
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose パターンに従ってリソースを解放します。
        /// </summary>
        /// <param name="disposing">マネージドリソースも解放するか（未使用）</param>
        private void Dispose(bool disposing)
        {
            lock (transferLock)
            {
                if (isDisposed)
                    return;

                if (frame != null)
                {
                    AVFrame* temp = frame;
                    ffmpeg.av_frame_free(&temp);
                    frame = null;
                }

                isDisposed = true;
            }
        }
    }
}
