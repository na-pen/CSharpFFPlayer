using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

using System.Runtime.InteropServices;
using CSharpFFPlayer.CSharpFFPlayer;

namespace CSharpFFPlayer
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    namespace CSharpFFPlayer
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DXGI_QUERY_VIDEO_MEMORY_INFO
        {
            public ulong Budget;
            public ulong CurrentUsage;
            public ulong AvailableForReservation;
            public ulong CurrentReservation;
        }
    }

    public static class GpuMemoryHelper
    {
        public static ulong GetAvailableGpuMemoryBytes()
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory4>();

            for (int i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out IDXGIAdapter1 adapter1).Failure)
                    break;

                if ((adapter1.Description1.Flags & AdapterFlags.Software) != 0)
                    continue;

                if (adapter1 is IDXGIAdapter3 adapter3)
                {
                    unsafe
                    {
                        // スタック上に確保した構造体
                        DXGI_QUERY_VIDEO_MEMORY_INFO memInfo;

                        int hr = adapter3.QueryVideoMemoryInfo(
                            0,
                            MemorySegmentGroup.Local,
                            (void*)(&memInfo) // 明示的なキャスト
                        );

                        if (hr >= 0)
                        {
                            return memInfo.Budget - memInfo.CurrentUsage;
                        }
                    }
                }
            }

            return 0;
        }
    }
}
