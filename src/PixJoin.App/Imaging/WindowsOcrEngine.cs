using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using PixJoin.Core.Models;
using PixJoin.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinOcrResult = Windows.Media.Ocr.OcrResult;
using WinOcrLine = Windows.Media.Ocr.OcrLine;
using WinOcrWord = Windows.Media.Ocr.OcrWord;
using OcrEngine = Windows.Media.Ocr.OcrEngine;

namespace PixJoin.App.Imaging;

/// <summary>
/// Windows 系统内置 OCR 引擎（Windows.Media.Ocr，Win10 1809+）。
/// 零第三方依赖、离线、中文支持随系统语言包。
/// 词框坐标为图片物理像素（SoftwareBitmap 输入）。
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private static OcrEngine? _engine;

    public bool IsAvailable => TryGetEngine() is not null;

    public OcrResult? Recognize(BitmapSource image)
    {
        var engine = TryGetEngine();
        if (engine is null) return null;

        var png = EncodePng(image);
        if (png is null || png.Length == 0) return null;

        using var ms = new MemoryStream(png);
        using var stream = new InMemoryRandomAccessStream();
        var dataWriter = new DataWriter(stream);
        dataWriter.WriteBytes(png);
        dataWriter.StoreAsync().AsTask().GetAwaiter().GetResult();
        dataWriter.DetachStream();
        stream.Seek(0);

        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        using var software = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();

        var result = engine.RecognizeAsync(software).AsTask().GetAwaiter().GetResult();
        return Convert(result);
    }

    private static OcrResult? Convert(WinOcrResult source)
    {
        if (source is null) return null;

        var words = new List<OcrWord>();
        int lineIdx = 0;
        foreach (var line in source.Lines)
        {
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWord
                {
                    Text = w.Text,
                    X = r.X,
                    Y = r.Y,
                    W = r.Width,
                    H = r.Height,
                    LineIndex = lineIdx,
                });
            }
            lineIdx++;
        }

        return new OcrResult { Words = words, FullText = source.Text ?? "" };
    }

    private static OcrEngine? TryGetEngine()
    {
        if (_engine is not null) return _engine;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            _engine = null;
        }
        return _engine;
    }

    private static byte[]? EncodePng(BitmapSource image)
    {
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
