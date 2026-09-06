using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WorkLens.Services;

internal enum VisualStudioMessagePackValueKind
{
    Null,
    Boolean,
    Integer,
    Float,
    String,
    Binary,
    Array,
    Map,
    Timestamp,
    Extension
}

internal sealed class VisualStudioMessagePackValue
{
    public VisualStudioMessagePackValueKind Kind { get; init; }
    public bool Boolean { get; init; }
    public long Integer { get; init; }
    public ulong UnsignedInteger { get; init; }
    public bool IsUnsigned { get; init; }
    public double Float { get; init; }
    public string? StringValue { get; init; }
    public int BinaryLength { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public sbyte ExtensionType { get; init; }
    public List<VisualStudioMessagePackValue>? Array { get; init; }
    public Dictionary<string, VisualStudioMessagePackValue>? Map { get; init; }

    public bool IsArray => Kind == VisualStudioMessagePackValueKind.Array;
    public bool IsMap => Kind == VisualStudioMessagePackValueKind.Map;

    public bool TryGetMapValue(string key, out VisualStudioMessagePackValue value)
    {
        if (Map is not null && Map.TryGetValue(key, out value!))
        {
            return true;
        }

        value = null!;
        return false;
    }
}

internal sealed class VisualStudioMessagePackFormatException(string message) : Exception(message);

internal sealed class VisualStudioMessagePackReader
{
    private readonly byte[] data;

    public VisualStudioMessagePackReader(byte[] data)
    {
        this.data = data;
    }

    public int Offset { get; private set; }
    public bool End => Offset >= data.Length;

    public VisualStudioMessagePackValue ReadValue(int depth = 0)
    {
        if (depth > 128)
        {
            throw new VisualStudioMessagePackFormatException("巢狀深度超過限制。");
        }

        var code = ReadByte();
        if (code <= 0x7f)
        {
            return Integer(code);
        }

        if (code >= 0xe0)
        {
            return Integer(unchecked((sbyte)code));
        }

        if (code is >= 0xa0 and <= 0xbf)
        {
            return String(ReadString(code & 0x1f));
        }

        if (code is >= 0x90 and <= 0x9f)
        {
            return ReadArray(code & 0x0f, depth);
        }

        if (code is >= 0x80 and <= 0x8f)
        {
            return ReadMap(code & 0x0f, depth);
        }

        return code switch
        {
            0xc0 => new VisualStudioMessagePackValue { Kind = VisualStudioMessagePackValueKind.Null },
            0xc2 => new VisualStudioMessagePackValue { Kind = VisualStudioMessagePackValueKind.Boolean, Boolean = false },
            0xc3 => new VisualStudioMessagePackValue { Kind = VisualStudioMessagePackValueKind.Boolean, Boolean = true },
            0xc4 => ReadBinary(ReadLength(ReadByte())),
            0xc5 => ReadBinary(ReadLength(ReadUInt16())),
            0xc6 => ReadBinary(ReadLength(ReadUInt32())),
            0xc7 => ReadExtension(ReadLength(ReadByte())),
            0xc8 => ReadExtension(ReadLength(ReadUInt16())),
            0xc9 => ReadExtension(ReadLength(ReadUInt32())),
            0xca => new VisualStudioMessagePackValue
            {
                Kind = VisualStudioMessagePackValueKind.Float,
                Float = ReadSingle()
            },
            0xcb => new VisualStudioMessagePackValue
            {
                Kind = VisualStudioMessagePackValueKind.Float,
                Float = ReadDouble()
            },
            0xcc => Unsigned(ReadByte()),
            0xcd => Unsigned(ReadUInt16()),
            0xce => Unsigned(ReadUInt32()),
            0xcf => Unsigned(ReadUInt64()),
            0xd0 => Integer(unchecked((sbyte)ReadByte())),
            0xd1 => Integer(ReadInt16()),
            0xd2 => Integer(ReadInt32()),
            0xd3 => Integer(ReadInt64()),
            0xd4 => ReadExtension(1),
            0xd5 => ReadExtension(2),
            0xd6 => ReadExtension(4),
            0xd7 => ReadExtension(8),
            0xd8 => ReadExtension(16),
            0xd9 => String(ReadString(ReadLength(ReadByte()))),
            0xda => String(ReadString(ReadLength(ReadUInt16()))),
            0xdb => String(ReadString(ReadLength(ReadUInt32()))),
            0xdc => ReadArray(ReadLength(ReadUInt16()), depth),
            0xdd => ReadArray(ReadLength(ReadUInt32()), depth),
            0xde => ReadMap(ReadLength(ReadUInt16()), depth),
            0xdf => ReadMap(ReadLength(ReadUInt32()), depth),
            _ => throw new VisualStudioMessagePackFormatException($"不支援的 MessagePack code 0x{code:X2}。")
        };
    }

    private VisualStudioMessagePackValue ReadArray(int count, int depth)
    {
        EnsureCollectionFits(count);
        var values = new List<VisualStudioMessagePackValue>(count);
        for (var index = 0; index < count; index++)
        {
            values.Add(ReadValue(depth + 1));
        }

        return new VisualStudioMessagePackValue
        {
            Kind = VisualStudioMessagePackValueKind.Array,
            Array = values
        };
    }

    private VisualStudioMessagePackValue ReadMap(int count, int depth)
    {
        EnsureCollectionFits(count * 2);
        var values = new Dictionary<string, VisualStudioMessagePackValue>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = ReadValue(depth + 1);
            values[KeyText(key)] = ReadValue(depth + 1);
        }

        return new VisualStudioMessagePackValue
        {
            Kind = VisualStudioMessagePackValueKind.Map,
            Map = values
        };
    }

    private VisualStudioMessagePackValue ReadExtension(int length)
    {
        var extensionType = unchecked((sbyte)ReadByte());
        var payload = ReadBytes(length);
        if (extensionType == -1 && TryReadTimestamp(payload, out var timestamp))
        {
            return new VisualStudioMessagePackValue
            {
                Kind = VisualStudioMessagePackValueKind.Timestamp,
                Timestamp = timestamp
            };
        }

        return new VisualStudioMessagePackValue
        {
            Kind = VisualStudioMessagePackValueKind.Extension,
            ExtensionType = extensionType,
            BinaryLength = length
        };
    }

    private static bool TryReadTimestamp(ReadOnlySpan<byte> payload, out DateTimeOffset timestamp)
    {
        timestamp = default;
        long seconds;
        long nanoseconds;
        if (payload.Length == 4)
        {
            seconds = BinaryPrimitives.ReadUInt32BigEndian(payload);
            nanoseconds = 0;
        }
        else if (payload.Length == 8)
        {
            var packed = BinaryPrimitives.ReadUInt64BigEndian(payload);
            nanoseconds = (long)(packed >> 34);
            seconds = (long)(packed & ((1UL << 34) - 1));
        }
        else if (payload.Length == 12)
        {
            nanoseconds = BinaryPrimitives.ReadUInt32BigEndian(payload);
            seconds = BinaryPrimitives.ReadInt64BigEndian(payload[4..]);
        }
        else
        {
            return false;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds)
                .AddTicks(nanoseconds / 100);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private VisualStudioMessagePackValue ReadBinary(int length)
    {
        ReadBytes(length);
        return new VisualStudioMessagePackValue
        {
            Kind = VisualStudioMessagePackValueKind.Binary,
            BinaryLength = length
        };
    }

    private byte ReadByte()
    {
        if (Offset >= data.Length)
        {
            throw new VisualStudioMessagePackFormatException("資料在值結尾前截斷。");
        }

        return data[Offset++];
    }

    private ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || count > data.Length - Offset)
        {
            throw new VisualStudioMessagePackFormatException("資料長度超出檔案範圍。");
        }

        var result = data.AsSpan(Offset, count);
        Offset += count;
        return result;
    }

    private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));
    private uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
    private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));
    private short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(ReadBytes(2));
    private int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
    private long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));
    private float ReadSingle() => BinaryPrimitives.ReadSingleBigEndian(ReadBytes(4));
    private double ReadDouble() => BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(8));

    private string ReadString(int length) => Encoding.UTF8.GetString(ReadBytes(length));
    private static int ReadLength(byte value) => value;
    private static int ReadLength(ushort value) => value;

    private static int ReadLength(uint value)
    {
        if (value > int.MaxValue)
        {
            throw new VisualStudioMessagePackFormatException("集合長度超過限制。");
        }

        return (int)value;
    }

    private void EnsureCollectionFits(int minimumBytes)
    {
        if (minimumBytes < 0 || minimumBytes > data.Length - Offset)
        {
            throw new VisualStudioMessagePackFormatException("集合長度超出檔案範圍。");
        }
    }

    private static VisualStudioMessagePackValue Integer(long value) => new()
    {
        Kind = VisualStudioMessagePackValueKind.Integer,
        Integer = value
    };

    private static VisualStudioMessagePackValue Unsigned(ulong value) => new()
    {
        Kind = VisualStudioMessagePackValueKind.Integer,
        UnsignedInteger = value,
        IsUnsigned = true
    };

    private static VisualStudioMessagePackValue String(string value) => new()
    {
        Kind = VisualStudioMessagePackValueKind.String,
        StringValue = value
    };

    private static string KeyText(VisualStudioMessagePackValue key)
    {
        if (key.Kind == VisualStudioMessagePackValueKind.String)
        {
            return key.StringValue ?? string.Empty;
        }

        if (key.Kind == VisualStudioMessagePackValueKind.Integer)
        {
            return key.IsUnsigned
                ? key.UnsignedInteger.ToString(CultureInfo.InvariantCulture)
                : key.Integer.ToString(CultureInfo.InvariantCulture);
        }

        return $"<{key.Kind}>";
    }
}
