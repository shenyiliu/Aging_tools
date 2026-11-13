namespace Aging.Core;

public static class BootSession
{
    public const string EventPrefix = "kernel-start-v1|";
    private static bool IsEventIdentity(string value)
    {
        var parts = value.Split('|');
        return parts.Length == 3 && parts[0] == "kernel-start-v1" &&
            long.TryParse(parts[1], out long record) && record > 0 &&
            long.TryParse(parts[2], out long ticks) && ticks > 0;
    }
    public static bool CanCompare(string previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current)) return false;
        if (previous.StartsWith(EventPrefix, StringComparison.Ordinal) || current.StartsWith(EventPrefix, StringComparison.Ordinal))
            return IsEventIdentity(previous) && IsEventIdentity(current);
        return true;
    }
    public static bool HasChanged(string previous, string current) => CanCompare(previous, current) && previous != current;
}
