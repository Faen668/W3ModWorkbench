namespace W3ModWorkbench;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.##} {Units[unit]}";
    }

    public static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    public static string Count(int n, string noun, string plural) => $"{n:N0} {(n == 1 ? noun : plural)}";
}
