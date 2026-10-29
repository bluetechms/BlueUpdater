using System.Text.RegularExpressions;

namespace BlueUpdater;

// Same rules as the api (AClientUpdate.ts): 2-4 numeric parts, compared as 4-part numbers. System.Version
// and semver both reject or mis-order "1.0.0.0"-style strings, so this is explicit.
public static class VersionUtil
{
    private static readonly Regex Valid = new(@"^\d{1,5}(\.\d{1,5}){1,3}$", RegexOptions.Compiled);

    public static bool IsValid(string? v) => v != null && Valid.IsMatch(v);

    public static int[] Parse(string? v)
    {
        var p = new int[4];
        if (string.IsNullOrWhiteSpace(v)) return p;
        var parts = v.Split('.');
        for (var i = 0; i < 4 && i < parts.Length; i++)
            p[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return p;
    }

    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (var i = 0; i < 4; i++)
            if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
        return 0;
    }

    // "1.1.0.0" for any of "1.1", "1.1.0.0+abc", "1.1.0.0-beta"; null when nothing numeric is there.
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Regex.Match(raw, @"\d+(\.\d+){0,3}");
        if (!m.Success) return null;
        var p = Parse(m.Value);
        return $"{p[0]}.{p[1]}.{p[2]}.{p[3]}";
    }
}
