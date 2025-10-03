using FFmpeg.AutoGen;
using System;
using System.Buffers;
using System.Diagnostics;

namespace CSharpFFPlayer
{
    /// <summary>
    /// FFmpeg の CPU フレームを任意フォーマットへ変換する薄いユーティリティ。
    /// - GPU (CUDA/D3D11/QSV/VAAPI/DXVA2) フォーマットは直接扱わない（GPU→CPUは外で行う）。
    /// - WPF の WriteableBitmap など「行パディング有り」のバッファへも直接書き込めるよう、
    ///   出力ストライド (BackBufferStride) を指定可能。
    /// </summary>
    public unsafe class FrameConveter : IDisposable
    {
        // 入力(=変換元)の現在値
        private AVPixelFormat _srcFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private int _srcWidth;
        private int _srcHeight;

        // 出力(=変換先)の固定値
        private AVPixelFormat _dstFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private int _dstWidth;
        private int _dstHeight;
        private int _dstStride; // WPF の BackBufferStride を渡す。0 の場合は幅×Bpp を自動使用。

        private SwsContext* _sws; // libswscale コンテキスト

        /// <summary>
        /// 変換処理などの詳細ログを出すか。フレーム毎のログが多くなるため通常は false 推奨。
        /// </summary>
        public bool VerboseLog { get; set; } = false;

        #region public API

        /// <summary>
        /// 出力先ストライド（例: WriteableBitmap.BackBufferStride）を設定。
        /// 0 を渡すと「幅 × Bpp」を自動使用。
        /// </summary>
        public void SetDestinationStride(int stride)
        {
            _dstStride = stride;
            Log($"[Configure] dstStride={_dstStride}");
        }

        /// <summary>
        /// 変換設定を行う。入力→出力のフォーマット・解像度を固定する。
        /// 入力側はフレーム実体に応じて変わりうるため、実行時に差異が出た場合は内部で自動更新される。
        /// ※入力に HW 由来フォーマットは指定不可（GPU→CPU 化してから渡してください）
        /// </summary>
        public void Configure(
            int srcWidth, int srcHeight, AVPixelFormat srcFormat,
            int dstWidth, int dstHeight, AVPixelFormat dstFormat)
        {
            if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
                throw new InvalidOperationException("Configure: 無効なサイズが渡されました。");

            if (srcFormat == AVPixelFormat.AV_PIX_FMT_NONE || dstFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new InvalidOperationException("Configure: 無効なピクセルフォーマットが渡されました。");

            if (IsHwFormat(srcFormat))
                throw new InvalidOperationException("Configure: 入力に GPU フォーマットは指定できません。");

            _srcWidth = srcWidth;
            _srcHeight = srcHeight;
            _srcFormat = srcFormat;

            _dstWidth = dstWidth;
            _dstHeight = dstHeight;
            _dstFormat = dstFormat;

            RecreateOrCacheSws();
            Log($"[Configure] src={_srcWidth}x{_srcHeight}/{_srcFormat} -> dst={_dstWidth}x{_dstHeight}/{_dstFormat}");
        }

        /// <summary>
        /// 変換元 ManagedFrame を、指定ネイティブバッファ（先頭ポインタ）へダイレクト書き込み。
        /// 出力先ストライドは SetDestinationStride で事前指定しておく。
        /// </summary>
        public void ConvertFrameDirect(ManagedFrame frame, byte* dstBuffer)
        {
            if (frame is null) throw new ArgumentNullException(nameof(frame));
            ConvertFrameDirect(frame.Frame, dstBuffer);
        }

        /// <summary>
        /// 変換元 AVFrame* を、指定ネイティブバッファ（先頭ポインタ）へダイレクト書き込み。
        /// 出力先ストライドは SetDestinationStride で事前指定しておく。
        /// ※GPU フォーマットは不可。GPU→CPU 化してから渡すこと。
        /// </summary>
        public void ConvertFrameDirect(AVFrame* frame, byte* dstBuffer)
        {
            GuardConfigured();
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (dstBuffer == null) throw new ArgumentNullException(nameof(dstBuffer));

            // 入力 (src) が事前設定と異なる場合に追従（たとえば NV12→YUV420P に変わった等）
            EnsureSourceFrom(frame);

            // 出力バッファの行ストライド（未指定なら幅×Bpp）
            int bpp = BytesPerPixel(_dstFormat);
            int dstStrideUse = _dstStride > 0 ? _dstStride : _dstWidth * bpp;

            // 出力プレーンを1面だけ使う（BGRA/ARGB などパックドを想定）
            byte_ptrArray4 dstData = default;
            int_array4 dstLinesize = default;
            dstData[0] = dstBuffer;
            dstLinesize[0] = dstStrideUse;

            int scaled = ffmpeg.sws_scale(
                _sws,
                frame->data,
                frame->linesize,
                0,
                _srcHeight,
                dstData,
                dstLinesize);

            if (scaled <= 0)
            {
                Warn($"sws_scale が {scaled} を返却。srcH={_srcHeight} srcFmt={_srcFormat} dstFmt={_dstFormat}");
            }
            else if (VerboseLog)
            {
                Log($"[sws] scale OK rows={scaled} src={_srcWidth}x{_srcHeight}/{_srcFormat} -> dst={_dstWidth}x{_dstHeight}/{_dstFormat} stride={dstStrideUse}");
            }
        }

        /// <summary>
        /// 変換元 ManagedFrame を配列化して返す（毎回新規 byte[] を作る版）。
        /// ※GC 圧縮を避けたい場合は ArrayPool を使うオーバーロードを利用してください。
        /// </summary>
        public byte[] ConvertFrameToArray(ManagedFrame frame)
        {
            if (frame is null) throw new ArgumentNullException(nameof(frame));

            switch (frame.HwDeviceType)
            {
                case AVHWDeviceType.AV_HWDEVICE_TYPE_NONE:
                    return ConvertCpuFrameToArray(frame.Frame);

                case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                    // ★ 注意：このクラスは GPU→CPU の転送は行わない方針。
                    //        ここで CPU 化を強制するのは実用上便利なので残す（呼び出し側のポリシーに合わせて外しても良い）。
                    frame.GetCpuFrame();
                    return ConvertCpuFrameToArray(frame.Frame);

                default:
                    throw new NotSupportedException($"ConvertFrameToArray: Unsupported HW type {frame.HwDeviceType}");
            }
        }

        /// <summary>
        /// 再利用配列( ArrayPool )を使って変換先を書き込みたい場合のオーバーロード。
        /// 戻り値：実際に使用した配列（呼び出し側で ArrayPool に返却すること）。
        /// </summary>
        public byte[] ConvertFrameToArray(ManagedFrame frame, ArrayPool<byte> pool, out int lengthUsed)
        {
            if (frame is null) throw new ArgumentNullException(nameof(frame));
            if (pool is null) throw new ArgumentNullException(nameof(pool));

            frame.GetCpuFrame(); // 必要なら CPU 化（方針に応じて外しても良い）

            int bpp = BytesPerPixel(_dstFormat);
            int stride = _dstWidth * bpp;        // 配列版は「幅×Bpp」ピッチで作る
            int size = stride * _dstHeight;

            var buffer = pool.Rent(size);
            fixed (byte* p = buffer)
            {
                // 一時的に出力ストライドを「幅×Bpp」に固定して Direct に流用
                int old = _dstStride;
                _dstStride = stride;
                try
                {
                    ConvertFrameDirect(frame.Frame, p);
                }
                finally
                {
                    _dstStride = old;
                }
            }
            lengthUsed = size;
            return buffer;
        }

        #endregion

        #region internals

        private static bool IsHwFormat(AVPixelFormat f) =>
               f == AVPixelFormat.AV_PIX_FMT_D3D11
            || f == AVPixelFormat.AV_PIX_FMT_QSV
            || f == AVPixelFormat.AV_PIX_FMT_CUDA
            || f == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD
            || f == AVPixelFormat.AV_PIX_FMT_VAAPI;

        private void GuardConfigured()
        {
            if (_dstFormat == AVPixelFormat.AV_PIX_FMT_NONE || _dstWidth <= 0 || _dstHeight <= 0)
                throw new InvalidOperationException("FrameConveter: Configure() が未完了です。");
        }

        /// <summary>
        /// フレームの実フォーマット／サイズが現在の入力設定と違う場合、コンテキストを再設定する。
        /// </summary>
        private void EnsureSourceFrom(AVFrame* frame)
        {
            var fmt = (AVPixelFormat)frame->format;
            if (IsHwFormat(fmt))
                throw new InvalidOperationException("GPUフォーマットの AVFrame は ConvertFrameDirect に渡せません（CPU 化してから渡してください）。");

            if (fmt == _srcFormat && frame->width == _srcWidth && frame->height == _srcHeight)
                return; // 変更なし

            _srcFormat = fmt;
            _srcWidth = frame->width;
            _srcHeight = frame->height;

            RecreateOrCacheSws();
            Log($"[AutoSource] src 変化により sws を更新: src={_srcWidth}x{_srcHeight}/{_srcFormat}");
        }

        /// <summary>
        /// sws_getCachedContext を使って、コンテキストを新規作成または再利用。
        /// </summary>
        private void RecreateOrCacheSws()
        {
            const int SWS_BICUBIC = 4; // 品質優先。必要ならパラメタ化してください。

            var before = _sws;
            _sws = ffmpeg.sws_getCachedContext(
                before,
                _srcWidth, _srcHeight, _srcFormat,
                _dstWidth, _dstHeight, _dstFormat,
                SWS_BICUBIC,
                null, null, null);

            if (_sws == null)
                throw new InvalidOperationException("sws_getCachedContext に失敗しました。");

            if (before == null)
                Log("[sws] context created.");
            else if (before != _sws)
                Log("[sws] context re-allocated (params changed).");
            else if (VerboseLog)
                Log("[sws] context reused.");
        }

        /// <summary>CPUフレームを byte[] に変換（幅×Bpp ピッチで作成）</summary>
        private byte[] ConvertCpuFrameToArray(AVFrame* frame)
        {
            GuardConfigured();
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            // 出力は「幅×Bpp」ピッチの連続配列として確保
            int bpp = BytesPerPixel(_dstFormat);
            int stride = _dstWidth * bpp;
            int size = stride * _dstHeight;

            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                // 一時的に出力ストライドを「幅×Bpp」に固定して Direct に流用
                int old = _dstStride;
                _dstStride = stride;
                try
                {
                    ConvertFrameDirect(frame, p);
                }
                finally
                {
                    _dstStride = old;
                }
            }
            return buffer;
        }

        private static int BytesPerPixel(AVPixelFormat fmt)
        {
            var desc = ffmpeg.av_pix_fmt_desc_get(fmt);
            if (desc == null) throw new ArgumentOutOfRangeException(nameof(fmt), $"Unsupported format: {fmt}");
            int bits = ffmpeg.av_get_bits_per_pixel(desc);
            if (bits % 8 != 0)
                throw new NotSupportedException($"非バイト境界のピクセル深度は未対応: {fmt} ({bits} bits/pixel)");
            return bits / 8;
        }

        #endregion

        #region logging

        [Conditional("DEBUG")]
        private static void Log(string msg)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [FrameConv] {msg}");
        }

        [Conditional("DEBUG")]
        private static void Warn(string msg)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [FrameConv][Warn] {msg}");
        }

        #endregion

        #region IDisposable

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
                Log("[sws] freed.");
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~FrameConveter()
        {
            try { Dispose(); } catch { /* finalize は例外を投げない */ }
        }

        #endregion
    }
}
