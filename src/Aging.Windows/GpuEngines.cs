using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aging.Windows;

public static class GpuEngines
{
    public static string FindFurMark(string root) => First(
        Path.Combine(root, "FurMark", "engine", "FurMark.exe"), Path.Combine(root, "FurMark", "FurMark.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Geeks3D", "Benchmarks", "FurMark", "FurMark.exe"));

    public static string FindMark3D(string root, bool legacy11) => legacy11
        ? First(Path.Combine(root, "3DMark", "engine11", "3DMark11Cmd.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Futuremark", "3DMark 11", "bin", Environment.Is64BitOperatingSystem ? "x64" : "x86", "3DMark11Cmd.exe"))
        : First(Path.Combine(root, "3DMark", "engine", "3DMarkCmd.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UL", "3DMark", "3DMarkCmd.exe"));

    private static string First(params string[] paths) => paths.FirstOrDefault(File.Exists) ?? "";

    public static (int Width, int Height) Resolution(string text)
    {
        var parts = text.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height) || width < 100 || height < 100 || width > 32768 || height > 32768)
            throw new FormatException("无效 GPU 测试分辨率：" + text);
        return (width, height);
    }

    public static async Task WriteIdentityAsync(string executable, string output, CancellationToken ct)
    {
        await using var source = File.OpenRead(executable);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
        await File.WriteAllTextAsync(Path.Combine(output, "engine-identity.json"), JsonSerializer.Serialize(new
        { Executable = executable, FileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion, SHA256 = hash }, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
}
