namespace Aging.Core;

/// <summary>Timing rules recovered from Reboot.au3 and Sleep.au3, independently of OS actions.</summary>
public static class LegacyTiming
{
    public static IReadOnlyList<string> Check(AgingItem item, IReadOnlyList<double> previousSeconds, double seconds)
    {
        if (!item.TimeCheck || !item.IsPower) return [];
        // Reboot compares against the mean including this completed boot. Sleep compares
        // against its preceding mean, and starts checking at the second transition.
        if (item.Kind != TaskKind.Reboot && previousSeconds.Count == 0) return [];
        double average = item.Kind == TaskKind.Reboot
            ? (previousSeconds.Sum() + seconds) / (previousSeconds.Count + 1)
            : previousSeconds.Average();
        var errors = new List<string>();
        if (!double.IsFinite(seconds) || !double.IsFinite(average) || seconds <= 0 || average <= 0 || seconds / average >= 2)
            errors.Add($"耗时检查异常：{seconds:0.0} 秒，比较均值 {average:0.0} 秒");
        if (item.Kind is TaskKind.S3 or TaskKind.S4 or TaskKind.CS && seconds < item.SleepSeconds)
            errors.Add($"睡眠耗时 {seconds:0.0} 秒，小于设置的 {item.SleepSeconds} 秒");
        return errors;
    }

    // All recovered executables use this local civil-time reference for legacy INI stamps.
    public static long Timestamp(DateTimeOffset time) => (long)(time.DateTime - new DateTime(2022, 7, 20)).TotalSeconds;
}
