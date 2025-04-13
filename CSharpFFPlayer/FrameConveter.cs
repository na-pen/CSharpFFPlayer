using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpFFPlayer
{
    /// <summary>
    /// フレームを変換する機能を提供する。
    /// </summary>
    public unsafe class FrameConveter : IDisposable
    {
        public FrameConveter() { }

        private AVPixelFormat srcFormat;
        private int srcWidth;
        private int srcHeight;
        private AVPixelFormat distFormat;
        private int distWidth;
        private int distHeight;

        private SwsContext* swsContext;

        /// <summary>
        /// フレームの変換を設定する。
        /// </summary>
        /// <param name="srcFormat">変換元のフォーマット。</param>
        /// <param name="srcWidth">変換元の幅。</param>
        /// <param name="srcHeight">変換元の高さ。</param>
        /// <param name="distFormat">変換先のフォーマット。</param>
        /// <param name="distWidth">変換先の幅。</param>
        /// <param name="distHeight">変換先の高さ。</param>
        public unsafe void Configure(int srcWidth, int srcHeight, AVPixelFormat srcFormat,
                     int dstWidth, int dstHeight, AVPixelFormat dstFormat)
        {
            if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
                throw new InvalidOperationException("Configure に無効なサイズが渡されました。");

            if (srcFormat == AVPixelFormat.AV_PIX_FMT_NONE || dstFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new InvalidOperationException("無効なピクセルフォーマットが渡されました。");

            if (srcFormat == AVPixelFormat.AV_PIX_FMT_D3D11 ||
                srcFormat == AVPixelFormat.AV_PIX_FMT_QSV ||
                srcFormat == AVPixelFormat.AV_PIX_FMT_CUDA ||
                srcFormat == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                srcFormat == AVPixelFormat.AV_PIX_FMT_VAAPI)
                throw new InvalidOperationException("GPUフォーマットは直接 sws_getContext に使用できません。");

            this.srcWidth = srcWidth;
            this.srcHeight = srcHeight;
            this.srcFormat = srcFormat;
            this.distWidth = dstWidth;
            this.distHeight = dstHeight;
            this.distFormat = dstFormat;

            ffmpeg.sws_freeContext(swsContext);

            const int SWS_BICUBIC = 4;

            swsContext = ffmpeg.sws_getContext(
                srcWidth,
                srcHeight,
                srcFormat,
                dstWidth,
                dstHeight,
                dstFormat,
                SWS_BICUBIC,
                null, null, null);

            if (swsContext == null)
            {
                throw new InvalidOperationException("sws_getContext に失敗しました（対応していないピクセルフォーマットか、サイズが不正）");
            }
        }
        public int ExpectedBufferSize()
        {
            int bytesPerPixel = ffmpeg.av_get_bits_per_pixel(ffmpeg.av_pix_fmt_desc_get(distFormat)) / 8;
            return distWidth * distHeight * bytesPerPixel;
        }

        /// <summary>
        /// フレームを変換する。変換したフレームを指定したバッファに直接書き込む。
        /// </summary>
        /// <param name="frame"></param>
        /// <returns></returns>
        public unsafe void ConvertFrameDirect(ManagedFrame frame, nint buffer)
        {
            ConvertFrameDirect(frame.Frame, (byte*)buffer.ToPointer());
        }

        /// <summary>
        /// フレームを変換する。変換したフレームを指定したバッファに直接書き込む。
        /// </summary>
        /// <param name="frame"></param>
        /// <returns></returns>
        public unsafe void ConvertFrameDirect(ManagedFrame frame, byte* buffer)
        {
            ConvertFrameDirect(frame.Frame, buffer);
        }

        /// <summary>
        /// フレームを変換する。変換したフレームを指定したバッファに直接書き込む。
        /// </summary>
        /// <param name="frame"></param>
        /// <returns></returns>
        public unsafe void ConvertFrameDirect(AVFrame* frame, byte* buffer)
        {
            if (swsContext == null)
                throw new InvalidOperationException("SwsContext が初期化されていません。Configure() を先に呼び出してください。");

            if ((AVPixelFormat)frame->format != srcFormat)
                throw new InvalidOperationException($"入力フレームフォーマットが一致しません: 期待={srcFormat}, 実際={(AVPixelFormat)frame->format}");

            byte_ptrArray4 dstData = default;
            int_array4 dstLinesize = default;

            int result = ffmpeg.av_image_fill_arrays(
                ref dstData,
                ref dstLinesize,
                buffer,
                distFormat,
                distWidth,
                distHeight,
                1);

            if (result < 0)
            {
                throw new InvalidOperationException("出力バッファへの av_image_fill_arrays に失敗しました。");
            }

            int scaled = ffmpeg.sws_scale(
                swsContext,
                frame->data,
                frame->linesize,
                0,
                srcHeight,
                dstData,
                dstLinesize);

            if (scaled <= 0)
            {
                throw new InvalidOperationException("フレームのスケーリング（sws_scale）に失敗しました。");
            }
        }


        /// <inheritdoc />
        public void Dispose()
        {
            DisposeUnManaged();
            GC.SuppressFinalize(this);
        }

        ~FrameConveter()
        {
            DisposeUnManaged();
        }

        private bool isDisposed = false;
        private void DisposeUnManaged()
        {
            if (isDisposed) { return; }
            ffmpeg.sws_freeContext(swsContext);
            isDisposed = true;
        }
    }
}
