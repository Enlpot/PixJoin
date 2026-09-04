namespace PixJoin.Core.Tests;

/// <summary>极简断言工具（不引入外部测试框架，保证完全离线可跑）。</summary>
internal static class Check
{
    public static int Passed;
    public static readonly List<string> Failures = new();

    public static void True(bool condition, string name)
    {
        if (condition) { Passed++; Console.WriteLine($"  PASS  {name}"); }
        else { Failures.Add(name); Console.WriteLine($"  FAIL  {name}"); }
    }

    public static void Equal<T>(T expected, T actual, string name)
        => True(EqualityComparer<T>.Default.Equals(expected, actual), $"{name} (期望 {expected}，实际 {actual})");

    public static void Near(double expected, double actual, string name, double tol = 0.001)
        => True(Math.Abs(expected - actual) <= tol, $"{name} (期望 {expected}，实际 {actual})");

    public static void Section(string title) => Console.WriteLine($"\n[{title}]");
}
