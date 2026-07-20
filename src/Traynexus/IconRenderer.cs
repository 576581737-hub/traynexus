using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Traynexus
{
    /// <summary>
    /// 数字徽标托盘图标（MemTrayCN 风格）：
    /// 圆角方块毛玻璃背景 + 中心大号内存百分比数字。
    /// 数字颜色随占用率变化：绿(<60) / 橙(60-85) / 红(>85)。
    /// 仅显示内存数字，简洁清晰，与微信等应用图标大小一致。
    /// </summary>
    public static class IconRenderer
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private struct CacheKey : IEquatable<CacheKey>
        {
            public readonly int Percent;
            public CacheKey(int p) { Percent = p; }
            public override int GetHashCode() { return Percent; }
            public override bool Equals(object obj)
            {
                if (!(obj is CacheKey)) return false;
                return Equals((CacheKey)obj);
            }
            public bool Equals(CacheKey other) { return Percent == other.Percent; }
        }

        private static readonly Dictionary<CacheKey, Icon> _cache =
            new Dictionary<CacheKey, Icon>();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// 构建托盘图标。memPercent 为内存占用百分比。
        /// battPercent/isCharging 保留参数兼容但不再用于绘图。
        /// </summary>
        public static Icon Build(int memPercent, int battPercent, bool isCharging)
        {
            if (memPercent < 0) memPercent = 0;
            if (memPercent > 100) memPercent = 100;
            var key = new CacheKey(memPercent);

            lock (_cacheLock)
            {
                Icon cached;
                if (_cache.TryGetValue(key, out cached)) return cached;
            }

            Icon icon = RenderIcon(memPercent);
            lock (_cacheLock)
            {
                Icon existing;
                if (_cache.TryGetValue(key, out existing))
                {
                    try { icon.Dispose(); } catch { }
                    return existing;
                }
                _cache[key] = icon;
                return icon;
            }
        }

        private static Icon RenderIcon(int memPercent)
        {
            const int size = 64;
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    g.Clear(Color.Transparent);

                    // === 圆角方块毛玻璃背景 ===
                    const float margin = 4f;
                    float plateSize = size - margin * 2f;
                    const float radius = 14f;
                    var plateRect = new RectangleF(margin, margin, plateSize, plateSize);
                    var platePath = RoundedRect(plateRect, radius);

                    // 渐变填充（上亮下暗，毛玻璃质感）
                    using (var plateBrush = new LinearGradientBrush(
                        plateRect,
                        Color.FromArgb(252, 253, 255),
                        Color.FromArgb(232, 236, 242),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPath(plateBrush, platePath);
                    }

                    // 细边框
                    using (var borderPen = new Pen(Color.FromArgb(200, 210, 220), 1.5f))
                    {
                        g.DrawPath(borderPen, platePath);
                    }
                    platePath.Dispose();

                    // 顶部高光
                    var hlRect = new RectangleF(margin + 3f, margin + 2f, plateSize - 8f, 10f);
                    var hlPath = RoundedRect(hlRect, 5f);
                    using (var hlBrush = new LinearGradientBrush(
                        hlRect,
                        Color.FromArgb(90, 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPath(hlBrush, hlPath);
                    }
                    hlPath.Dispose();

                    // === 中心大号数字 ===
                    string text = memPercent.ToString();

                    // 数字颜色：绿(<60) / 橙(60-85) / 红(>85)
                    Color textColor;
                    if (memPercent < 60)
                        textColor = Color.FromArgb(34, 197, 94);   // 绿 #22C55E
                    else if (memPercent <= 85)
                        textColor = Color.FromArgb(249, 115, 22);   // 橙 #F97316
                    else
                        textColor = Color.FromArgb(239, 68, 68);    // 红 #EF4444

                    // 字体尺寸按图标 64px 底图设计：数字应占方块的 55%-65%
                    float fontSize;
                    if (memPercent < 10) fontSize = 40f;
                    else if (memPercent < 100) fontSize = 36f;
                    else fontSize = 26f;

                    // 使用 Arial Black 更粗更清晰，托盘缩放后仍可读
                    using (var font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        var sf = new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center,
                            FormatFlags = StringFormatFlags.NoWrap
                        };
                        // 用整个 bitmap 区域作为绘制目标，避免边界裁切
                        var fullRect = new RectangleF(0, 0, size, size);
                        // 阴影
                        var shadowRect = new RectangleF(1.5f, 1.5f, size, size);
                        using (var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                        {
                            g.DrawString(text, font, shadowBrush, shadowRect, sf);
                        }
                        g.DrawString(text, font, new SolidBrush(textColor), fullRect, sf);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        public static void DisposeCache()
        {
            lock (_cacheLock)
            {
                foreach (var kv in _cache)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
                _cache.Clear();
            }
        }
    }

    /// <summary>
    /// 产品 logo 加载器：从 exe 内嵌资源加载 PNG，缓存为 Icon 或 Bitmap，供窗口 / 任务栏 / 关于页使用。
    /// 打包方式（见 build.bat）：/resource:logo_256.png,Traynexus.logo_256.png
    /// </summary>
    public static class LogoLoader
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static Icon _icon;
        private static Bitmap _bitmap;
        private static Bitmap _github;
        private static Bitmap _githubWhite;
        private static readonly object _lock = new object();

        /// <summary>返回窗口图标：多尺寸 ICO（16/32/48/64/128/256），Windows 会自动挑最合适的尺寸，
        /// 避免任务栏强制缩放单尺寸位图导致的模糊 + 视觉偏小。</summary>
        public static Icon GetWindowIcon()
        {
            lock (_lock)
            {
                if (_icon != null) return _icon;
                try
                {
                    using (var raw = LoadPng("Traynexus.logo_256.png"))
                    {
                        if (raw == null) return null;
                        // 先裁掉四周透明边距，让可见内容填满目标 icon 画布
                        using (var trimmed = TrimTransparentBorder(raw))
                        {
                            _icon = BuildMultiSizeIcon(trimmed, new int[] { 16, 24, 32, 48, 64, 128, 256 });
                        }
                    }
                }
                catch { _icon = null; }
                return _icon;
            }
        }

        /// <summary>返回品牌区显示用的 Bitmap（128×128），供 PictureBox 使用</summary>
        public static Bitmap GetBrandBitmap()
        {
            lock (_lock)
            {
                if (_bitmap != null) return _bitmap;
                try { _bitmap = LoadPng("Traynexus.logo_128.png"); }
                catch { _bitmap = null; }
                return _bitmap;
            }
        }

        /// <summary>返回 GitHub 图标位图。white=true 时返回白色版本（用于深色主题），否则黑色版本</summary>
        public static Bitmap GetGithubBitmap(bool white)
        {
            lock (_lock)
            {
                if (white)
                {
                    if (_githubWhite != null) return _githubWhite;
                    try { _githubWhite = LoadPng("Traynexus.github_icon_white.png"); }
                    catch { _githubWhite = null; }
                    return _githubWhite;
                }
                else
                {
                    if (_github != null) return _github;
                    try { _github = LoadPng("Traynexus.github_icon.png"); }
                    catch { _github = null; }
                    return _github;
                }
            }
        }

        private static Bitmap LoadPng(string resourceName)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) return null;
                return new Bitmap(s);
            }
        }

        /// <summary>
        /// 把源位图按多个目标尺寸重采样，然后组装成一个多帧 ICO 内存流并返回 Icon。
        /// Windows 任务栏/标题栏会自动选用最匹配的尺寸，避免单尺寸位图被强制缩放导致模糊。
        /// </summary>
        private static Icon BuildMultiSizeIcon(Bitmap src, int[] sizes)
        {
            // 每一帧先编码为 PNG 字节数组（Vista+ ICO 允许 PNG 帧，节省体积，256 以内也用 PNG）
            var pngBytesList = new System.Collections.Generic.List<byte[]>();
            foreach (int sz in sizes)
            {
                using (var resized = new Bitmap(sz, sz, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(resized))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, sz, sz));
                    }
                    using (var ms = new System.IO.MemoryStream())
                    {
                        resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        pngBytesList.Add(ms.ToArray());
                    }
                }
            }

            // 拼装 ICO 二进制：6-byte ICONDIR + N * 16-byte ICONDIRENTRY + N 段 PNG 数据
            using (var ico = new System.IO.MemoryStream())
            {
                var bw = new System.IO.BinaryWriter(ico);
                bw.Write((ushort)0);          // Reserved
                bw.Write((ushort)1);          // Type: 1=icon
                bw.Write((ushort)sizes.Length); // Count

                int headerSize = 6 + 16 * sizes.Length;
                int offset = headerSize;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    int dataLen = pngBytesList[i].Length;
                    bw.Write((byte)(sz >= 256 ? 0 : sz)); // Width  (0 表示 256)
                    bw.Write((byte)(sz >= 256 ? 0 : sz)); // Height
                    bw.Write((byte)0);         // Color count
                    bw.Write((byte)0);         // Reserved
                    bw.Write((ushort)1);       // Color planes
                    bw.Write((ushort)32);      // Bits per pixel
                    bw.Write((uint)dataLen);   // Data size
                    bw.Write((uint)offset);    // Data offset
                    offset += dataLen;
                }
                for (int i = 0; i < sizes.Length; i++)
                    bw.Write(pngBytesList[i]);

                bw.Flush();
                ico.Position = 0;
                return new Icon(ico);
            }
        }

        /// <summary>
        /// 扫描位图找出非透明像素的最小外接矩形，裁掉四周的透明边距。
        /// 用于让 logo 在 Windows 任务栏/标题栏与其他"填满画布"的图标并排时视觉尺寸一致。
        /// </summary>
        private static Bitmap TrimTransparentBorder(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            int left = w, top = h, right = -1, bottom = -1;

            var rect = new Rectangle(0, 0, w, h);
            var data = src.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                for (int y = 0; y < h; y++)
                {
                    int rowStart = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        // BGRA，Alpha 在第 4 字节
                        byte a = buf[rowStart + x * 4 + 3];
                        if (a > 60) // 忽略半透明的柔阴影像素，只取主体轮廓
                        {
                            if (x < left) left = x;
                            if (x > right) right = x;
                            if (y < top) top = y;
                            if (y > bottom) bottom = y;
                        }
                    }
                }
            }
            finally
            {
                src.UnlockBits(data);
            }

            // 完全透明或异常：原样返回一份拷贝
            if (right < left || bottom < top) return new Bitmap(src);

            int cw = right - left + 1;
            int ch = bottom - top + 1;
            // 保持正方形（Windows Icon 需要正方形，避免拉伸变形）
            int side = Math.Max(cw, ch);
            var result = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                int dx = (side - cw) / 2;
                int dy = (side - ch) / 2;
                g.DrawImage(src,
                    new Rectangle(dx, dy, cw, ch),
                    new Rectangle(left, top, cw, ch),
                    GraphicsUnit.Pixel);
            }
            return result;
        }
    }
}
