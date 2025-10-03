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
                else if (targetType == RenderTargetType.D3DImage)
                {
                    RenderToD3DImage(latest);
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

        private void RenderToD3DImage(ManagedFrame frame)
        {
            switch (frame.HwDeviceType)
            {
                case AVHWDeviceType.AV_HWDEVICE_TYPE_NONE:
                    {
                        // CPU フレームを BGRA 配列に変換
                        byte[] bgra = frameConveter.ConvertFrameToArray(frame);

                        // 書き込み用システムメモリテクスチャ
                        using (var sysTex = new Texture(device, width, height, 1,
                                                        Usage.Dynamic, Format.A8R8G8B8, Pool.SystemMemory))
                        {
                            var rect = sysTex.LockRectangle(0, LockFlags.Discard);
                            Marshal.Copy(bgra, 0, rect.DataPointer, bgra.Length);
                            sysTex.UnlockRectangle(0);

                            // sysTex → surface にコピー
                            using (var sysSurf = sysTex.GetSurfaceLevel(0))
                            {
                                device!.UpdateSurface(sysSurf, null, d3d9SharedSurf, null);
                            }
                        }

                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            d3dImage!.Lock();
                            d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                            d3dImage.Unlock();
                        }), System.Windows.Threading.DispatcherPriority.Render);
                        break;
                    }
                case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                    {
                        break;
                    }

                default:
                    {
                        Console.WriteLine($"[警告] 未対応の HwDeviceType: {frame.HwDeviceType}");
                        break;
                    }
            }
        }

        private bool d3dBackBufferSetOnce = false;
        public void PresentBgra(byte[] bgra)
        {
            if (targetType == RenderTargetType.WriteableBitmap)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    writeableBitmap!.Lock();
                    unsafe
                    {
                        Buffer.MemoryCopy(
                            source: Unsafe.AsPointer(ref bgra[0]),
                            destination: writeableBitmap.BackBuffer.ToPointer(),
                            destinationSizeInBytes: writeableBitmap.BackBufferStride * writeableBitmap.PixelHeight,
                            sourceBytesToCopy: bgra.Length);
                    }
                    writeableBitmap.AddDirtyRect(rect);
                    writeableBitmap.Unlock();
                }), System.Windows.Threading.DispatcherPriority.Render);
                return;
            }

            // D3DImage 経路（sysmem テクスチャ → d3d9SharedSurf → D3DImage）
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                using (var sysTex = new Texture(device, width, height, 1,
                                                Usage.Dynamic, Format.A8R8G8B8, Pool.SystemMemory))
                {
                    var lr = sysTex.LockRectangle(0, LockFlags.Discard);
                    Marshal.Copy(bgra, 0, lr.DataPointer, bgra.Length);
                    sysTex.UnlockRectangle(0);

                    using (var sysSurf = sysTex.GetSurfaceLevel(0))
                    {
                        // ★ 宛先は d3d9SharedSurf（null の surface ではない）
                        device!.UpdateSurface(sysSurf, null, d3d9SharedSurf, null);
                    }
                }

                d3dImage!.Lock();
                d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                d3dImage.Unlock();
            }), System.Windows.Threading.DispatcherPriority.Render);
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
