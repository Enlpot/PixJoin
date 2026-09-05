using System.Windows.Media.Imaging;
using PixJoin.Core.Models;

namespace PixJoin.Core.Services;

/// <summary>
/// OCR 识别引擎抽象：输入贴图原始位图，输出词级结果。
/// 引擎实现可能不可用（如系统缺少语言包），调用方需先检查 <see cref="IsAvailable"/>。
/// </summary>
public interface IOcrEngine
{
    /// <summary>引擎是否可用（语言包 / 平台支持）。</summary>
    bool IsAvailable { get; }

    /// <summary>同步识别（内部实现可自行决定是否阻塞；调用方应在后台线程调用）。</summary>
    OcrResult? Recognize(BitmapSource image);
}
