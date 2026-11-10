using Aging.Windows;
using System.Buffers.Binary;
using Xunit;

namespace Aging.Windows.Tests;

public sealed class CoreTempTests
{
    [Fact]
    public void DecodesHottestCoreAndRejectsUninitializedAndMissingData()
    {
        byte[] data = Sample(2, 1, 62.5f, 74f);
        Assert.Equal(74d, CoreTempReader.Decode(data)!.TemperatureCelsius);
        Assert.Null(CoreTempReader.Decode(new byte[CoreTempReader.DataSize]));
        Assert.Null(CoreTempReader.Decode(new byte[20]));
        Assert.Null(CoreTempReader.Decode(Sample(1, 1, float.NaN)));
        Assert.Null(CoreTempReader.Decode(Sample(1, 1, 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1540), 129);
        Assert.Null(CoreTempReader.Decode(data));
    }

    [Fact]
    public void NormalizesFahrenheitAndDistanceToTjMax()
    {
        byte[] data = Sample(2, 1, 40, 25);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024), 100);
        data[2685] = 1;
        Assert.Equal(75d, CoreTempReader.Decode(data)!.TemperatureCelsius);
        data = Sample(1, 1, 176);
        data[2684] = 1;
        Assert.Equal(80d, CoreTempReader.Decode(data)!.TemperatureCelsius, 5);
    }

    private static byte[] Sample(uint cores, uint cpus, params float[] values)
    {
        byte[] data = new byte[CoreTempReader.DataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1536), cores);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1540), cpus);
        for (int index = 0; index < values.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(1544 + index * 4), values[index]);
        return data;
    }
}
