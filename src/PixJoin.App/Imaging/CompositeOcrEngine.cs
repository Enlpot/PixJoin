using System;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;
using PixJoin.Core.Services;

namespace PixJoin.App.Imaging;

/// <summary>
/// 组合 OCR 引擎：高精度（PP-OCRv4）优先，不可用 / 识别失败时自动兜底到系统引擎。
/// 调用方无感，只要求 IsAvailable 与 Recognize 语义与单引擎一致。
/// </summary>
public sealed class CompositeOcrEngine : IOcrEngine
{
    private readonly IOcrEngine _primary;
    private readonly IOcrEngine _fallback;

    public CompositeOcrEngine(IOcrEngine primary, IOcrEngine fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>切换主引擎语言（RapidOcrEngine 多语言；非 Rapid 引擎忽略）。</summary>
    public void SetPrimaryLanguage(string language)
    {
        if (_primary is RapidOcrEngine rapid) rapid.SetLanguage(language);
    }

    /// <summary>任一引擎可用即可工作。</summary>
    public bool IsAvailable => _primary.IsAvailable || _fallback.IsAvailable;

    public OcrResult? Recognize(BitmapSource image)
    {
        if (_primary.IsAvailable)
        {
            try
            {
                var r = _primary.Recognize(image);
                if (r is { Words.Count: > 0 }) return r;
            }
            catch
            {
                // 主引擎异常 → 兜底
            }
        }
        return _fallback.IsAvailable ? _fallback.Recognize(image) : null;
    }
}
