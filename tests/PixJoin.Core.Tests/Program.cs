using PixJoin.Core.Tests;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("PixJoin.Core 单元测试");
Console.WriteLine("=====================================");

SnapEngineTests.Run();
GroupManagerTests.Run();
ExportTests.Run();
StickerGeometryTests.Run();
OcrSelectionTests.Run();
ImageProcessorTests.Run();
AnnotationRendererTests.Run();

Console.WriteLine("\n=====================================");
Console.WriteLine($"通过 {Check.Passed} 项，失败 {Check.Failures.Count} 项");

if (Check.Failures.Count > 0)
{
    Console.WriteLine("\n失败用例：");
    foreach (var f in Check.Failures) Console.WriteLine($"  - {f}");
    return 1;
}

Console.WriteLine("全部通过。");
return 0;
