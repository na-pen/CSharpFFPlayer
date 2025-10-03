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
    int uvIdx = (y >> 1) * pitchUV + (x & ~1);

    float Y = (float)yPlane[yIdx];
    float U = (float)uvPlane[uvIdx + 0] - 128.0f;
    float V = (float)uvPlane[uvIdx + 1] - 128.0f;

    float cR = useBT709 ? 1.5748f : 1.4020f;
    float cG1 = useBT709 ? 0.1873f : 0.3441f;
    float cG2 = useBT709 ? 0.4681f : 0.7141f;
    float cB = useBT709 ? 1.8556f : 1.7720f;

    float Rf = Y + cR * V;
    float Gf = Y - cG1 * U - cG2 * V;
    float Bf = Y + cB * U;

    unsigned char R = (unsigned char)(Rf < 0 ? 0 : (Rf > 255 ? 255 : Rf));
    unsigned char G = (unsigned char)(Gf < 0 ? 0 : (Gf > 255 ? 255 : Gf));
    unsigned char B = (unsigned char)(Bf < 0 ? 0 : (Bf > 255 ? 255 : Bf));

    int o = y * pitchOut + x * 4;
    outBGRA[o + 0] = B;
    outBGRA[o + 1] = G;
    outBGRA[o + 2] = R;
    outBGRA[o + 3] = 255;
}
