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
        private AVPixelFormat srcFormat;
        private int srcWidth;
        private int srcHeight;
        private AVPixelFormat distFormat;
        private int distWidth;
        private int distHeight;

        private int dstStride;              // ★ 追加: 出力ストライド（WPFのBackBufferStride）
        private SwsContext* swsContext;

        public void SetDestinationStride(int stride) => dstStride = stride; // ★ 追加

        public unsafe void Configure(int srcWidth, int srcHeight, AVPixelFormat srcFormat,
                                     int dstWidth, int dstHeight, AVPixelFormat dstFormat)
        {
            if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
                throw new InvalidOperationException("Configure に無効なサイズが渡されました。");

            if (srcFormat == AVPixelFormat.AV_PIX_FMT_NONE || dstFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new InvalidOperationException("無効なピクセルフォーマットが渡されました。");

            if (IsHwFormat(srcFormat))
                throw new InvalidOperationException("GPUフォーマットは直接 sws_getContext に使用できません。");

            this.srcWidth = srcWidth;
            this.srcHeight = srcHeight;
            this.srcFormat = srcFormat;
            this.distWidth = dstWidth;
            this.distHeight = dstHeight;
            this.distFormat = dstFormat;

            RecreateSws();
        }

        private static bool IsHwFormat(AVPixelFormat f) =>
            f == AVPixelFormat.AV_PIX_FMT_D3D11
            || f == AVPixelFormat.AV_PIX_FMT_QSV
            || f == AVPixelFormat.AV_PIX_FMT_CUDA
            || f == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD
            || f == AVPixelFormat.AV_PIX_FMT_VAAPI;

        private void RecreateSws()
        {
            ffmpeg.sws_freeContext(swsContext);
            const int SWS_BICUBIC = 4;
            swsContext = ffmpeg.sws_getContext(
                srcWidth, srcHeight, srcFormat,
                distWidth, distHeight, distFormat,
                SWS_BICUBIC,
                null, null, null);

            if (swsContext == null)
                throw new InvalidOperationException("sws_getContext に失敗しました。");
        }

        // 変換元が変わったら即リコンフィグ
        private void EnsureSource(AVFrame* frame)
        {
            var fmt = (AVPixelFormat)frame->format;
            if (fmt == srcFormat && frame->width == srcWidth && frame->height == srcHeight)
                return;

            if (IsHwFormat(fmt))
                throw new InvalidOperationException("GPUフォーマットは ConvertFrameDirect に渡せません。");

            srcFormat = fmt;
            srcWidth = frame->width;
            srcHeight = frame->height;
            RecreateSws();
        }

        public unsafe void ConvertFrameDirect(ManagedFrame frame, byte* buffer) =>
            ConvertFrameDirect(frame.Frame, buffer);

        public unsafe void ConvertFrameDirect(AVFrame* frame, byte* buffer)
        {
            if (swsContext == null)
                throw new InvalidOperationException("SwsContext が初期化されていません。Configure() を先に呼び出してください。");

            // ★ 実フレームに合わせて動的に再設定（GPU→CPU後のNV12/P010/YUV420Pなどに追従）
            EnsureSource(frame);

            byte_ptrArray4 dstData = default;
            int_array4 dstLinesize = default;

            // ★ av_image_fill_arrays を使わない（WPFは行パディングあり）
            dstData[0] = buffer;
            dstLinesize[0] = dstStride > 0 ? dstStride : distWidth * (ffmpeg.av_get_bits_per_pixel(ffmpeg.av_pix_fmt_desc_get(distFormat)) / 8);
            dstData[1] = null; dstData[2] = null; dstData[3] = null;
            dstLinesize[1] = 0; dstLinesize[2] = 0; dstLinesize[3] = 0;

            int scaled = ffmpeg.sws_scale(
                swsContext,
                frame->data,
                frame->linesize,
                0,
                srcHeight,
                dstData,
                dstLinesize);

            if (scaled <= 0)
                Console.WriteLine("[警告] sws_scale が 0 を返しました（スキップ）");
        }
        public unsafe byte[] ConvertFrameToArray(ManagedFrame frame)
        {
            switch (frame.HwDeviceType)
            {
                case AVHWDeviceType.AV_HWDEVICE_TYPE_NONE:
                    {
                        int bpp = ffmpeg.av_get_bits_per_pixel(ffmpeg.av_pix_fmt_desc_get(distFormat)) / 8;
                        int stride = distWidth * bpp;
                        int bufferSize = stride * distHeight;
                        byte[] buffer = new byte[bufferSize];

                        fixed (byte* dst = buffer)
                        {
                            ConvertFrameDirect(frame.Frame, dst);
                        }

                        return buffer;
                    }
                case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                    {
                        // ==== CUDAフレーム ====

                        frame.GetCpuFrame();

                        int bpp = ffmpeg.av_get_bits_per_pixel(ffmpeg.av_pix_fmt_desc_get(distFormat)) / 8;
                        int stride = distWidth * bpp;
                        int bufferSize = stride * distHeight;
                        byte[] buffer = new byte[bufferSize];

                        fixed (byte* dst = buffer)
                        {
                            ConvertFrameDirect(frame.Frame, dst);
                        }

                        return buffer;

                        // 案2: GPU→D3D経路に直結させたい場合はここに BlitCUDAFrameToD3D() を呼ぶ
                        // return Array.Empty<byte>();
                    }
                default:
                    throw new NotSupportedException($"Unsupported hardware type: {frame.HwDeviceType}");
            }
        }

        public void Dispose()
        {
            ffmpeg.sws_freeContext(swsContext);
            swsContext = null;
        }
    }

}
