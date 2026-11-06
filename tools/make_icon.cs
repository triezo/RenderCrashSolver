// make_icon.cs - writes app.ico (the exe icon Explorer shows) from the same drawing as the window icon, Theme.AppIconBitmap.
// Built and run by build.bat / package.bat:  make_icon.exe <out.ico>

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace RenderSolver
{
    static class MakeIcon
    {
        static int Main(string[] args)
        {
            if (args.Length != 1) { Console.Error.WriteLine("usage: make_icon <out.ico>"); return 1; }
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (Bitmap bmp = Theme.AppIconBitmap(size))
                    images.Add(size >= 256 ? Png(bmp) : Dib(bmp));

            using (var w = new BinaryWriter(File.Create(args[0])))
            {
                // ICONDIR, then one ICONDIRENTRY per image, then the images
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);   // palette colours
                    w.Write((byte)0);
                    w.Write((short)1);  // planes
                    w.Write((short)32); // bits per pixel
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
            Console.WriteLine("icon: " + args[0]);
            return 0;
        }

        static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>Classic 32-bit icon image: BITMAPINFOHEADER, bottom-up BGRA rows, empty AND mask (the alpha channel is used).</summary>
        static byte[] Dib(Bitmap bmp)
        {
            int n = bmp.Width, maskRow = ((n + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(n);
                w.Write(n * 2); // XOR image + AND mask
                w.Write((short)1);
                w.Write((short)32);
                w.Write(0);     // BI_RGB
                w.Write(n * n * 4 + maskRow * n);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = n - 1; y >= 0; y--)
                    for (int x = 0; x < n; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                w.Write(new byte[maskRow * n]);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
