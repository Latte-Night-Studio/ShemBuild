using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ShemBuild.Core;

/// <summary>
/// A deliberately narrow reader used to evaluate the 3D prototype. It does not
/// establish compatibility for any Minecraft release or claim complete .schem support.
/// </summary>
public sealed class SchematicReader
{
    private const long MaxCompressedBytes = 64L * 1024 * 1024;
    private const long MaxExpandedBytes = 128L * 1024 * 1024;
    private const int MaxVolume = 20_000_000;
    private const int MaxPaletteEntries = 65_536;
    private const int MaxNbtItems = 2_000_000;

    public Schematic Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.GetExtension(path).Equals(".schem", StringComparison.OrdinalIgnoreCase))
        {
            throw new SchematicReadException("Підтримується лише Sponge Java Edition .schem. Legacy .schematic, .nbt і Bedrock .mcstructure ще не підтримуються.");
        }

        try
        {
            using var source = File.OpenRead(path);
            if (source.Length == 0)
            {
                throw new SchematicReadException("Файл порожній. Виберіть інший файл Sponge .schem.");
            }

            if (source.Length > MaxCompressedBytes)
            {
                throw new SchematicReadException("Стиснений файл перевищує прототипний ліміт 64 МБ. Для перевірки виберіть меншу схему.");
            }

            Span<byte> signature = stackalloc byte[2];
            if (source.Read(signature) != signature.Length || signature[0] != 0x1F || signature[1] != 0x8B)
            {
                throw new SchematicReadException("Файл не є GZip Sponge Java Edition .schem. Legacy .schematic, Bedrock .mcstructure та інші формати не підтримуються.");
            }
            source.Position = 0;

            using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: false);
            using var payload = ReadBounded(gzip);
            payload.Position = 0;

            using var reader = new BinaryReader(payload, Encoding.UTF8, leaveOpen: true);
            var nbt = new NbtReader(reader, MaxNbtItems).ReadRoot();
            return ToSchematic(nbt.RootName, nbt.Value);
        }
        catch (SchematicReadException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw new SchematicReadException("Файл не є коректним GZip-файлом Sponge .schem. Перевірте файл або виберіть інший.");
        }
        catch (EndOfStreamException)
        {
            throw new SchematicReadException("Файл обірваний або його структура NBT пошкоджена. Перевірте файл або виберіть інший.");
        }
        catch (IOException)
        {
            throw new SchematicReadException("Не вдалося прочитати файл. Перевірте доступ до нього та спробуйте ще раз.");
        }
        catch (OverflowException)
        {
            throw new SchematicReadException("Розміри схеми завеликі або некоректні. Виберіть менший файл.");
        }
    }

    private static MemoryStream ReadBounded(Stream input)
    {
        var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return result;
            }

            if (result.Length + read > MaxExpandedBytes)
            {
                result.Dispose();
                throw new SchematicReadException("Розпакований файл перевищує прототипний ліміт 128 МБ. Для перевірки виберіть меншу схему.");
            }

            result.Write(buffer, 0, read);
        }
    }

    private static Schematic ToSchematic(string rootName, Dictionary<string, object?> root)
    {
        var schematic = rootName == "Schematic"
            ? root
            : GetCompound(root, "Schematic", "У файлі не знайдено кореневий тег Sponge Schematic. Формат або видання не підтримується.");

        var version = GetInt(schematic, "Version", "У файлі відсутня версія формату схеми.");
        if (version is not (2 or 3))
        {
            throw new SchematicReadException($"Прототип читає структури Sponge .schem версій 2 і 3; версія {version} не підтримується цим прототипом.");
        }

        var width = GetUnsignedShort(schematic, "Width");
        var height = GetUnsignedShort(schematic, "Height");
        var length = GetUnsignedShort(schematic, "Length");
        if (width == 0 || height == 0 || length == 0)
        {
            throw new SchematicReadException("Розміри схеми мають бути більшими за нуль.");
        }

        var volumeLong = (long)width * height * length;
        if (volumeLong > MaxVolume)
        {
            throw new SchematicReadException($"Схема містить {volumeLong:N0} позицій; прототипний ліміт — {MaxVolume:N0}. Для перевірки виберіть меншу схему.");
        }

        var blockContainer = version == 3
            ? GetCompound(schematic, "Blocks", "У схемі версії 3 відсутній контейнер Blocks.")
            : schematic;

        var paletteTag = GetFirstCompound(blockContainer, ["Palette", "BlockPalette"])
            ?? throw new SchematicReadException("У схемі не знайдено палітру блоків.");
        var palette = ReadPalette(paletteTag);
        var encodedBlocks = GetFirstByteArray(blockContainer, version == 2
            ? ["BlockData", "Data"]
            : ["Data", "BlockData"]);
        if (encodedBlocks is null)
        {
            throw new SchematicReadException("У схемі не знайдено масив BlockData.");
        }

        var blocks = DecodeVarInts(encodedBlocks, checked((int)volumeLong), palette.Length);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var paletteIndex in blocks)
        {
            var blockState = palette[paletteIndex];
            if (!Schematic.IsAir(blockState))
            {
                counts.TryGetValue(blockState, out var current);
                counts[blockState] = current + 1;
            }
        }

        var metadata = TryGetCompound(schematic, "Metadata");
        var name = metadata is not null && metadata.TryGetValue("Name", out var nameTag) ? nameTag as string : null;
        var offset = ReadOffset(schematic);

        return new Schematic
        {
            Width = width,
            Height = height,
            Length = length,
            FormatVersion = version,
            DataVersion = schematic.TryGetValue("DataVersion", out var dataVersionTag) ? dataVersionTag as int? : null,
            OffsetX = offset.X,
            OffsetY = offset.Y,
            OffsetZ = offset.Z,
            Name = name,
            Palette = palette,
            Blocks = blocks,
            BlockCounts = counts
        };
    }

    private static (int X, int Y, int Z) ReadOffset(Dictionary<string, object?> schematic)
    {
        if (!schematic.TryGetValue("Offset", out var value))
        {
            return (0, 0, 0);
        }

        if (value is not int[] offset || offset.Length != 3)
        {
            throw new SchematicReadException("Поле Offset має містити рівно три цілих числа.");
        }

        return (offset[0], offset[1], offset[2]);
    }

    private static string[] ReadPalette(Dictionary<string, object?> paletteTag)
    {
        if (paletteTag.Count == 0 || paletteTag.Count > MaxPaletteEntries)
        {
            throw new SchematicReadException($"Порожня палітра або палітра більша за ліміт {MaxPaletteEntries:N0} записів.");
        }

        var maxIndex = -1;
        foreach (var value in paletteTag.Values)
        {
            if (value is not int index || index < 0 || index >= MaxPaletteEntries)
            {
                throw new SchematicReadException("Палітра містить некоректний індекс блока.");
            }

            maxIndex = Math.Max(maxIndex, index);
        }

        var result = new string[maxIndex + 1];
        foreach (var (state, value) in paletteTag)
        {
            result[(int)value!] = state;
        }

        for (var index = 0; index < result.Length; index++)
        {
            result[index] ??= $"shembuild:missing_palette_entry_{index}";
        }

        return result;
    }

    private static int[] DecodeVarInts(byte[] bytes, int expectedCount, int paletteCount)
    {
        var result = new int[expectedCount];
        var offset = 0;
        for (var block = 0; block < expectedCount; block++)
        {
            uint value = 0;
            var shift = 0;
            var complete = false;
            for (var byteIndex = 0; byteIndex < 5; byteIndex++)
            {
                if (offset >= bytes.Length)
                {
                    throw new SchematicReadException("Масив даних блоків коротший за заявлені розміри схеми.");
                }

                var next = bytes[offset++];
                value |= (uint)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    complete = true;
                    break;
                }

                shift += 7;
            }

            if (!complete || value >= paletteCount)
            {
                throw new SchematicReadException("Масив даних блоків містить некоректний індекс палітри.");
            }

            result[block] = (int)value;
        }

        if (offset != bytes.Length)
        {
            throw new SchematicReadException("Масив даних блоків довший за заявлені розміри схеми.");
        }

        return result;
    }

    private static int GetUnsignedShort(Dictionary<string, object?> compound, string name)
    {
        if (compound.TryGetValue(name, out var value) && value is short raw)
        {
            return unchecked((ushort)raw);
        }

        throw new SchematicReadException($"У схемі відсутній або некоректний розмір {name}.");
    }

    private static int GetInt(Dictionary<string, object?> compound, string name, string error)
    {
        if (compound.TryGetValue(name, out var value) && value is int result)
        {
            return result;
        }

        throw new SchematicReadException(error);
    }

    private static Dictionary<string, object?> GetCompound(Dictionary<string, object?> parent, string name, string error) =>
        TryGetCompound(parent, name) ?? throw new SchematicReadException(error);

    private static Dictionary<string, object?>? TryGetCompound(Dictionary<string, object?> parent, string name) =>
        parent.TryGetValue(name, out var value) ? value as Dictionary<string, object?> : null;

    private static Dictionary<string, object?>? GetFirstCompound(Dictionary<string, object?> parent, string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetCompound(parent, name) is { } compound)
            {
                return compound;
            }
        }

        return null;
    }

    private static byte[]? GetFirstByteArray(Dictionary<string, object?> parent, string[] names)
    {
        foreach (var name in names)
        {
            if (parent.TryGetValue(name, out var value) && value is byte[] bytes)
            {
                return bytes;
            }
        }

        return null;
    }

    private sealed class NbtReader(BinaryReader reader, int maxItems)
    {
        private int _itemsRead;

        public (string RootName, Dictionary<string, object?> Value) ReadRoot()
        {
            if (reader.ReadByte() != 10)
            {
                throw new SchematicReadException("Файл не починається з кореневого NBT-компаунду.");
            }

            var rootName = ReadString();
            var root = (Dictionary<string, object?>)ReadPayload(10, 0)!;
            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new SchematicReadException("Після кореневого NBT-компаунду знайдено зайві дані.");
            }

            return (rootName, root);
        }

        private object? ReadPayload(byte type, int depth)
        {
            if (depth > 64)
            {
                throw new SchematicReadException("Структура NBT надто глибока для прототипу.");
            }

            AddItems(1);
            return type switch
            {
                1 => reader.ReadByte(),
                2 => ReadInt16(),
                3 => ReadInt32(),
                4 => ReadInt64(),
                5 => BitConverter.Int32BitsToSingle(ReadInt32()),
                6 => BitConverter.Int64BitsToDouble(ReadInt64()),
                7 => ReadByteArray(),
                8 => ReadString(),
                9 => ReadList(depth),
                10 => ReadCompound(depth),
                11 => ReadIntArray(),
                12 => ReadLongArray(),
                _ => throw new SchematicReadException($"Файл містить невідомий тип NBT-тега {type}.")
            };
        }

        private Dictionary<string, object?> ReadCompound(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                var type = reader.ReadByte();
                if (type == 0)
                {
                    return result;
                }

                if (type > 12)
                {
                    throw new SchematicReadException($"Файл містить невідомий тип NBT-тега {type}.");
                }

                var name = ReadString();
                if (!result.TryAdd(name, ReadPayload(type, depth + 1)))
                {
                    throw new SchematicReadException("NBT-компаунд містить повторені назви полів.");
                }
            }
        }

        private List<object?> ReadList(int depth)
        {
            var elementType = reader.ReadByte();
            var count = ReadCount();
            if (elementType == 0 && count != 0)
            {
                throw new SchematicReadException("Список NBT містить некоректний тип елемента.");
            }

            var result = new List<object?>(count);
            for (var index = 0; index < count; index++)
            {
                result.Add(ReadPayload(elementType, depth + 1));
            }

            return result;
        }

        private byte[] ReadByteArray()
        {
            // The array length is already bounded by the decompressed NBT limit.
            // Do not count raw bytes as separate NBT objects in the item budget.
            var count = ReadLength();
            EnsureAvailable(count);
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new EndOfStreamException();
            }

            return bytes;
        }

        private int[] ReadIntArray()
        {
            var count = ReadCount();
            EnsureAvailable((long)count * sizeof(int));
            var result = new int[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = ReadInt32();
            }

            return result;
        }

        private long[] ReadLongArray()
        {
            var count = ReadCount();
            EnsureAvailable((long)count * sizeof(long));
            var result = new long[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = ReadInt64();
            }

            return result;
        }

        private int ReadCount()
        {
            var count = ReadLength();
            AddItems(count);
            return count;
        }

        private int ReadLength()
        {
            var count = ReadInt32();
            if (count < 0)
            {
                throw new SchematicReadException("Файл містить від’ємну довжину NBT-списку або масиву.");
            }

            return count;
        }

        private string ReadString()
        {
            Span<byte> lengthBytes = stackalloc byte[sizeof(ushort)];
            reader.ReadExactly(lengthBytes);
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            EnsureAvailable(length);
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private short ReadInt16()
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            reader.ReadExactly(bytes);
            return BinaryPrimitives.ReadInt16BigEndian(bytes);
        }

        private int ReadInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            reader.ReadExactly(bytes);
            return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }

        private long ReadInt64()
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            reader.ReadExactly(bytes);
            return BinaryPrimitives.ReadInt64BigEndian(bytes);
        }

        private void AddItems(int count)
        {
            if (count < 0 || _itemsRead > maxItems - count)
            {
                throw new SchematicReadException($"Структура NBT перевищує прототипний ліміт {maxItems:N0} елементів.");
            }

            _itemsRead += count;
        }

        private void EnsureAvailable(long count)
        {
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new EndOfStreamException();
            }
        }
    }
}
