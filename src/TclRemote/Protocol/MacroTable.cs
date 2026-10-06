using System.Globalization;

namespace TclRemote;

internal readonly record struct MacroStep(string Key, int Code, TimeSpan PauseBefore);

internal static class MacroTable
{
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(2500);

    // Sequence data only. The send path walks these steps; it does not special-case hdmi1.
    // A positive PauseBefore is the gap before that key. Override it with SessionOptions.MacroPause.
    private static readonly (string Name, MacroStepSpec[] Steps)[] Definitions =
    [
        ("hdmi1",
        [
            new("home", 0),
            new("source", 2500),
        ]),
    ];

    private static readonly Dictionary<string, MacroStepSpec[]> Map = Build();

    public static IEnumerable<string> Names => Definitions.Select(static definition => definition.Name);

    public static bool TryParsePause(string? text, out TimeSpan pause)
    {
        pause = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            return false;
        if (milliseconds is < 0 or > 60_000)
            return false;
        pause = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    public static bool TryExpand(string name, TimeSpan? pauseOverride, out MacroStep[] steps)
    {
        steps = [];
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (!Map.TryGetValue(KeyTable.Canon(name), out var specs))
            return false;

        var built = new MacroStep[specs.Length];
        for (var i = 0; i < specs.Length; i++)
        {
            if (!KeyTable.TryResolve(specs[i].Key, out var code))
                throw new InvalidOperationException($"宏 {name} 引用了未知按键 {specs[i].Key}");

            var pause = TimeSpan.FromMilliseconds(specs[i].PauseBeforeMilliseconds);
            if (pauseOverride is not null && specs[i].PauseBeforeMilliseconds > 0)
                pause = pauseOverride.Value;
            built[i] = new MacroStep(specs[i].Key, code, pause);
        }

        steps = built;
        return true;
    }

    private static Dictionary<string, MacroStepSpec[]> Build()
    {
        var map = new Dictionary<string, MacroStepSpec[]>(Definitions.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, steps) in Definitions)
            map.Add(name, steps);
        return map;
    }

    private readonly record struct MacroStepSpec(string Key, int PauseBeforeMilliseconds);
}
