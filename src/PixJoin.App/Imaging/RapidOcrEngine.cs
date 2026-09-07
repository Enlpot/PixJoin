using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;
using PixJoin.Core.Services;
using RapidOcrNet;
using SkiaSharp;
using RapidResult = RapidOcrNet.OcrResult;
using CoreResult = PixJoin.Core.Models.OcrResult;

namespace PixJoin.App.Imaging;

/// <summary>
/// 高精度 OCR 引擎（PP-OCRv4 中文模型，ONNX Runtime CPU）。
/// 词框坐标 = 图片物理像素，与现有 OcrWord 选择逻辑直接对接；
/// 中文一字一框、拉丁按词分框（ReturnWordBox=true，CTC 时间列重建，紧贴字形）。
/// 模型缺失时自动后台下载；就绪前 IsAvailable=false，由组合引擎走兜底。
/// </summary>
public sealed class RapidOcrEngine : IOcrEngine
{
    private readonly RapidOcrModelStore _models;
    private readonly object _initLock = new();
    private RapidOcr? _ocr;
    private bool _initFailed;

    public RapidOcrEngine(RapidOcrModelStore? models = null)
    {
        _models = models ?? new RapidOcrModelStore();
    }

    public bool IsAvailable
    {
        get
        {
            _models.EnsureReady();
            if (!_models.IsReady) return false;
            lock (_initLock) return !_initFailed;
        }
    }

    public CoreResult? Recognize(BitmapSource image)
    {
        var engine = GetEngine();
        if (engine is null) return null;

        using var bitmap = ToSkBitmap(image);
        if (bitmap is null || bitmap.Width < 4 || bitmap.Height < 4) return null;

        var options = RapidOcrOptions.PythonCompat with { ReturnWordBox = true };
        var result = engine.Detect(bitmap, options, CancellationToken.None);
        return Convert(result);
    }

    private RapidOcr? GetEngine()
    {
        if (!_models.IsReady) return null;
        lock (_initLock)
        {
            if (_ocr is not null) return _ocr;
            if (_initFailed) return null;

            try
            {
                var ocr = new RapidOcr();
                ocr.InitModels(_models.DetPath, _models.ResolvedClsPath, _models.RecPath, _models.KeysPath,
                    Math.Max(2, Environment.ProcessorCount / 2));
                _ocr = ocr;
                return _ocr;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                System.Diagnostics.Debug.WriteLine($"[PixJoin] RapidOcr 初始化失败: {ex.Message}");
                return null;
            }
        }
    }

    private static CoreResult? Convert(RapidResult source)
    {
        if (source is null || source.TextBlocks is not { Length: > 0 }) return null;

        var words = new List<OcrWord>();
        // 文档阅读顺序：按块顶边 Y 排序（RapidOcr 返回顺序不稳定）
        var blocks = source.TextBlocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text) && b.BoxPoints is { Length: >= 4 })
            .OrderBy(b => b.BoxPoints.Min(p => p.Y))
            .ToList();

        int lineIdx = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var block in blocks)
        {
            if (block.WordResults is { Length: > 0 })
            {
                foreach (var w in block.WordResults)
                {
                    var (x, y, bw, bh) = BoxBounds(w.BoxPoints);
                    if (bw < 0.5 || bh < 0.5) continue;
                    words.Add(new OcrWord
                    {
                        Text = w.Text,
                        X = x, Y = y, W = bw, H = bh,
                        LineIndex = lineIdx,
                    });
                    sb.Append(w.Text);
                }
            }
            else
            {
                var (x, y, bw, bh) = BoxBounds(block.BoxPoints);
                words.Add(new OcrWord
                {
                    Text = block.Text,
                    X = x, Y = y, W = bw, H = bh,
                    LineIndex = lineIdx,
                });
                sb.Append(block.Text);
            }
            sb.Append('\n');
            lineIdx++;
        }

        if (words.Count == 0) return null;
        return new CoreResult { Words = words, FullText = sb.ToString().TrimEnd('\n') };
    }

    private static (double X, double Y, double W, double H) BoxBounds(IReadOnlyList<SKPointI>? pts)
    {
        if (pts is null || pts.Count < 4)
            return (0, 0, 0, 0);
        double minX = pts.Min(p => (double)p.X), minY = pts.Min(p => (double)p.Y);
        double maxX = pts.Max(p => (double)p.X), maxY = pts.Max(p => (double)p.Y);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>BitmapSource → BGRA SKBitmap（CopyPixels 零拷贝字节搬移）。</summary>
    private static SKBitmap? ToSkBitmap(BitmapSource source)
    {
        try
        {
            var bmp = source;
            if (bmp.Format != PixelFormats.Bgra32)
                bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

            var width = bmp.PixelWidth;
            var height = bmp.PixelHeight;
            var stride = width * 4;
            var bytes = new byte[stride * height];
            bmp.CopyPixels(bytes, stride, 0);

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var sk = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, sk.GetPixels(), bytes.Length);
            return sk;
        }
        catch
        {
            return null;
        }
    }
}
