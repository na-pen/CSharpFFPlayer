using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using SharpDX;
using SharpDX.Direct3D9;
using static System.Windows.Forms.DataFormats;

namespace CSharpFFPlayer
{
    public class CpuD3DImageWriter : IDisposable
    {
        private readonly D3DImage d3dImage;
        private readonly int width, height;

        private Direct3DEx d3d;
        private DeviceEx device;
        private Texture texture;
        private Surface surface;

        public CpuD3DImageWriter(D3DImage d3dImage, int width, int height)
        {
            this.d3dImage = d3dImage ?? throw new ArgumentNullException(nameof(d3dImage));
            this.width = width;
            this.height = height;

            InitD3D9();
        }

        private void InitD3D9()
        {
            d3d = new Direct3DEx();
            var presentParams = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = PresentInterval.Default
            };

            device = new DeviceEx(d3d, 0, DeviceType.Hardware, IntPtr.Zero,
                                  CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                                  presentParams);

            // CPU から書き込む用テクスチャ（システムメモリ）
            texture = new Texture(device, width, height, 1, Usage.Dynamic, SharpDX.Direct3D9.Format.A8R8G8B8, Pool.Default);
            surface = texture.GetSurfaceLevel(0);

            // WPF に接続
            d3dImage.Lock();
            d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer);
            d3dImage.Unlock();
        }

        /// <summary>
        /// CPUフレーム（BGRA配列）を描画
        /// </summary>
        public unsafe void EnqueueFrame(byte[] bgraData)
        {
            DataRectangle rect = texture.LockRectangle(0, LockFlags.Discard);
            Marshal.Copy(bgraData, 0, rect.DataPointer, bgraData.Length);
            texture.UnlockRectangle(0);

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                d3dImage.Lock();
                d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                d3dImage.Unlock();
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        public void Dispose()
        {
            surface?.Dispose();
            texture?.Dispose();
            device?.Dispose();
            d3d?.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
    }
}
