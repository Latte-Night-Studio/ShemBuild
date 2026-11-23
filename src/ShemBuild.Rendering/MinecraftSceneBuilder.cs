using System.Numerics;
using System.Text.Json;
using ShemBuild.Core;
using ShemBuild.Resources;

namespace ShemBuild.Rendering;

/// <summary>Builds block meshes from Java blockstate/model JSON and their textures.</summary>
public sealed class MinecraftSceneBuilder
{
    public const int FaceWarningThreshold = 750_000;
    private const int AbsoluteFaceSafetyLimit = 2_000_000;
    private const int MaxTextureBytes = 128 * 1024 * 1024;
    private static readonly Vector4 MissingTint = new(0.9f, 0.12f, 0.82f, 1f);

    private readonly Dictionary<string, ResolvedModel?> _modelCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedBlock> _blockCache = new(StringComparer.Ordinal);
    private readonly Dictionary<MaterialKey, List<RenderVertex>> _vertices = [];
    private readonly Dictionary<string, byte[]> _textureCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _statesWithMissingTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _missingBlocks = new(StringComparer.Ordinal);
    private MinecraftResourcePack? _pack;
    private int _faceCount;
    private int _textureBytes;

    public RenderScene Build(Schematic schematic, MinecraftResourcePack? pack)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        _pack = pack;
        _modelCache.Clear();
        _blockCache.Clear();
        _vertices.Clear();
        _textureCache.Clear();
        _missingTextures.Clear();
        _statesWithMissingTextures.Clear();
        _missingBlocks.Clear();
        _faceCount = 0;
        _textureBytes = 0;

        var plane = checked(schematic.Width * schematic.Length);
        var blockModels = new ResolvedBlock?[schematic.Palette.Length];
        for (var y = 0; y < schematic.Height; y++)
        {
            for (var z = 0; z < schematic.Length; z++)
            {
                for (var x = 0; x < schematic.Width; x++)
                {
                    var cellIndex = y * plane + z * schematic.Width + x;
                    var paletteIndex = schematic.Blocks[cellIndex];
                    var state = schematic.Palette[paletteIndex];
                    if (Schematic.IsAir(state))
                    {
                        continue;
                    }

                    var block = blockModels[paletteIndex] ??= ResolveBlock(state);
                    if (block.IsMissing)
                    {
                        _missingBlocks.TryGetValue(state, out var count);
                        _missingBlocks[state] = count + 1;
                    }

                    var blockOrigin = new Vector3(
                        x - (schematic.Width - 1) / 2f,
                        y - (schematic.Height - 1) / 2f,
                        z - (schematic.Length - 1) / 2f);

                    foreach (var part in block.Parts)
                    {
                        foreach (var element in part.Model.Elements)
                        {
                            foreach (var face in element.Faces)
                            {
                                var cullFace = face.CullFace is null ? null : RotateDirection(face.CullFace, part.XRotation, part.YRotation);
                                if (cullFace is not null && NeighborOccludes(schematic, x, y, z, plane, cullFace, blockModels))
                                {
                                    continue;
                                }

                                var tint = face.TintIndex.HasValue ? GetBlockTint(state) : Vector4.One;
                                var material = ResolveMaterial(state, part.Model, face, tint);
                                AddFace(material, face, element.Rotation, part.XRotation, part.YRotation, blockOrigin);
                            }
                        }
                    }
                }
            }
        }

        foreach (var state in _statesWithMissingTextures)
        {
            if (schematic.BlockCounts.TryGetValue(state, out var count))
            {
                _missingBlocks[state] = count;
            }
        }

        var meshes = new List<RenderMesh>(_vertices.Count);
        foreach (var (material, vertices) in _vertices)
        {
            byte[]? textureBytes = null;
            var hasAlpha = false;
            if (material.TexturePath is not null && _textureCache.TryGetValue(material.TexturePath, out var loadedTexture))
            {
                textureBytes = loadedTexture;
                hasAlpha = HasAlphaChannel(loadedTexture);
            }

            meshes.Add(new RenderMesh(material.TexturePath, textureBytes, material.Tint, hasAlpha, vertices.ToArray()));
        }

        return new RenderScene(meshes, _faceCount, new Dictionary<string, int>(_missingBlocks), pack?.Name);
    }

    private ResolvedBlock ResolveBlock(string state)
    {
        if (_blockCache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var result = _pack is null ? CreateMissingBlock() : ResolveBlockState(state);
        _blockCache[state] = result;
        return result;
    }

    private ResolvedBlock ResolveBlockState(string state)
    {
        var (identifier, properties) = ParseBlockState(state);
        var (namespaceName, blockName) = SplitResourceLocation(identifier);
        if (!_pack!.TryReadAsset($"assets/{namespaceName}/blockstates/{blockName}.json", out var bytes))
        {
            return CreateMissingBlock();
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var parts = new List<ModelPart>();
        if (root.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Object)
        {
            var selected = ChooseVariant(variants, properties);
            if (selected is not null)
            {
                AddApply(parts, selected.Value, namespaceName);
            }
        }

        if (root.TryGetProperty("multipart", out var multipart) && multipart.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in multipart.EnumerateArray())
            {
                if (item.TryGetProperty("when", out var condition) && !ConditionMatches(condition, properties))
                {
                    continue;
                }

                if (item.TryGetProperty("apply", out var apply))
                {
                    AddApply(parts, apply, namespaceName);
                }
            }
        }

        return parts.Count == 0
            ? CreateMissingBlock()
            : new ResolvedBlock(parts, parts.Any(part => part.Model.IsFullCube), false);
    }

    private static JsonElement? ChooseVariant(JsonElement variants, IReadOnlyDictionary<string, string> properties)
    {
        JsonElement? best = null;
        var bestSpecificity = -1;
        JsonElement? emptyVariant = null;
        foreach (var variant in variants.EnumerateObject())
        {
            if (variant.Name.Length == 0)
            {
                emptyVariant = variant.Value;
                continue;
            }

            var terms = variant.Name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matches = terms.All(term =>
            {
                var separator = term.IndexOf('=');
                return separator > 0 && properties.TryGetValue(term[..separator], out var actual) && actual == term[(separator + 1)..];
            });
            if (matches && terms.Length > bestSpecificity)
            {
                best = variant.Value;
                bestSpecificity = terms.Length;
            }
        }

        return best ?? emptyVariant;
    }

    private static bool ConditionMatches(JsonElement condition, IReadOnlyDictionary<string, string> properties)
    {
        if (condition.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (condition.TryGetProperty("OR", out var alternatives) && alternatives.ValueKind == JsonValueKind.Array)
        {
            return alternatives.EnumerateArray().Any(alternative => ConditionMatches(alternative, properties));
        }

        foreach (var conditionPart in condition.EnumerateObject())
        {
            if (conditionPart.NameEquals("AND"))
            {
                if (conditionPart.Value.ValueKind == JsonValueKind.Array &&
                    !conditionPart.Value.EnumerateArray().All(alternative => ConditionMatches(alternative, properties)))
                {
                    return false;
                }

                continue;
            }

            if (!properties.TryGetValue(conditionPart.Name, out var actual))
            {
                return false;
            }

            var matches = conditionPart.Value.ValueKind == JsonValueKind.Array
                ? conditionPart.Value.EnumerateArray().Any(value => value.GetString() == actual)
                : conditionPart.Value.GetString()?.Split('|').Contains(actual, StringComparer.Ordinal) == true;
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private void AddApply(List<ModelPart> parts, JsonElement apply, string defaultNamespace)
    {
        if (apply.ValueKind == JsonValueKind.Array)
        {
            // Weighted alternatives are selected deterministically to keep previews stable.
            if (apply.GetArrayLength() > 0)
            {
                AddApply(parts, apply[0], defaultNamespace);
            }

            return;
        }

        if (apply.ValueKind != JsonValueKind.Object || !apply.TryGetProperty("model", out var modelElement))
        {
            return;
        }

        var modelName = modelElement.GetString();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        var model = ResolveModel(NormalizeResourceLocation(modelName, defaultNamespace), new HashSet<string>(StringComparer.Ordinal));
        if (model is null || model.Elements.Count == 0)
        {
            return;
        }

        var x = apply.TryGetProperty("x", out var xElement) ? xElement.GetInt32() : 0;
        var y = apply.TryGetProperty("y", out var yElement) ? yElement.GetInt32() : 0;
        parts.Add(new ModelPart(model, x, y));
    }

    private ResolvedModel? ResolveModel(string location, HashSet<string> visiting)
    {
        if (_modelCache.TryGetValue(location, out var cached))
        {
            return cached;
        }

        if (!visiting.Add(location))
        {
            return null;
        }

        var (namespaceName, modelName) = SplitResourceLocation(location);
        if (!_pack!.TryReadAsset($"assets/{namespaceName}/models/{modelName}.json", out var bytes))
        {
            _modelCache[location] = null;
            visiting.Remove(location);
            return null;
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        ResolvedModel? parent = null;
        if (root.TryGetProperty("parent", out var parentElement))
        {
            var parentName = parentElement.GetString();
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                parent = ResolveModel(NormalizeResourceLocation(parentName, namespaceName), visiting);
            }
        }

        var textures = parent is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(parent.Textures, StringComparer.Ordinal);
        if (root.TryGetProperty("textures", out var textureObject) && textureObject.ValueKind == JsonValueKind.Object)
        {
            foreach (var texture in textureObject.EnumerateObject())
            {
                if (texture.Value.ValueKind == JsonValueKind.String)
                {
                    textures[texture.Name] = texture.Value.GetString()!;
                }
            }
        }

        var elements = root.TryGetProperty("elements", out var ownElements)
            ? ParseElements(ownElements)
            : parent?.Elements ?? [];
        var model = new ResolvedModel(textures, elements, elements.Any(IsFullCubeElement));
        _modelCache[location] = model;
        visiting.Remove(location);
        return model;
    }

    private static IReadOnlyList<ModelElement> ParseElements(JsonElement elements)
    {
        if (elements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<ModelElement>();
        foreach (var element in elements.EnumerateArray())
        {
            if (!element.TryGetProperty("from", out var fromElement) || !TryReadVector3(fromElement, out var from) ||
                !element.TryGetProperty("to", out var toElement) || !TryReadVector3(toElement, out var to) ||
                !element.TryGetProperty("faces", out var faceObject) || faceObject.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ElementRotation? rotation = null;
            if (element.TryGetProperty("rotation", out var rotationElement) && rotationElement.ValueKind == JsonValueKind.Object)
            {
                var origin = rotationElement.TryGetProperty("origin", out var originElement) && TryReadVector3(originElement, out var parsedOrigin)
                    ? parsedOrigin
                    : new Vector3(8, 8, 8);
                var axis = rotationElement.TryGetProperty("axis", out var axisElement) ? axisElement.GetString() ?? "y" : "y";
                var angle = rotationElement.TryGetProperty("angle", out var angleElement) ? angleElement.GetSingle() : 0f;
                var rescale = rotationElement.TryGetProperty("rescale", out var rescaleElement) && rescaleElement.GetBoolean();
                rotation = new ElementRotation(origin, axis, angle, rescale);
            }

            var faces = new List<ModelFace>();
            foreach (var property in faceObject.EnumerateObject())
            {
                if (!TryGetFaceCorners(property.Name, from, to, out var corners) || property.Value.ValueKind != JsonValueKind.Object ||
                    !property.Value.TryGetProperty("texture", out var textureElement))
                {
                    continue;
                }

                var uv = property.Value.TryGetProperty("uv", out var uvElement) && TryReadVector4(uvElement, out var parsedUv)
                    ? parsedUv
                    : DefaultUv(property.Name, from, to);
                var uvRotation = property.Value.TryGetProperty("rotation", out var faceRotation) ? faceRotation.GetInt32() : 0;
                var cullFace = property.Value.TryGetProperty("cullface", out var cullElement) ? cullElement.GetString() : null;
                var tintIndex = property.Value.TryGetProperty("tintindex", out var tintElement) ? tintElement.GetInt32() : (int?)null;
                faces.Add(new ModelFace(property.Name, corners, uv, uvRotation, cullFace, tintIndex, textureElement.GetString() ?? string.Empty));
            }

            parsed.Add(new ModelElement(from, to, rotation, faces));
        }

        return parsed;
    }

    private MaterialKey ResolveMaterial(string state, ResolvedModel model, ModelFace face, Vector4 tint)
    {
        if (_pack is null)
        {
            return new MaterialKey(null, MissingTint);
        }

        var textureReference = face.TextureReference;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (textureReference.StartsWith('#'))
        {
            var key = textureReference[1..];
            if (!visited.Add(key) || !model.Textures.TryGetValue(key, out textureReference!))
            {
                _statesWithMissingTextures.Add(state);
                return new MaterialKey(null, MissingTint);
            }
        }

        var (namespaceName, textureName) = SplitResourceLocation(textureReference);
        textureName = textureName.StartsWith("textures/", StringComparison.Ordinal) ? textureName[9..] : textureName;
        var texturePath = $"assets/{namespaceName}/textures/{textureName}.png";
        var textureBytes = LoadTexture(texturePath);
        if (textureBytes is null)
        {
            _missingTextures.Add(texturePath);
            _statesWithMissingTextures.Add(state);
            return new MaterialKey(null, MissingTint);
        }

        return new MaterialKey(texturePath, tint);
    }

    private byte[]? LoadTexture(string path)
    {
        if (_textureCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        if (_missingTextures.Contains(path) || !_pack!.TryReadAsset(path, out var bytes) || !IsPng(bytes))
        {
            _missingTextures.Add(path);
            return null;
        }

        if (_textureBytes + bytes.Length > MaxTextureBytes)
        {
            throw new ResourcePackException("Використані текстури перевищили ліміт 128 МБ. Виберіть компактнішу схему або ресурсний пакет.");
        }

        _textureBytes += bytes.Length;
        _textureCache[path] = bytes;
        return bytes;
    }

    private void AddFace(MaterialKey material, ModelFace face, ElementRotation? elementRotation, int xRotation, int yRotation, Vector3 blockOrigin)
    {
        if (_faceCount >= AbsoluteFaceSafetyLimit)
        {
            throw new SchematicReadException(
                $"Сцена перевищила аварійний ліміт {AbsoluteFaceSafetyLimit:N0} відкритих граней. Щоб уникнути нестачі пам’яті, цю схему не відображено.");
        }

        if (!_vertices.TryGetValue(material, out var vertices))
        {
            vertices = [];
            _vertices.Add(material, vertices);
        }

        var points = new Vector3[4];
        for (var i = 0; i < points.Length; i++)
        {
            var local = TransformVertex(face.Corners[i], elementRotation, xRotation, yRotation) / 16f - new Vector3(0.5f);
            points[i] = blockOrigin + local;
        }

        var uvs = FaceUvs(face.Uv, face.UvRotation);
        vertices.Add(new RenderVertex(points[0], uvs[0]));
        vertices.Add(new RenderVertex(points[1], uvs[1]));
        vertices.Add(new RenderVertex(points[2], uvs[2]));
        vertices.Add(new RenderVertex(points[0], uvs[0]));
        vertices.Add(new RenderVertex(points[2], uvs[2]));
        vertices.Add(new RenderVertex(points[3], uvs[3]));
        _faceCount++;
    }

    private bool NeighborOccludes(Schematic schematic, int x, int y, int z, int plane, string cullFace, ResolvedBlock?[] paletteCache)
    {
        var (dx, dy, dz) = cullFace switch
        {
            "west" => (-1, 0, 0), "east" => (1, 0, 0),
            "down" => (0, -1, 0), "up" => (0, 1, 0),
            "north" => (0, 0, -1), "south" => (0, 0, 1),
            _ => (0, 0, 0)
        };
        var nx = x + dx;
        var ny = y + dy;
        var nz = z + dz;
        if (nx < 0 || nx >= schematic.Width || ny < 0 || ny >= schematic.Height || nz < 0 || nz >= schematic.Length)
        {
            return false;
        }

        var paletteIndex = schematic.Blocks[ny * plane + nz * schematic.Width + nx];
        if (Schematic.IsAir(schematic.Palette[paletteIndex]))
        {
            return false;
        }

        var neighbor = paletteCache[paletteIndex] ??= ResolveBlock(schematic.Palette[paletteIndex]);
        return neighbor.IsFullCube;
    }

    private static ResolvedBlock CreateMissingBlock()
    {
        var faces = new List<ModelFace>();
        foreach (var direction in new[] { "down", "up", "north", "south", "west", "east" })
        {
            if (TryGetFaceCorners(direction, Vector3.Zero, new Vector3(16), out var corners))
            {
                faces.Add(new ModelFace(direction, corners, new Vector4(0, 0, 16, 16), 0, direction, null, string.Empty));
            }
        }

        var element = new ModelElement(Vector3.Zero, new Vector3(16), null, faces);
        var model = new ResolvedModel(new Dictionary<string, string>(), [element], true);
        return new ResolvedBlock([new ModelPart(model, 0, 0)], true, true);
    }

    private static bool IsFullCubeElement(ModelElement element) =>
        element.Rotation is null && element.From.X <= 0 && element.From.Y <= 0 && element.From.Z <= 0 &&
        element.To.X >= 16 && element.To.Y >= 16 && element.To.Z >= 16;

    private static Vector4 GetBlockTint(string state)
    {
        var separator = state.IndexOf('[');
        var name = state[..(separator < 0 ? state.Length : separator)];
        if (name.Contains("water", StringComparison.Ordinal)) return new Vector4(0.25f, 0.46f, 0.89f, 1f);
        if (name.Contains("leaves", StringComparison.Ordinal) || name.Contains("grass", StringComparison.Ordinal) ||
            name.Contains("fern", StringComparison.Ordinal) || name.Contains("vine", StringComparison.Ordinal))
        {
            return new Vector4(0.58f, 0.76f, 0.35f, 1f);
        }

        return Vector4.One;
    }

    private static Vector3 TransformVertex(Vector3 point, ElementRotation? elementRotation, int stateX, int stateY)
    {
        if (elementRotation is { } rotation)
        {
            var relative = point - rotation.Origin;
            var radians = rotation.Angle * MathF.PI / 180f;
            relative = Rotate(relative, rotation.Axis, radians);
            if (rotation.Rescale && MathF.Abs(rotation.Angle) > 0.01f)
            {
                var scale = 1f / MathF.Cos(radians);
                relative = rotation.Axis switch
                {
                    "x" => new Vector3(relative.X, relative.Y * scale, relative.Z * scale),
                    "y" => new Vector3(relative.X * scale, relative.Y, relative.Z * scale),
                    _ => new Vector3(relative.X * scale, relative.Y * scale, relative.Z)
                };
            }

            point = relative + rotation.Origin;
        }

        point -= new Vector3(8);
        point = Rotate(point, "x", stateX * MathF.PI / 180f);
        point = Rotate(point, "y", stateY * MathF.PI / 180f);
        return point + new Vector3(8);
    }

    private static Vector3 Rotate(Vector3 vector, string axis, float radians) => axis switch
    {
        "x" => Vector3.Transform(vector, Matrix4x4.CreateRotationX(radians)),
        "y" => Vector3.Transform(vector, Matrix4x4.CreateRotationY(radians)),
        _ => Vector3.Transform(vector, Matrix4x4.CreateRotationZ(radians))
    };

    private static string RotateDirection(string direction, int xRotation, int yRotation)
    {
        var normal = direction switch
        {
            "west" => -Vector3.UnitX, "east" => Vector3.UnitX,
            "down" => -Vector3.UnitY, "up" => Vector3.UnitY,
            "north" => -Vector3.UnitZ, "south" => Vector3.UnitZ,
            _ => Vector3.Zero
        };
        normal = Rotate(normal, "x", xRotation * MathF.PI / 180f);
        normal = Rotate(normal, "y", yRotation * MathF.PI / 180f);
        if (normal.X < -0.5f) return "west";
        if (normal.X > 0.5f) return "east";
        if (normal.Y < -0.5f) return "down";
        if (normal.Y > 0.5f) return "up";
        if (normal.Z < -0.5f) return "north";
        if (normal.Z > 0.5f) return "south";
        return direction;
    }

    private static bool TryGetFaceCorners(string direction, Vector3 from, Vector3 to, out Vector3[] corners)
    {
        var x0 = from.X; var y0 = from.Y; var z0 = from.Z;
        var x1 = to.X; var y1 = to.Y; var z1 = to.Z;
        corners = direction switch
        {
            "down" => [new(x0, y0, z1), new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1)],
            "up" => [new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0)],
            "north" => [new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(x1, y0, z0)],
            "south" => [new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new(x0, y0, z1)],
            "west" => [new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0)],
            "east" => [new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1)],
            _ => []
        };

        return corners.Length == 4;
    }

    private static Vector4 DefaultUv(string direction, Vector3 from, Vector3 to) => direction switch
    {
        "down" or "up" => new Vector4(from.X, from.Z, to.X, to.Z),
        "north" => new Vector4(16 - to.X, 16 - to.Y, 16 - from.X, 16 - from.Y),
        "south" => new Vector4(from.X, 16 - to.Y, to.X, 16 - from.Y),
        "west" => new Vector4(from.Z, 16 - to.Y, to.Z, 16 - from.Y),
        "east" => new Vector4(16 - to.Z, 16 - to.Y, 16 - from.Z, 16 - from.Y),
        _ => new Vector4(0, 0, 16, 16)
    };

    private static Vector2[] FaceUvs(Vector4 uv, int rotation)
    {
        var values = new[]
        {
            new Vector2(uv.X / 16f, uv.Y / 16f),
            new Vector2(uv.X / 16f, uv.W / 16f),
            new Vector2(uv.Z / 16f, uv.W / 16f),
            new Vector2(uv.Z / 16f, uv.Y / 16f)
        };
        var steps = ((rotation % 360) + 360) % 360 / 90;
        return steps == 0 ? values : Enumerable.Range(0, 4).Select(index => values[(index + steps) % 4]).ToArray();
    }

    private static bool TryReadVector3(JsonElement element, out Vector3 vector)
    {
        vector = default;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3) return false;
        vector = new Vector3(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());
        return true;
    }

    private static bool TryReadVector4(JsonElement element, out Vector4 vector)
    {
        vector = default;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 4) return false;
        vector = new Vector4(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle(), element[3].GetSingle());
        return true;
    }

    private static (string Identifier, Dictionary<string, string> Properties) ParseBlockState(string value)
    {
        var open = value.IndexOf('[');
        if (open < 0 || !value.EndsWith(']')) return (value, new Dictionary<string, string>(StringComparer.Ordinal));
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value[(open + 1)..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0) properties[pair[..separator]] = pair[(separator + 1)..];
        }

        return (value[..open], properties);
    }

    private static string NormalizeResourceLocation(string location, string defaultNamespace)
    {
        var (namespaceName, path) = SplitResourceLocation(location, defaultNamespace);
        return $"{namespaceName}:{path.TrimStart('/')}";
    }

    private static (string Namespace, string Path) SplitResourceLocation(string location, string defaultNamespace = "minecraft")
    {
        var separator = location.IndexOf(':');
        return separator >= 0 ? (location[..separator], location[(separator + 1)..]) : (defaultNamespace, location);
    }

    private static bool HasAlphaChannel(byte[] png) =>
        png.Length > 25 && png[25] is 3 or 4 or 6;

    private static bool IsPng(byte[] data) =>
        data.Length > 25 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private readonly record struct MaterialKey(string? TexturePath, Vector4 Tint);
    private sealed record ResolvedBlock(IReadOnlyList<ModelPart> Parts, bool IsFullCube, bool IsMissing);
    private sealed record ModelPart(ResolvedModel Model, int XRotation, int YRotation);
    private sealed record ResolvedModel(Dictionary<string, string> Textures, IReadOnlyList<ModelElement> Elements, bool IsFullCube);
    private sealed record ModelElement(Vector3 From, Vector3 To, ElementRotation? Rotation, IReadOnlyList<ModelFace> Faces);
    private sealed record ElementRotation(Vector3 Origin, string Axis, float Angle, bool Rescale);
    private sealed record ModelFace(string Direction, Vector3[] Corners, Vector4 Uv, int UvRotation, string? CullFace, int? TintIndex, string TextureReference);
}
