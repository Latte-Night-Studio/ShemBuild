using System.IO.Compression;

namespace ShemBuild.Resources;

/// <summary>Read-only access to Minecraft assets in a client JAR or resource-pack ZIP.</summary>
public sealed class MinecraftResourcePack : IDisposable
{
    private const int MaxEntries = 250_000;
    private const int MaxAssetBytes = 32 * 1024 * 1024;

    private readonly ZipArchive _archive;
    private readonly FileStream _stream;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    private MinecraftResourcePack(string path, FileStream stream, ZipArchive archive, Dictionary<string, ZipArchiveEntry> entries)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        _stream = stream;
        _archive = archive;
        _entries = entries;
    }

    public string Path { get; }
    public string Name { get; }

    public bool ContainsAsset(string assetPath) => _entries.ContainsKey(assetPath.Replace('\\', '/').TrimStart('/'));

    public static MinecraftResourcePack Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new ResourcePackException("Файл ресурсного пакета не знайдено.");
        }

        FileStream? stream = null;
        ZipArchive? archive = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntries)
            {
                throw new ResourcePackException($"Архів містить забагато файлів (ліміт {MaxEntries:N0}).");
            }

            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (normalized.Length > 0 && !normalized.EndsWith('/'))
                {
                    entries.TryAdd(normalized, entry);
                }
            }

            if (!entries.ContainsKey("assets/minecraft/blockstates/stone.json") ||
                !entries.ContainsKey("assets/minecraft/models/block/cube_all.json"))
            {
                throw new ResourcePackException("У ZIP/JAR не знайдено базові моделі Minecraft. Виберіть клієнтський .jar або ZIP ресурсного пакета з папкою assets.");
            }

            return new MinecraftResourcePack(path, stream, archive, entries);
        }
        catch (ResourcePackException)
        {
            archive?.Dispose();
            stream?.Dispose();
            throw;
        }
        catch (InvalidDataException)
        {
            archive?.Dispose();
            stream?.Dispose();
            throw new ResourcePackException("Файл не є коректним ZIP/JAR-архівом. Виберіть клієнтський .jar або ZIP ресурсного пакета.");
        }
        catch (IOException)
        {
            archive?.Dispose();
            stream?.Dispose();
            throw new ResourcePackException("Не вдалося прочитати архів. Перевірте доступ до файлу й виберіть його знову.");
        }
    }

    public bool TryReadAsset(string assetPath, out byte[] data)
    {
        if (!_entries.TryGetValue(assetPath.Replace('\\', '/').TrimStart('/'), out var entry))
        {
            data = [];
            return false;
        }

        if (entry.Length < 0 || entry.Length > MaxAssetBytes)
        {
            throw new ResourcePackException($"Ресурс {assetPath} перевищує ліміт {MaxAssetBytes / (1024 * 1024)} МБ.");
        }

        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaxAssetBytes)
            {
                throw new ResourcePackException($"Ресурс {assetPath} перевищує ліміт {MaxAssetBytes / (1024 * 1024)} МБ.");
            }

            output.Write(buffer, 0, read);
        }

        data = output.ToArray();
        return true;
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }
}

public sealed class ResourcePackException(string message) : Exception(message)
{
}
