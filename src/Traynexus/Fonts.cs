using System.Drawing;

namespace Traynexus
{
    /// <summary>
    /// 全局共享 Font 实例。
    /// MainForm 原本 100+ 处 new Font(...) 挂在控件属性上，WinForms 控件 Dispose
    /// 不会自动释放这些 Font，存在 GDI+ 句柄缓慢上涨。抽为静态共享后整进程只 new 一次，
    /// 进程退出由 OS 回收，消除泄漏 + 减少 GC 压力。
    /// 字号档位按 MainForm 实际用量归一化，新增控件请优先复用既有档位。
    /// </summary>
    internal static class Fonts
    {
        private const string Fnt = "Microsoft YaHei UI";

        // ---- 8f ----
        public static readonly Font S8   = new Font(Fnt, 8f);
        public static readonly Font S8B  = new Font(Fnt, 8f, FontStyle.Bold);

        // ---- 8.5f ----
        public static readonly Font S85  = new Font(Fnt, 8.5f);

        // ---- 9f ----
        public static readonly Font S9   = new Font(Fnt, 9f);
        public static readonly Font S9B  = new Font(Fnt, 9f, FontStyle.Bold);

        // ---- 9.5f ----
        public static readonly Font S95  = new Font(Fnt, 9.5f);
        public static readonly Font S95B = new Font(Fnt, 9.5f, FontStyle.Bold);

        // ---- 10f ----
        public static readonly Font S10   = new Font(Fnt, 10f);
        public static readonly Font S10B  = new Font(Fnt, 10f, FontStyle.Bold);

        // ---- 11f ----
        public static readonly Font S11   = new Font(Fnt, 11f);
        public static readonly Font S11B  = new Font(Fnt, 11f, FontStyle.Bold);

        // ---- 12f+ ----
        public static readonly Font S12B  = new Font(Fnt, 12f, FontStyle.Bold);
        public static readonly Font S14B  = new Font(Fnt, 14f, FontStyle.Bold);
        public static readonly Font S15B  = new Font(Fnt, 15f, FontStyle.Bold);
        public static readonly Font S22B  = new Font(Fnt, 22f, FontStyle.Bold);
    }
}
