using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Aging.Windows;

public sealed record CoreTempReading(double TemperatureCelsius, string CpuName, int CoreCount, int CpuCount);

/// <summary>Reads Core Temp's published shared-memory protocol, without caching sensor values.</summary>
public static class CoreTempReader
{
    public const string MappingName = "CoreTempMappingObject";
    public const int DataSize = 2688;

    public static CoreTempReading? Read()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, DataSize, MemoryMappedFileAccess.Read);
            byte[] data = new byte[DataSize];
            if (view.ReadArray(0, data, 0, data.Length) != data.Length) return null;
            return Decode(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    // Layout: uiLoad[256], uiTjMax[128], uiCoreCnt, uiCPUCnt, fTemp[256],
    // fVID/fCPUSpeed/fFSBSpeed/fMultiplier, sCPUName[100], Fahrenheit/Delta flags.
    // Source contract: official Core Temp SDK, GetCoreTempInfo.h. No SDK DLL is loaded.
    public static CoreTempReading? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < DataSize) return null;
        uint cores = BinaryPrimitives.ReadUInt32LittleEndian(data[1536..]);
        uint cpus = BinaryPrimitives.ReadUInt32LittleEndian(data[1540..]);
        if (cores is 0 or > 256 || cpus is 0 or > 128 || (ulong)cores * cpus > 256) return null;
        bool fahrenheit = data[2684] != 0, delta = data[2685] != 0;
        double? highest = null;
        for (int cpu = 0; cpu < cpus; cpu++)
        {
            uint tjMax = BinaryPrimitives.ReadUInt32LittleEndian(data[(1024 + cpu * 4)..]);
            for (int core = 0; core < cores; core++)
            {
                int index = cpu * (int)cores + core;
                double temperature = BinaryPrimitives.ReadSingleLittleEndian(data[(1544 + index * 4)..]);
                if (!double.IsFinite(temperature)) continue;
                if (delta)
                {
                    if (tjMax == 0 || temperature < 0) continue;
                    temperature = tjMax - temperature;
                }
                if (fahrenheit) temperature = (temperature - 32) / 1.8;
                // Zero/uninitialized slots and driver error sentinels are not sensor evidence.
                if (temperature <= 0 || temperature > 150) continue;
                highest = highest.HasValue ? Math.Max(highest.Value, temperature) : temperature;
            }
        }
        if (!highest.HasValue) return null;
        string name = Encoding.ASCII.GetString(data.Slice(2584, 100)).Split('\0')[0].Trim();
        return new(highest.Value, name, (int)cores, (int)cpus);
    }
}
