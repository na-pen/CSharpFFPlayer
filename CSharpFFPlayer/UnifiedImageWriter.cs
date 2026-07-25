using FFmpeg.AutoGen;
using SharpDX;
using SharpDX.Direct3D9;
using System;
using System.Collections.Concurrent;
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

    /// <summary>
    /// CPU 経路・D3DImage 経路のどちらにも描画できるユーティリティ。
    /// ・WriteableBitmap: 直接 BackBuffer にコピー
    /// ・D3DImage: sysmem テクスチャへ書いて D3D9 RT サーフェスへ UpdateSurface()
    /// 本クラスは「描画器」に徹し、GPU 変換や FFmpeg の責務は外に持たせます。
    /// </summary>
    public class UnifiedImageWriter : IDisposable
    {
        // --- コンストラクタ引数 ---
        private readonly RenderTargetType targetType;
        private readonly WriteableBitmap? writeableBitmap;
        private readonly D3DImage? d3dImage;
        private readonly FrameConveter frameConveter;
        private readonly int width;
        private readonly int height;
        private readonly Int32Rect rect;

        // --- WriteableBitmap 経路（ManagedFrame キュー方式） ---
        private readonly ConcurrentQueue<ManagedFrame> renderQueue = new();
        private bool isRenderPending = false;

        // --- D3D9Ex / D3DImage 経路 ---
        private Direct3DEx? d3d;
        private DeviceEx? device;
        private Texture? d3d9SharedTex;  // 表示側（D3DImage の back buffer に紐付く）
        private Surface? d3d9SharedSurf; // ↑の 0 面
        private IntPtr sharedHandle;

        // sysmem 側（毎回 new せず再利用）
        private Texture? sysmemTex;
        private Surface? sysmemSurf;

        // --- 破棄済みフラグ ---
        // 描画ターゲット切り替え時、Dispatcher に積まれたままの描画要求が
        // 破棄済みデバイスに触れないようにするためのガード。
        private volatile bool disposed = false;
        public bool IsDisposed => disposed;

        // --- 合流描画（最新だけ） ---
        private readonly object presentLock = new();
        private bool presentPending = false;
        private byte[]? pendingBgra;
        private Action<byte[]>? pendingOnConsumed;

        public UnifiedImageWriter(
            RenderTargetType targetType,
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
            this.rect = new Int32Rect(0, 0, width, height);

            Log($"ctor: target={targetType}, size={width}x{height}");

            if (targetType == RenderTargetType.WriteableBitmap)
            {
                writeableBitmap = wb ?? throw new ArgumentNullException(nameof(wb));
                frameConveter.SetDestinationStride(writeableBitmap.BackBufferStride);
                Log($"WriteableBitmap stride={writeableBitmap.BackBufferStride}");
            }
            else
            {
                d3dImage = di ?? throw new ArgumentNullException(nameof(di));
                EnsureD3D9Ready(); // D3D 初期化
            }
        }

        // =========================================================
        // ==============  WriteableBitmap 経路（既存） =============
        // =========================================================
        public void EnqueueFrame(ManagedFrame newFrame)
        {
            if (disposed) { newFrame?.Dispose(); return; }

            // 最新のみ残す
            while (renderQueue.TryDequeue(out var old)) old.Dispose();
            renderQueue.Enqueue(newFrame);

            if (!isRenderPending)
            {
                isRenderPending = true;
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(RenderLatestFrame),
                    System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        private void RenderLatestFrame()
        {
            isRenderPending = false;

            if (disposed) { ClearQueue(); return; }

            if (!renderQueue.TryDequeue(out var latest))
                return;

            try
            {
                if (targetType != RenderTargetType.WriteableBitmap)
                    return;

                // GPU フレームならここで CPU フレーム化（呼び出し側の方針に合わせる）
                if (latest.IsGpuFrame)
                {
                    unsafe { latest.GetCpuFrame(); }
                    unsafe { if (latest.Frame == null) return; }
                }

                RenderToWriteableBitmap(latest);
            }
            finally
            {
                latest.Dispose();

                if (!renderQueue.IsEmpty)
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        new Action(RenderLatestFrame),
                        System.Windows.Threading.DispatcherPriority.Render);
                }
            }
        }

        private void RenderToWriteableBitmap(ManagedFrame frame)
        {
            writeableBitmap!.Lock();
            try
            {
                unsafe
                {
                    byte* dst = (byte*)writeableBitmap.BackBuffer.ToPointer();
                    frameConveter.ConvertFrameDirect(frame, dst);
                }
                writeableBitmap.AddDirtyRect(rect);
            }
            finally
            {
                writeableBitmap.Unlock();
            }
        }

        // =========================================================
        // ==============  D3DImage 経路（BGRA 受け取り） ===========
        // =========================================================
        /// <summary>
        /// BGRA を「最新だけ合流」しつつ描画。
        /// 送信元は ArrayPool を使う場合、onConsumed で返却可能。
        /// </summary>
        public void PresentBgra(byte[] bgra, Action<byte[]>? onConsumed = null)
        {
            if (bgra == null) return;
            if (disposed) { onConsumed?.Invoke(bgra); return; }
            if (bgra.Length < width * height * 4)
                Log($"[Warn] BGRA サイズが小さい: {bgra.Length} < {width * height * 4}");

            lock (presentLock)
            {
                // 既に保留があればそれは破棄（返却）
                if (pendingBgra != null && pendingBgra != bgra)
                    pendingOnConsumed?.Invoke(pendingBgra);

                pendingBgra = bgra;
                pendingOnConsumed = onConsumed;

                if (presentPending) return;
                presentPending = true;
            }

            Application.Current.Dispatcher.BeginInvoke(
                new Action(RenderLatestBgra),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        private void RenderLatestBgra()
        {
            byte[]? current;
            Action<byte[]>? cb;

            lock (presentLock)
            {
                current = pendingBgra;
                cb = pendingOnConsumed;
                pendingBgra = null;
                pendingOnConsumed = null;
            }

            try
            {
                if (current == null) return;
                if (disposed) return;

                if (targetType == RenderTargetType.WriteableBitmap)
                {
                    CopyToWriteableBitmap(current);
                }
                else
                {
                    EnsureD3D9Ready();
                    EnsureSysmemTexture();

                    // sysmem → shared surface → D3DImage
                    var lr = sysmemTex!.LockRectangle(0, LockFlags.Discard);
                    try
                    {
                        // コピー量は「配列長」ではなく必要なバイト数で決める。
                        // また、テクスチャの行ピッチが幅×4 と一致しない場合があるため行単位でコピーする。
                        int srcStride = width * 4;
                        int dstStride = lr.Pitch;
                        int rows = Math.Min(height, current.Length / srcStride);

                        if (dstStride == srcStride)
                        {
                            Marshal.Copy(current, 0, lr.DataPointer, srcStride * rows);
                        }
                        else
                        {
                            for (int y = 0; y < rows; y++)
                            {
                                Marshal.Copy(current, y * srcStride, lr.DataPointer + y * dstStride, srcStride);
                            }
                        }
                    }
                    finally
                    {
                        sysmemTex.UnlockRectangle(0);
                    }

                    device!.UpdateSurface(sysmemSurf!, null, d3d9SharedSurf!, null);

                    d3dImage!.Lock();
                    d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                    d3dImage.Unlock();
                }
            }
            catch (Exception ex)
            {
                Log($"[Present] 例外: {ex}");
            }
            finally
            {
                // ArrayPool 返却など
                cb?.Invoke(current!);

                // 描画中に到着した最新があれば、もう一回だけスケジュール
                lock (presentLock)
                {
                    if (pendingBgra != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(
                            new Action(RenderLatestBgra),
                            System.Windows.Threading.DispatcherPriority.Render);
                    }
                    else
                    {
                        presentPending = false;
                    }
                }
            }
        }

        // =========================================================
        // ==============  下回り（共通ヘルパ） =====================
        // =========================================================
        private void EnsureD3D9Ready()
        {
            // すでに有効
            if (device != null && !device.IsDisposed &&
                d3d9SharedSurf != null && !d3d9SharedSurf.IsDisposed) return;

            Log("InitD3D9: 開始");
            try
            {
                // Dispose してから再作成
                sysmemSurf?.Dispose(); sysmemSurf = null;
                sysmemTex?.Dispose(); sysmemTex = null;

                d3d9SharedSurf?.Dispose(); d3d9SharedSurf = null;
                d3d9SharedTex?.Dispose(); d3d9SharedTex = null;

                device?.Dispose(); device = null;
                d3d?.Dispose(); d3d = null;

                d3d = new Direct3DEx();
                var pp = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    DeviceWindowHandle = GetDesktopWindow(),
                    PresentationInterval = PresentInterval.Default
                };

                device = new DeviceEx(
                    d3d, 0, DeviceType.Hardware, IntPtr.Zero,
                    CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                    pp);

                d3d9SharedTex = new Texture(device, width, height, 1,
                                            Usage.RenderTarget, Format.A8R8G8B8, Pool.Default, ref sharedHandle);
                d3d9SharedSurf = d3d9SharedTex.GetSurfaceLevel(0);

                d3dImage!.Lock();
                d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, d3d9SharedSurf.NativePointer);
                d3dImage.Unlock();

                Log($"InitD3D9: 完了 (A8R8G8B8 {width}x{height})");
            }
            catch (Exception ex)
            {
                Log($"InitD3D9: 失敗 {ex}");
                throw;
            }
        }

        private void EnsureSysmemTexture()
        {
            if (sysmemTex != null && !sysmemTex.IsDisposed) return;

            sysmemSurf?.Dispose(); sysmemSurf = null;
            sysmemTex?.Dispose(); sysmemTex = null;

            sysmemTex = new Texture(device, width, height, 1,
                                    Usage.Dynamic, Format.A8R8G8B8, Pool.SystemMemory);
            sysmemSurf = sysmemTex.GetSurfaceLevel(0);

            Log("SysmemTexture: 再作成");
        }

        /// <summary>
        /// WPF の行パディングを考慮して BGRA を書き込む。
        /// </summary>
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
                        source: Unsafe.AsPointer(ref src[0]),
                        destination: dst.ToPointer(),
                        destinationSizeInBytes: (long)dstStride * rows,
                        sourceBytesToCopy: (long)srcStride * rows);
                }
                else
                {
                    // 行ごとコピー
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

                writeableBitmap.AddDirtyRect(rect);
            }
            finally
            {
                writeableBitmap.Unlock();
            }
        }

        // =========================================================
        // ==============  破棄処理  ================================
        // =========================================================
        public void ClearQueue()
        {
            while (renderQueue.TryDequeue(out var frame))
                frame.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            Log("Dispose()");
            ClearQueue();

            lock (presentLock)
            {
                pendingBgra = null;
                pendingOnConsumed = null;
                presentPending = false;
            }

            // sysmem
            sysmemSurf?.Dispose(); sysmemSurf = null;
            sysmemTex?.Dispose(); sysmemTex = null;

            // shared
            d3d9SharedSurf?.Dispose(); d3d9SharedSurf = null;
            d3d9SharedTex?.Dispose(); d3d9SharedTex = null;

            device?.Dispose(); device = null;
            d3d?.Dispose(); d3d = null;
        }

        // =========================================================
        // ==============  小物  ====================================
        // =========================================================
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Log(string msg)
        {
            var tid = Environment.CurrentManagedThreadId;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [T{tid}] {msg}");
        }
    }
}
