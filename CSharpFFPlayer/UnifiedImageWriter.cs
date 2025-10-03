using FFmpeg.AutoGen;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using ManagedCuda.VectorTypes;
using SharpDX;
using SharpDX.Direct3D9;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CSharpFFPlayer
{
    public enum RenderTargetType
    {
        WriteableBitmap,
        D3DImage
    }

    public class UnifiedImageWriter : IDisposable
    {
        private readonly RenderTargetType targetType;
        private readonly WriteableBitmap? writeableBitmap;
        private readonly D3DImage? d3dImage;
        private readonly Int32Rect rect;
        private readonly FrameConveter frameConveter;

        private readonly ConcurrentQueue<ManagedFrame> renderQueue = new();
        private bool isRenderPending = false;

        // D3D9Ex 関連
        private Direct3DEx? d3d;
        private DeviceEx? device;
        private Texture? texture;
        private Surface? surface;
        private readonly int width;
        private readonly int height;

        // フィールド
        private Texture d3d9SharedTex;
        private Surface d3d9SharedSurf;
        private IntPtr sharedHandle;
        private CUgraphicsResource cudaResource;
        private CudaContext cudaCtx;
        private CudaKernel kNv12ToBgra;
        private CudaDeviceVariable<byte> d_bgra;  // 出力バッファ (width*height*4)

        public UnifiedImageWriter(RenderTargetType targetType,
                                  int width,
                                  int height,
                                  FrameConveter frameConveter,
                                  WriteableBitmap? wb = null,
                                  D3DImage? di = null)
        {
            this.targetType = targetType;
            this.width = width;
            this.height = height;
            this.frameConveter = frameConveter ?? throw new ArgumentNullException(nameof(frameConveter));

            rect = new Int32Rect(0, 0, width, height);

            if (targetType == RenderTargetType.WriteableBitmap)
            {
                writeableBitmap = wb ?? throw new ArgumentNullException(nameof(wb));
                frameConveter.SetDestinationStride(writeableBitmap.BackBufferStride);
            }
            else if (targetType == RenderTargetType.D3DImage)
            {
                d3dImage = di ?? throw new ArgumentNullException(nameof(di));

                using var ctx = new CudaContext(0);  // GPU0 を使う
                var props = ctx.GetDeviceInfo();
                InitD3D9();
                Console.WriteLine($"Device: {props.DeviceName}");
                Console.WriteLine($"Compute Capability: {props.ComputeCapability.Major}.{props.ComputeCapability.Minor}");
                Console.WriteLine($"Total Global Memory: {props.TotalGlobalMemory / (1024 * 1024)} MB");
            }
        }
        private void InitD3D9()
        {
            d3d = new Direct3DEx();
            var pp = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = PresentInterval.Default
            };

            device = new DeviceEx(d3d, 0, DeviceType.Hardware, IntPtr.Zero,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, pp);

            // システムメモリにコピー後、UpdateSurface() で使うテクスチャ
            d3d9SharedTex = new Texture(device, width, height, 1,
                Usage.RenderTarget, Format.A8R8G8B8, Pool.Default, ref sharedHandle);

            d3d9SharedSurf = d3d9SharedTex.GetSurfaceLevel(0);

            // D3DImage に紐付け
            d3dImage!.Lock();
            d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, d3d9SharedSurf.NativePointer);
            d3dImage.Unlock();
        }

        public void EnqueueFrame(ManagedFrame newFrame)
        {
            while (renderQueue.TryDequeue(out var old))
                old.Dispose();

            renderQueue.Enqueue(newFrame);

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
                if (targetType == RenderTargetType.WriteableBitmap)
                {
                    if (latest.IsGpuFrame)
                    {
                        unsafe { latest.GetCpuFrame(); }
                        unsafe { if (latest.Frame == null) return; }
                    }
                    RenderToWriteableBitmap(latest);
                }
            }
            finally
            {
                latest.Dispose();

                if (!renderQueue.IsEmpty)
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        new Action(RenderLatestFrame),
                        System.Windows.Threading.DispatcherPriority.Render
                    );
                }
            }
        }

        private void RenderToWriteableBitmap(ManagedFrame frame)
        {
            writeableBitmap!.Lock();
            unsafe
            {
                byte* bufferPtr = (byte*)writeableBitmap.BackBuffer.ToPointer();
                frameConveter.ConvertFrameDirect(frame, bufferPtr);
                writeableBitmap.AddDirtyRect(rect);
            }
            writeableBitmap.Unlock();
        }


        private bool d3dBackBufferSetOnce = false;
        // フィールドを追加
        private readonly object _presentLock = new object();
        private volatile bool _presentScheduled = false;

        // 「最新」フレームだけ保持（古いのは上書き＝参照解放）
        private byte[]? _latestBgra;
        private Action<byte[]>? _onConsumed; // ArrayPool 返却などをしたい場合に使う（任意）

        // 再利用用のシステムメモリテクスチャ（D3DImage用）
        private Texture? _sysTexReuse;
        private Surface? _sysSurfReuse;

        private void EnsureSysmemTexture()
        {
            if (_sysTexReuse != null && !_sysTexReuse.IsDisposed) return;
            _sysSurfReuse?.Dispose();
            _sysTexReuse?.Dispose();
            _sysTexReuse = new Texture(device, width, height, 1,
                                       Usage.Dynamic, Format.A8R8G8B8, Pool.SystemMemory);
            _sysSurfReuse = _sysTexReuse.GetSurfaceLevel(0);
        }

        private unsafe void CopyToWriteableBitmap(byte[] src)
        {
            writeableBitmap!.Lock();
            try
            {
                int dstStride = writeableBitmap.BackBufferStride;
                int srcStride = width * 4;
                int rows = height;
                IntPtr dst = writeableBitmap.BackBuffer;

                if (dstStride == srcStride)
                {
                    Buffer.MemoryCopy(
                        source: System.Runtime.CompilerServices.Unsafe.AsPointer(ref src[0]),
                        destination: dst.ToPointer(),
                        destinationSizeInBytes: dstStride * rows,
                        sourceBytesToCopy: srcStride * rows);
                }
                else
                {
                    // 行ごとコピー（パディング対応）
                    int copy = Math.Min(dstStride, srcStride);
                    fixed (byte* pSrc0 = src)
                    {
                        byte* pSrc = pSrc0;
                        byte* pDst = (byte*)dst.ToPointer();
                        for (int y = 0; y < rows; y++)
                        {
                            Buffer.MemoryCopy(pSrc, pDst, dstStride, copy);
                            pSrc += srcStride;
                            pDst += dstStride;
                        }
                    }
                }
                writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                writeableBitmap.Unlock();
            }
        }

        // 合流描画用
        private bool _presentPending = false;
        private byte[]? _pendingBgra;
        private Action<byte[]>? _pendingOnConsumed;


        public void PresentBgra(byte[] bgra, Action<byte[]>? onConsumed = null)
        {
            // 最新だけ保持。すでに保留があるなら古い方は返却（破棄）して上書き。
            lock (_presentLock)
            {
                if (_pendingBgra != null && _pendingBgra != bgra)
                    _pendingOnConsumed?.Invoke(_pendingBgra);

                _pendingBgra = bgra;
                _pendingOnConsumed = onConsumed;

                if (_presentPending) return;          // すでにUIに投げ済み
                _presentPending = true;
            }

            // 一回だけUIスレッドへ。処理後にまだ最新があればもう一回だけ投げ直す。
            Application.Current.Dispatcher.BeginInvoke((Action)RenderLatestBgra, System.Windows.Threading.DispatcherPriority.Render);
        }

        private void RenderLatestBgra()
        {
            byte[]? current;
            Action<byte[]>? cb;

            // 最新を引き当て、スロットを空にする
            lock (_presentLock)
            {
                current = _pendingBgra;
                cb = _pendingOnConsumed;
                _pendingBgra = null;
                _pendingOnConsumed = null;
            }

            try
            {
                if (current == null) return; // 競合で消えた

                if (targetType == RenderTargetType.WriteableBitmap)
                {
                    CopyToWriteableBitmap(current);
                }
                else // D3DImage
                {
                    // 必要ならデバイス復活
                    if (device == null || device.IsDisposed || d3d9SharedSurf == null || d3d9SharedSurf.IsDisposed)
                        InitD3D9();

                    EnsureSysmemTexture();

                    var lr = _sysTexReuse!.LockRectangle(0, LockFlags.Discard);
                    Marshal.Copy(current, 0, lr.DataPointer, current.Length);
                    _sysTexReuse.UnlockRectangle(0);

                    device!.UpdateSurface(_sysSurfReuse!, null, d3d9SharedSurf, null);

                    d3dImage!.Lock();
                    d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                    d3dImage.Unlock();
                }
            }
            finally
            {
                // 配列の返却があればここで
                cb?.Invoke(current!);

                // 描画中にさらに新しいフレームが到着していたら、もう一回だけスケジュール
                lock (_presentLock)
                {
                    if (_pendingBgra != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke((Action)RenderLatestBgra, System.Windows.Threading.DispatcherPriority.Render);
                    }
                    else
                    {
                        _presentPending = false; // 何もなければアイドルへ
                    }
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

            surface?.Dispose();
            texture?.Dispose();
            device?.Dispose();
            d3d?.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
    }
}
