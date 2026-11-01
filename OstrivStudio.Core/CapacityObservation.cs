using System.Buffers.Binary;

namespace OstrivStudio.Core;

// Read-only evidence, never an authorization to write. The offset was compared
// with the UI for one granary, warehouse and big hay barrack on this executable.
public static class CapacityObservation
{
    public const int Offset = 0x4d0;
    public static bool SupportsClass(string className) => className is "building_granary" or "building_warehouse" or "building_hay_barrack_big";

    public static float? Decode(string gameHash, string className, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (!string.Equals(gameHash, FishBulkEditor.SupportedHash, StringComparison.Ordinal) ||
            !SupportsClass(className) ||
            first.Length != 4 || !first.SequenceEqual(second)) return null;
        float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(first));
        return float.IsFinite(value) && value >= 0 && value <= 1_000_000_000f ? value : null;
    }
}
