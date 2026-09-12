using System;
using System.Drawing;
using System.Windows.Forms;

namespace Win7Proxy
{
    /// <summary>
    /// 高 DPI 支持。与 app.manifest 里的 dpiAwareness 声明配套使用，缺一不可：
    /// - manifest 负责把进程变成 DPI 感知。不声明时 Windows 会先把整个窗口当位图放大
    ///   再交给程序绘制，结果必然发虚发糊，且程序内部根本无从补救；
    /// - 本类负责按屏幕 DPI 放大控件尺寸。否则 DPI 感知后字体按点值自动变大，
    ///   而代码里写死的像素宽高不变，就会出现"字很大、按钮很小"的错位与裁切。
    ///
    /// 为什么不用 AutoScaleMode.Dpi：实测在 net461 目标的纯代码窗体（无设计器）上
    /// 它不产生任何缩放（125%~200% 下窗口尺寸纹丝不动），因此这里改为显式缩放，
    /// 行为确定、可验证。
    /// </summary>
    internal static class HiDpi
    {
        /// <summary>布局基准 DPI —— 即代码里那些 Width/Height 像素值所对应的 DPI。</summary>
        private const float DesignDpi = 96F;

        /// <summary>界面统一字体。Segoe UI 自 Vista 起可用，Win7 中文环境会自动回退到雅黑。</summary>
        public static Font UiFont => new Font("Segoe UI", 9F);

        /// <summary>记录实际生效的缩放倍率，便于排查。</summary>
        public static float LastScale { get; private set; } = 1F;

        /// <summary>在窗体构造函数<b>最开头</b>调用（必须早于设置 Size 和创建控件）。</summary>
        public static void ApplyTo(Form form)
        {
            form.AutoScaleMode = AutoScaleMode.None;  // 关掉自动缩放，改由 ScaleForDpi 显式控制
            form.Font = UiFont;
        }

        /// <summary>
        /// 在窗体构造函数<b>末尾</b>（所有控件都创建完之后）调用，按屏幕 DPI 整体放大。
        /// </summary>
        public static void ScaleForDpi(Form form)
        {
            float factor;
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    factor = g.DpiX / DesignDpi;
            }
            catch
            {
                return;
            }

            if (float.IsNaN(factor) || float.IsInfinity(factor) || factor < 0.5F || factor > 5F)
                return;

            LastScale = factor;
            if (Math.Abs(factor - 1F) < 0.01F) return;   // 100% 缩放，无需处理

            form.Scale(new SizeF(factor, factor));
        }
    }
}
