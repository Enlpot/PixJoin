using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PixJoin.App.Imaging;

/// <summary>
/// PP-OCRv4 中文模型管理：检测 / 下载 / 状态。
/// NuGet 自带 PP-OCRv5 latin 模型（det/cls 通用），中文识别需 v4 ch rec + dict；
/// det 换用 v4 mobile（与 rec 同一官方组合，参数用 PythonCompat 预设）。
/// 模型缺失时后台静默下载（modelscope 官方源），下载完成前由调用方走兜底引擎。
/// </summary>
public sealed class RapidOcrModelStore
{
    /// <summary>模型所在目录（exe 旁 models\v4）。</summary>
    public string ModelDir { get; }

    public string DetPath => Path.Combine(ModelDir, "ch_PP-OCRv4_det_mobile.onnx");
    public string ClsPath => Path.Combine(ModelDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
    public string RecPath => Path.Combine(ModelDir, "ch_PP-OCRv4_rec_mobile.onnx");
    public string KeysPath => Path.Combine(ModelDir, "ppocr_keys_v1.txt");

    private static readonly (string FileName, string Url)[] Sources =
    {
        ("ch_PP-OCRv4_det_mobile.onnx",
         "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_mobile.onnx"),
        ("ch_PP-OCRv4_rec_mobile.onnx",
         "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv4/rec/ch_PP-OCRv4_rec_mobile.onnx"),
        ("ppocr_keys_v1.txt",
         "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/paddle/PP-OCRv4/rec/ch_PP-OCRv4_rec_mobile/ppocr_keys_v1.txt"),
    };

    /// <summary>v5 latin 包自带 cls（与 v4 通用）；latin rec/det 不用。</summary>
    private readonly string _bundledCls;

    private readonly object _lock = new();
    private Task? _downloadTask;
    private bool _downloadFailed;
    private DateTime _lastFailAt;

    public RapidOcrModelStore(string? modelDir = null)
    {
        ModelDir = modelDir ?? Path.Combine(AppContext.BaseDirectory, "models", "v4");
        _bundledCls = Path.Combine(AppContext.BaseDirectory, "models", "v5",
            "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
    }

    /// <summary>四件套是否就绪（含 v5 自带 cls 的可用拷贝）。</summary>
    public bool IsReady
    {
        get
        {
            if (!File.Exists(DetPath) || !File.Exists(RecPath) || !File.Exists(KeysPath))
                return false;

            var cls = File.Exists(ClsPath) ? ClsPath : _bundledCls;
            return File.Exists(cls);
        }
    }

    public string ResolvedClsPath => File.Exists(ClsPath) ? ClsPath : _bundledCls;

    public bool IsDownloading
    {
        get
        {
            lock (_lock) return _downloadTask is { IsCompleted: false };
        }
    }

    /// <summary>是否正在下载 / 刚失败（用于提示文案）。</summary>
    public string StatusText
    {
        get
        {
            if (IsDownloading) return "中文模型下载中…";
            if (_downloadFailed) return "中文模型下载失败";
            return IsReady ? "高精度模型就绪" : "等待下载";
        }
    }

    /// <summary>确保模型就绪；缺失时启动一次后台下载（幂等）。</summary>
    public void EnsureReady()
    {
        if (IsReady) return;
        lock (_lock)
        {
            if (IsReady) return;
            if (_downloadTask is { IsCompleted: false }) return;
            if (_downloadFailed && DateTime.UtcNow - _lastFailAt < TimeSpan.FromMinutes(30))
                return; // 失败后 30 分钟内不反复重试

            _downloadTask = Task.Run(DownloadAsync);
        }
    }

    private async Task DownloadAsync()
    {
        try
        {
            Directory.CreateDirectory(ModelDir);
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            foreach (var (file, url) in Sources)
            {
                var dest = Path.Combine(ModelDir, file);
                if (File.Exists(dest) && new FileInfo(dest).Length > 1024) continue;

                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var fs = new FileStream(dest + ".part", FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(fs).ConfigureAwait(false);
                fs.Flush(true);
                File.Move(dest + ".part", dest, overwrite: true);
            }

            // v5 自带 cls 拷贝到 v4 目录，保证整组自包含（后续发版/精简模型可整体分发）
            if (!File.Exists(ClsPath) && File.Exists(_bundledCls))
            {
                try { File.Copy(_bundledCls, ClsPath, overwrite: true); } catch { /* 非关键 */ }
            }

            lock (_lock) _downloadFailed = false;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _downloadFailed = true;
                _lastFailAt = DateTime.UtcNow;
            }
            System.Diagnostics.Debug.WriteLine($"[PixJoin] 模型下载失败: {ex.Message}");
        }
    }
}
