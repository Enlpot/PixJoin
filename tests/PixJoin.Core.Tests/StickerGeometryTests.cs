using PixJoin.Core.Models;

namespace PixJoin.Core.Tests;

internal static class StickerGeometryTests
{
    public static void Run()
    {
        Check.Section("StickerGeometry 四角缩放（Bug 2 回归）");

        const double ow = 100, oh = 50;   // 原始 2:1
        const double ratio = ow / oh;

        // 1. 默认等比：拖 BR 角向任意方向放大，宽高比始终 2:1
        {
            // 向右拉大
            var (w1, h1) = StickerGeometry.ComputeResize(ow, oh, 40, 0, left: false, top: false, keepAspect: true);
            Check.Near(ratio, w1 / h1, "右拉：比例保持 2:1");

            // 向下拉大
            var (w2, h2) = StickerGeometry.ComputeResize(ow, oh, 0, 30, left: false, top: false, keepAspect: true);
            Check.Near(ratio, w2 / h2, "下拉：比例保持 2:1");

            // 对角线拉大
            var (w3, h3) = StickerGeometry.ComputeResize(ow, oh, 40, 10, left: false, top: false, keepAspect: true);
            Check.Near(ratio, w3 / h3, "对角拉：比例保持 2:1");
        }

        // 2. 左侧 / 上侧手柄：符号取反后比例仍一致，且尺寸为正向变化
        {
            // 左上手柄向左上拖（dx<0, dy<0）→ 放大
            var (w, h) = StickerGeometry.ComputeResize(ow, oh, -20, -10, left: true, top: true, keepAspect: true);
            Check.Near(ratio, w / h, "TL 手柄放大：比例保持 2:1");
            Check.True(w > ow && h > oh, "TL 手柄放大：尺寸应增大");

            // 右下手柄向右下拖（dx>0, dy>0）→ 放大（left/top=false）
            var (w2, h2) = StickerGeometry.ComputeResize(ow, oh, 20, 10, left: false, top: false, keepAspect: true);
            Check.Near(ratio, w2 / h2, "BR 手柄放大：比例保持 2:1");
        }

        // 3. 缩小：比例仍锁定
        {
            var (w, h) = StickerGeometry.ComputeResize(ow, oh, -20, 0, left: false, top: false, keepAspect: true);
            Check.Near(ratio, w / h, "缩小：比例保持 2:1");
            Check.True(w < ow && h < oh, "缩小：尺寸应减小");
        }

        // 4. 自由拉伸（keepAspect=false）：比例不锁定
        {
            var (w, h) = StickerGeometry.ComputeResize(ow, oh, 40, 0, left: false, top: false, keepAspect: false);
            Check.Equal(ow + 40, w, "自由拉伸：宽度按增量变化");
            Check.Equal(oh, h, "自由拉伸：高度不变");
            Check.True(Math.Abs(w / h - ratio) > 0.001, "自由拉伸：比例应被破坏（非等比）");
        }

        // 5. 位置补偿：缩放时固定对角位置由调用方处理；ComputeResize 只负责尺寸。
        //    投影式等比下，水平外拉应平滑放大且保持比例。
        {
            var (w, h) = StickerGeometry.ComputeResize(ow, oh, 25, 0, left: false, top: false, keepAspect: true);
            Check.Near(ratio, w / h, "等比投影：比例保持 2:1");
            Check.True(w > ow && h > oh, "等比投影：水平外拉应放大");
        }

        // 6. 投影式等比回归：鼠标方向任意，缩放连续且不放大主轴
        //    （旧「主导轴」方案在水平/垂直主导切换处会突变、且放大非主导轴位移）
        {
            // 几乎纯水平拖（位移 ≈40）
            var (w1, h1) = StickerGeometry.ComputeResize(ow, oh, 40, 1, left: false, top: false, keepAspect: true);
            // 几乎纯垂直拖（位移 ≈40）
            var (w2, h2) = StickerGeometry.ComputeResize(ow, oh, 1, 40, left: false, top: false, keepAspect: true);
            Check.Near(ratio, w1 / h1, "投影等比：近水平拖保持比例");
            Check.Near(ratio, w2 / h2, "投影等比：近垂直拖保持比例");

            // 等长位移 → 缩放幅度应接近（投影到等比线的长度一致）
            double len1 = Math.Sqrt(w1 * w1 + h1 * h1);
            double len2 = Math.Sqrt(w2 * w2 + h2 * h2);
            Check.Near(len1, len2, "投影等比：等长位移缩放幅度一致", 20.0);

            // 平滑性：沿等比线方向小幅移动，结果应单调连续（无台阶）
            var (a0, _) = StickerGeometry.ComputeResize(ow, oh, 10, 5, left: false, top: false, keepAspect: true);
            var (a1, _) = StickerGeometry.ComputeResize(ow, oh, 11, 5.5, left: false, top: false, keepAspect: true);
            var (a2, _) = StickerGeometry.ComputeResize(ow, oh, 12, 6, left: false, top: false, keepAspect: true);
            Check.True(a0 < a1 && a1 < a2, "投影等比：沿等比线移动单调平滑");
        }
    }
}
