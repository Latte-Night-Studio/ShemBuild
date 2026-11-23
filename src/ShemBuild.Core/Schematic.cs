namespace ShemBuild.Core;

/// <summary>Prototype, read-only view of the block data in a Sponge schematic.</summary>
public sealed class Schematic
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Length { get; init; }
    public required int FormatVersion { get; init; }
    public int? DataVersion { get; init; }
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public int OffsetZ { get; init; }
    public string? Name { get; init; }
    public required string[] Palette { get; init; }

    /// <summary>Palette index for each cell, with X changing fastest.</summary>
    public required int[] Blocks { get; init; }

    /// <summary>Counts non-air block states by identifier.</summary>
    public required IReadOnlyDictionary<string, int> BlockCounts { get; init; }

    public int NonAirBlockCount => BlockCounts.Values.Sum();

    public static bool IsAir(string blockState) =>
        blockState is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";
}

public sealed class SchematicReadException(string message) : Exception(message)
{
}
