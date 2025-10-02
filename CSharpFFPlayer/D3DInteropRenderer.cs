
using SharpDX.Direct3D11;
// D3D9Ex 系 (WPF D3DImage 用)
using SharpDX.Direct3D9;
using SharpDX.DXGI;
using SharpDX.Multimedia;
using System.Windows.Interop;
using System.Windows; // Int32Rect はここ

using CreateFlags9 = SharpDX.Direct3D9.CreateFlags;
using Device11 = SharpDX.Direct3D11.Device;
using Device9 = SharpDX.Direct3D9.DeviceEx;
using DriverType11 = SharpDX.Direct3D.DriverType;
// フォーマットはエイリアス指定
using Format11 = SharpDX.DXGI.Format;
using Format9 = SharpDX.Direct3D9.Format;
using PresentParameters9 = SharpDX.Direct3D9.PresentParameters;
using Resource11 = SharpDX.Direct3D11.Resource;
using SwapEffect9 = SharpDX.Direct3D9.SwapEffect;
using Usage9 = SharpDX.Direct3D9.Usage;


public class D3DInteropRenderer : IDisposable
{
    private Device11 device11;
    private Device9 device9;
    private Texture2D sharedTex11;
    private Texture sharedTex9;
    private D3DImage d3dImage;

    public D3DInteropRenderer(D3DImage targetImage, int width, int height)
    {
        d3dImage = targetImage;

        // D3D11 デバイス作成
        // D3D11 デバイス作成
        device11 = new Device11(DriverType11.Hardware, DeviceCreationFlags.BgraSupport);

        // D3D9Ex デバイス作成 (WPF 用)
        using (var d3d9Ex = new Direct3DEx())
        {
            var presentParams = new PresentParameters9(width, height)
            {
                Windowed = true,
                SwapEffect = SwapEffect9.Discard,
                DeviceWindowHandle = IntPtr.Zero, // 必要なら Window ハンドルを渡す
                PresentationInterval = PresentInterval.Default
            };

            device9 = new Device9(d3d9Ex, 0, DeviceType.Hardware,
                IntPtr.Zero,
                CreateFlags9.HardwareVertexProcessing | CreateFlags9.Multithreaded | CreateFlags9.FpuPreserve,
                presentParams);
        }


        // D3D11 テクスチャ作成 (共有ハンドル付き)
        var texDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format11.B8G8R8A8_UNorm, // ★ NV12 のままならシェーダ変換が必要
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            OptionFlags = ResourceOptionFlags.Shared
        };
        sharedTex11 = new Texture2D(device11, texDesc);

        // 共有ハンドルを取得
        var dxgiRes = sharedTex11.QueryInterface<SharpDX.DXGI.Resource>();
        IntPtr handle = dxgiRes.SharedHandle;

        // D3D9Ex テクスチャを共有ハンドルから作成
        sharedTex9 = new Texture(device9, width, height, 1,
           Usage9.RenderTarget, Format9.A8R8G8B8, Pool.Default, ref handle);

        // D3DImage にバインド
        d3dImage.Lock();
        d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, sharedTex9.GetSurfaceLevel(0).NativePointer);
        d3dImage.Unlock();
    }

    public void UpdateFrame(Texture2D decodedTex11)
    {
        // CUDA→D3D11 転送済みのテクスチャをコピー
        device11.ImmediateContext.CopyResource(decodedTex11, sharedTex11);

        // D3DImage 更新
        d3dImage.Lock();
        d3dImage.AddDirtyRect(new Int32Rect(0, 0, d3dImage.PixelWidth, d3dImage.PixelHeight));
        d3dImage.Unlock();
    }

    public void Dispose()
    {
        sharedTex9?.Dispose();
        sharedTex11?.Dispose();
        device9?.Dispose();
        device11?.Dispose();
    }
}
