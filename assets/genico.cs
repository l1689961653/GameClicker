// genico.cs —— 生成 assets\app.ico（16/24/32/48/64/256 多尺寸，PNG 内嵌格式）
// 图案与 TrayIconFactory 一致：蓝色渐变圆 + 白环 + 白色光标箭头
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class GenIco
{
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 256 };

        List<byte[]> pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap bmp = Render(s))
            using (MemoryStream pm = new MemoryStream())
            {
                bmp.Save(pm, ImageFormat.Png);
                pngs.Add(pm.ToArray());
            }
        }

        // ICONDIR(6) + 每项 16 字节目录 + 所有 PNG 数据
        int headerSize = 6 + 16 * sizes.Length;
        List<byte> file = new List<byte>();

        // ICONDIR
        file.AddRange(new byte[] { 0, 0, 1, 0 });
        file.AddRange(BitConverter.GetBytes((ushort)sizes.Length));

        // 目录项（先算好每张 PNG 的偏移）
        int offset = headerSize;
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            file.Add((byte)(s >= 256 ? 0 : s));       // width（0 = 256）
            file.Add((byte)(s >= 256 ? 0 : s));       // height
            file.Add(0);                               // 调色板色数
            file.Add(0);                               // 保留
            file.AddRange(BitConverter.GetBytes((ushort)1));  // planes
            file.AddRange(BitConverter.GetBytes((ushort)32)); // bpp
            file.AddRange(BitConverter.GetBytes((uint)pngs[i].Length));
            file.AddRange(BitConverter.GetBytes((uint)offset));
            offset += pngs[i].Length;
        }

        // PNG 数据
        foreach (byte[] png in pngs) file.AddRange(png);

        File.WriteAllBytes(outPath, file.ToArray());
        Console.WriteLine("已生成 " + outPath + "（" + file.Count + " 字节，" + sizes.Length + " 个尺寸）");
    }

    static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            RectangleF body = new RectangleF(s * 0.08f, s * 0.08f, s * 0.84f, s * 0.84f);
            using (LinearGradientBrush bg = new LinearGradientBrush(body,
                Color.FromArgb(74, 132, 244), Color.FromArgb(38, 78, 188), 55f))
                g.FillEllipse(bg, body);
            float ring = Math.Max(1f, s * 0.055f);
            using (Pen p = new Pen(Color.White, ring))
                g.DrawEllipse(p, new RectangleF(body.X + ring / 2, body.Y + ring / 2,
                    body.Width - ring, body.Height - ring));
            PointF[] tri = new PointF[]
            {
                new PointF(s * 0.36f, s * 0.28f),
                new PointF(s * 0.68f, s * 0.56f),
                new PointF(s * 0.51f, s * 0.59f),
                new PointF(s * 0.45f, s * 0.76f),
            };
            using (SolidBrush wb = new SolidBrush(Color.White))
                g.FillPolygon(wb, tri);
        }
        return bmp;
    }
}
