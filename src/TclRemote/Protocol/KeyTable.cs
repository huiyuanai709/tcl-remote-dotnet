using System.Globalization;

namespace TclRemote;

internal static class KeyTable
{
    private static readonly (string Name, int Code)[] Ordered =
    [
        ("power", 20),
        ("up", 11),
        ("down", 12),
        ("left", 13),
        ("right", 14),
        ("ok", 15),
        ("enter", 15),
        ("back", 16),
        ("menu", 18),
        ("home", 19),
        ("tv", 19),
        ("tv_home", 19),
        ("launcher", 19),
        ("vol_up", 21),
        ("vol_down", 22),
        ("mute", 23),
        ("ch_up", 27),
        ("ch_down", 28),
        ("source", 29),
        ("input", 29),
        ("mouse_left", 39),
        ("mouse_right", 40),
    ];

    private static readonly Dictionary<string, int> Map = BuildMap();

    public static IEnumerable<(string Name, int Code)> Entries => Ordered;

    public static Dictionary<string, int> ToJsonMap()
    {
        var map = new Dictionary<string, int>(Ordered.Length);
        foreach (var (name, code) in Ordered)
            map.Add(name, code);
        return map;
    }

    public static bool TryResolve(string key, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = Canon(key);
        if (IsAllDigits(normalized))
        {
            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out code))
                return false;
            return true;
        }

        return Map.TryGetValue(normalized, out code);
    }

    public static string Canon(string key) => key.Trim().Replace('-', '_');

    public static string DescribeKnownKeys()
    {
        return string.Join(", ", Ordered.Select(static entry => entry.Name));
    }

    private static Dictionary<string, int> BuildMap()
    {
        var map = new Dictionary<string, int>(Ordered.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, code) in Ordered)
            map[name] = code;
        return map;
    }

    private static bool IsAllDigits(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (var ch in value)
        {
            if (ch is < '0' or > '9')
                return false;
        }

        return true;
    }
}
