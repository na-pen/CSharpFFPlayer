using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpFFPlayer
{
    public enum EffectType
    {
        None,
        Posterize,
        Monochrome
    }
    internal class Effect
    {


        // ポスタリゼーション処理（BGR24、posterizeLevels階調）
        internal unsafe void ApplyPosterize(byte* bufferPtr, int stride, int width, int height, int posterizeLevels = 4)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = bufferPtr + y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte* pixel = row + x * 3; // BGR24

                    byte b = pixel[0];
                    byte g = pixel[1];
                    byte r = pixel[2];

                    int step = 256 / posterizeLevels;

                    r = (byte)((r / step) * step);
                    g = (byte)((g / step) * step);
                    b = (byte)((b / step) * step);

                    pixel[0] = b;
                    pixel[1] = g;
                    pixel[2] = r;
                }
            }
        }

        // モノクロ（2値化）処理（BGR24）
        internal unsafe void ApplyMonochrome(byte* bufferPtr, int stride, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = bufferPtr + y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte* pixel = row + x * 3;

                    byte b = pixel[0];
                    byte g = pixel[1];
                    byte r = pixel[2];

                    byte gray = (byte)(r * 0.299 + g * 0.587 + b * 0.114);
                    byte bin = (gray >= 128) ? (byte)255 : (byte)0;

                    pixel[0] = bin;
                    pixel[1] = bin;
                    pixel[2] = bin;
                }
            }
        }



    }
}
