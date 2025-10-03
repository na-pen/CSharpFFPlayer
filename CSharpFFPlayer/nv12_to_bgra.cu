// ====== 定数メモリに LUT を配置 ======
__constant__ int LUT_U_B[256];
__constant__ int LUT_U_G[256];
__constant__ int LUT_V_R[256];
__constant__ int LUT_V_G[256];

// ====== カーネル ======
extern "C" __global__
void Nv12ToBgraKernel(
    const unsigned char* __restrict__ yPlane, int pitchY,
    const unsigned char* __restrict__ uvPlane, int pitchUV,
    unsigned char* __restrict__ outBGRA, int pitchOut,
    int width, int height, int useBT709)
{
    int x = blockDim.x * blockIdx.x + threadIdx.x;
    int y = blockDim.y * blockIdx.y + threadIdx.y;
    if (x >= width || y >= height) return;

    int yIdx = y * pitchY + x;

    // ===== 2x2 グループで UV を共有 =====
    int uvX = x & ~1;        
    int uvY = y >> 1;
    int uvIdx = uvY * pitchUV + uvX;

    int U, V;
    if ((threadIdx.x & 1) == 0 && (threadIdx.y & 1) == 0) {
        U = (int)uvPlane[uvIdx + 0]; // 0〜255
        V = (int)uvPlane[uvIdx + 1];
    }

    unsigned mask = 0xffffffff;
    U = __shfl_sync(mask, U, (threadIdx.x & ~1) + (threadIdx.y & ~1) * blockDim.x);
    V = __shfl_sync(mask, V, (threadIdx.x & ~1) + (threadIdx.y & ~1) * blockDim.x);

    // ===== YUV → RGB 変換 (Y寄与 + LUT寄与) =====
    int Y = (int)yPlane[yIdx];
    int C = Y - 16;
    if (C < 0) C = 0;

    int Yterm = (298 * C + 128) >> 8;  // Y寄与（共通部分）

    int idxU = U; // 0〜255
    int idxV = V;

    int R = Yterm + LUT_V_R[idxV];
    int G = Yterm + LUT_U_G[idxU] + LUT_V_G[idxV];
    int B = Yterm + LUT_U_B[idxU];

    // clamp
    R = R < 0 ? 0 : (R > 255 ? 255 : R);
    G = G < 0 ? 0 : (G > 255 ? 255 : G);
    B = B < 0 ? 0 : (B > 255 ? 255 : B);

    uchar4 out;
    out.x = (unsigned char)B;
    out.y = (unsigned char)G;
    out.z = (unsigned char)R;
    out.w = 255;

    ((uchar4*)outBGRA)[y * (pitchOut / 4) + x] = out;
}
