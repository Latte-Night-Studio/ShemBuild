using System.Numerics;

namespace ShemBuild.Rendering;

public sealed record RenderVertex(Vector3 Position, Vector2 TextureCoordinate);

public sealed record RenderMesh(
    string? TexturePath,
    byte[]? TextureBytes,
    Vector4 Tint,
    bool HasAlpha,
    IReadOnlyList<RenderVertex> Vertices);

public sealed record RenderScene(
    IReadOnlyList<RenderMesh> Meshes,
    int ExposedFaces,
    IReadOnlyDictionary<string, int> MissingBlocks,
    string? ResourcePackName);
