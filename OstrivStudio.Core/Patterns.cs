namespace OstrivStudio.Core;

// Compatibility probes observed in Resource Editor 3.50963.1. A match is
// evidence only: it does not authorize a write or validate an object layout.
public sealed record PatternDefinition(string Name, string Pattern);
public sealed record PatternResult(string Name, int Matches);

public static class Patterns
{
    public static readonly PatternDefinition[] Known =
    [
        new("Capacity", "9C 13 00 48 8B 03 F3 0F 59 B3 ?? ?? ?? ?? FF 90 08 04 00 00 0F 2F C6 0F"),
        new("Granary storage", "8D 53 30 48 8B 01 FF 90 ?? ?? ?? ?? F3 0F 11 43 34 E9 8B 00 00 00"),
        new("Warehouse storage", "49 8B 06 49 8B CE FF 90 ?? ?? ?? ?? 49 63 8E F8 06 00 00 84 C0 0F"),
        new("Trading post storage", "48 8B D3 48 8B CE FF 90 ?? ?? ?? ?? F3 0F 59 C7 0F 57 C9 F3 0F 5A"),
        new("Farm storage", "89 BB 80 04 00 00 48 89 BB ?? ?? ?? ?? 48 89 BB 90 04 00 00 48 89 BB"),
        new("Forestry storage", "CA 7C D6 45 8B CC 66 45 89 8D ?? ?? ?? ?? 8B FB 4C 63 89 18 2B 00 00 4D"),
        new("Resource count", "75 D7 45 84 D2 75 57 8B 83 ?? ?? ?? ?? 41 8B CD 2B C1 49 8B D3"),
        new("Resource pointer", "49 8B CA 48 8D 14 80 48 8B 83 ?? ?? ?? ?? 4C 8D 04 90 48 8B D3"),
        new("Next cart", "84 C0 74 0D 48 8B 04 33 83 B8 A8 04 00 00 00 74 02 FF C5 41 FF C6 48 83 C3 ?? 44 3B B7 C0 07 00 00"),
        new("Next horse wagon", "4C 8D 44 24 40 C7 44 24 40 9A 00 00 00 E8 ?? ?? ?? ?? 48 89 43 08 FF C7 48 83 C6 ?? 3B BD 70 08 00 00"),
        new("Cart count", "8B 81 D8 07 00 00 39 81 ?? ?? ?? ?? 7D 1E 48 8B 01 FF 90 D0 07 00"),
        new("Cart pointer", "49 8B 06 49 8B CE FF 90 ?? ?? ?? ?? 49 63 8E F8 06 00 00 84 C0 0F"),
        new("Cart health", "00 48 89 03 33 C0 48 89 83 ?? ?? ?? ?? 48 89 83 F8 01 00 00 48 89 83"),
        new("Wagon count", "0F 84 F0 06 00 00 49 8D 8F ?? ?? ?? ?? BA 01 00 00 00 44 8B C2 FF 15"),
        new("Wagon pointer", "CF E8 E0 F1 24 00 49 8D 97 ?? ?? ?? ?? 48 8B CF E8 C1 81 FC FF 49 8D"),
        new("Wagon health", "89 BB 70 04 00 00 89 BB ?? ?? ?? ?? 48 89 BB 88 04 00 00 48 89 BB"),
        new("Farm field count", "88 AB B8 08 00 00 89 AB ?? ?? ?? ?? 89 AB D0 08 00 00 48 89 AB C8"),
        new("Farm field pointer", "56 57 48 83 EC 30 48 8D B1 ?? ?? ?? ?? 48 8B F9 8B 0E 48 8B EA 85 C9"),
        new("Well water", "89 BB A8 01 00 00 48 89 BB ?? ?? ?? ?? 89 BB B0 01 00 00 C7 83 B4 01"),
        new("Market bench", "89 83 30 03 00 00 48 89 83 ?? ?? ?? ?? 89 83 38 03 00 00 89 83 48 03")
    ];

    public static int CountMatches(ReadOnlySpan<byte> data, string pattern)
    {
        var pieces = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0) throw new ArgumentException("Pattern is empty.", nameof(pattern));
        int?[] tokens = pieces.Select(p => p == "??" ? (int?)null : Convert.ToByte(p, 16)).ToArray();
        int anchor = Array.FindIndex(tokens, x => x.HasValue);
        if (anchor < 0) throw new ArgumentException("Pattern must contain a fixed byte.", nameof(pattern));
        int count = 0;
        for (int i = 0; i <= data.Length - tokens.Length; i++)
        {
            if (data[i + anchor] != tokens[anchor]) continue;
            bool matched = true;
            for (int j = 0; j < tokens.Length; j++)
                if (tokens[j].HasValue && data[i + j] != tokens[j]) { matched = false; break; }
            if (matched) count++;
        }
        return count;
    }
}
