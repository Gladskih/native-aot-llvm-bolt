using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class Program
{
    private static readonly byte[][] Records =
    [
        """{"kind":"reading","value":23,"device":377}"""u8.ToArray(),
        """{"device":239,"value":71,"kind":"reading"}"""u8.ToArray(),
        """{"kind":"reading","device":101,"value":-13,"extra":{"region":"north"}}"""u8.ToArray(),
        """{"value":18,"kind":"reading","device":455}"""u8.ToArray(),
        """{"kind":"heartbeat","device":377,"value":3}"""u8.ToArray(),
        """{"kind":"reading","device":239,"value":58}"""u8.ToArray(),
        """{"kind":"reading","device":455,"value":214,"extra":[1,2,3]}"""u8.ToArray(),
        """{"device":101,"kind":"alarm","value":7}"""u8.ToArray(),
        """{"kind":"reading","value":81,"device":239}"""u8.ToArray(),
        """{"kind":"reading","device":377,"value":99}"""u8.ToArray(),
        """{"kind":"reading","device":101,"value":12}"""u8.ToArray(),
        """{"value":-9,"device":455,"kind":"reading"}"""u8.ToArray(),
        """{"kind":"reading","device":377,"value":42}"""u8.ToArray(),
        """{"kind":"heartbeat","device":239,"value":4}"""u8.ToArray(),
        """{"kind":"reading","device":101,"value":117}"""u8.ToArray(),
        """{"kind":"reading","device":455,"value":136}"""u8.ToArray()
    ];

    private static int Main(string[] args)
    {
        if (args is ["--self-test"])
        {
            SelfTest();
            Console.WriteLine("self-test: OK");
            return 0;
        }

        string? mode = args is ["--gc" or "--brotli", _, _] ? args[0] : null;
        int offset = mode is null ? 0 : 1;
        if (args.Length != offset + 2 ||
            !int.TryParse(args[offset], CultureInfo.InvariantCulture, out int count) || count < 1 ||
            !uint.TryParse(args[offset + 1], CultureInfo.InvariantCulture, out uint seed) || seed == 0)
        {
            Console.Error.WriteLine("Usage: Telemetry [--gc|--brotli] <positive count> <nonzero uint seed> | --self-test");
            return 2;
        }

        ulong checksum = mode switch
        {
            "--gc" => Collect(count, seed),
            "--brotli" => Compress(count, seed),
            _ => ParseRecords(count, seed)
        };
        Console.WriteLine(checksum.ToString("x16", CultureInfo.InvariantCulture));
        return 0;
    }

    private static ulong ParseRecords(int count, uint seed)
    {
        ulong checksum = 0;
        for (int index = 0; index < count; index++)
        {
            seed = Next(seed);
            int sample = (int)(seed & (uint)(Records.Length - 1));
            checksum = unchecked(checksum * 1099511628211UL + (uint)Parse(Records[sample]));
        }

        return checksum;
    }

    private static ulong Collect(int rounds, uint seed)
    {
        const int size = 8192;
        var roots = new Node?[size];
        for (int round = 0; round < rounds; round++)
        {
            for (int index = 0; index < size; index++)
            {
                seed = Next(seed);
                int slot = (int)(seed & (size - 1));
                // Keep one older leaf alive without building unbounded chains.
                var leaf = roots[(slot + 17) & (size - 1)]?.Leaf ?? new Node(seed, null);
                roots[slot] = new Node(seed, leaf);
            }

            if (round % 8 == 7)
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        }

        ulong checksum = 0;
        foreach (Node? node in roots)
        {
            if (node is not null)
                checksum = unchecked(checksum * 1099511628211UL + node.Value + node.Leaf!.Value);
        }

        return checksum;
    }

    private sealed record Node(uint Value, Node? Leaf);

    private static ulong Compress(int count, uint seed)
    {
        const int payloadCount = 8;
        var payloads = new byte[payloadCount][];
        for (int index = 0; index < payloads.Length; index++)
        {
            using var stream = new MemoryStream();
            for (int record = 0; record < 4096; record++)
            {
                seed = Next(seed);
                stream.Write(Records[seed & (uint)(Records.Length - 1)]);
                stream.WriteByte((byte)'\n');
            }

            payloads[index] = stream.ToArray();
        }

        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(payloads.Max(data => data.Length))];
        var restored = new byte[payloads.Max(data => data.Length)];
        ulong checksum = 0;
        for (int index = 0; index < count; index++)
        {
            var payload = payloads[index % payloadCount];
            Check(BrotliEncoder.TryCompress(payload, compressed, out int written, quality: 5, window: 20));
            if (index < payloadCount)
            {
                Check(BrotliDecoder.TryDecompress(compressed.AsSpan(0, written), restored, out int restoredBytes));
                Check(payload.AsSpan().SequenceEqual(restored.AsSpan(0, restoredBytes)));
            }

            checksum = unchecked(checksum * 1099511628211UL + (uint)written + compressed[written / 2]);
        }

        return checksum;
    }

    private static uint Next(uint seed)
    {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        return seed;
    }

    private static int Parse(ReadOnlySpan<byte> record)
    {
        var reader = new Utf8JsonReader(record);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a telemetry object.");

        int device = 0;
        int value = 0;
        int kind = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("device"u8))
            {
                reader.Read();
                device = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("value"u8))
            {
                reader.Read();
                value = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("kind"u8))
            {
                reader.Read();
                kind = reader.ValueTextEquals("reading"u8) ? 1 :
                    reader.ValueTextEquals("heartbeat"u8) ? 2 :
                    reader.ValueTextEquals("alarm"u8) ? 3 : 0;
            }
            else
            {
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            throw new JsonException("Expected the end of the telemetry object.");

        return kind switch
        {
            1 => device * 31 + value,
            2 => device * 17 - value,
            3 => device * 53 + value * 7,
            _ => throw new JsonException("Unknown telemetry kind.")
        };
    }

    private static void SelfTest()
    {
        Check(Parse(Records[0]) == 11710);
        Check(Parse(Records[2]) == 3118);
        Check(Parse(Records[4]) == 6406);
        Check(Parse(Records[6]) == 14319);
        Check(Parse(Records[7]) == 5402);

        bool caught = false;
        bool unwound = false;
        try
        {
            ParseBrokenRecord(ref unwound);
        }
        catch (JsonException)
        {
            caught = true;
        }

        Check(caught && unwound);
        CheckReferenceAcrossCollection();
        Check(Compress(8, 77) != 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ParseBrokenRecord(ref bool unwound)
    {
        try
        {
            Parse("""{"kind":"reading","value":}"""u8);
        }
        finally
        {
            unwound = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckReferenceAcrossCollection()
    {
        var survivor = new byte[1024];
        survivor[999] = 42;
        for (int index = 0; index < 4096; index++)
            GC.KeepAlive(new byte[1024]);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Check(survivor[999] == 42);
    }

    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Self-test assertion failed.");
    }
}
