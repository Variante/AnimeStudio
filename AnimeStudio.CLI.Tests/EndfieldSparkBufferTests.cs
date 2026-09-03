using System;
using System.IO;
using System.Text;
using AnimeStudio.Endfield;

internal static class EndfieldSparkBufferTests
{
    public static void Run()
    {
        TestMinimalRootMap();
        TestTrailingBytesRejected();
        TestInvalidPointerRejected();
        TestNegativeAndOverflowCountsRejected();
        TestUnsupportedRootAndArrayKindsRejected();
        TestUnterminatedStringRejected();
        TestInvalidUtf8Rejected();
    }

    private static void TestMinimalRootMap()
    {
        var result = EndfieldSparkBuffer.ParseBytes(BuildRootMapFixture());
        if (result.Name != "Root" || result.Data["a"]?["x"]?.ToObject<int>() != 42)
        {
            throw new InvalidOperationException("SparkBuffer minimal root map fixture decoded incorrectly");
        }
    }

    private static void TestTrailingBytesRejected()
    {
        var payload = BuildRootMapFixture();
        Array.Resize(ref payload, payload.Length + 1);
        payload[^1] = 0xA5;
        AssertThrows("trailing bytes", () => EndfieldSparkBuffer.ParseBytes(payload));
    }

    private static void TestInvalidPointerRejected()
    {
        var payload = BuildRootMapFixture();
        // The root-map bean pointer is dataOffset + 16. Point it beyond EOF.
        var dataOffset = BitConverter.ToInt32(payload, 8);
        BitConverter.GetBytes(payload.Length + 100).CopyTo(payload, dataOffset + 16);
        AssertThrows("bean pointer", () => EndfieldSparkBuffer.ParseBytes(payload));
    }

    private static void TestNegativeAndOverflowCountsRejected()
    {
        var payload = BuildRootMapFixture();
        var dataOffset = BitConverter.ToInt32(payload, 8);
        BitConverter.GetBytes(-1).CopyTo(payload, dataOffset);
        AssertThrows("root map item count", () => EndfieldSparkBuffer.ParseBytes(payload));

        payload = BuildRootMapFixture();
        BitConverter.GetBytes(int.MaxValue).CopyTo(payload, BitConverter.ToInt32(payload, 0));
        AssertThrows("type definition count", () => EndfieldSparkBuffer.ParseBytes(payload));
    }

    private static void TestUnsupportedRootAndArrayKindsRejected()
    {
        var payload = BuildRootMapFixture();
        var rootOffset = BitConverter.ToInt32(payload, 4);
        payload[rootOffset] = 1; // Byte root kind.
        AssertThrows("unsupported root type", () => EndfieldSparkBuffer.ParseBytes(payload));

        payload = BuildRootMapFixture(includeArrayByteField: true);
        AssertThrows("unsupported field type", () => EndfieldSparkBuffer.ParseBytes(payload));
    }

    private static void TestUnterminatedStringRejected()
    {
        var payload = BuildRootMapFixture();
        var rootOffset = BitConverter.ToInt32(payload, 4);
        // Remove the root name terminator by replacing it with a nonzero byte.
        for (var i = rootOffset + 5; i < payload.Length; i++)
        {
            payload[i] = 0x7F;
        }
        AssertThrows("truncated null-terminated string", () => EndfieldSparkBuffer.ParseBytes(payload));
    }

    private static void TestInvalidUtf8Rejected()
    {
        var payload = BuildRootMapFixture();
        var rootOffset = BitConverter.ToInt32(payload, 4);
        payload[rootOffset + 1] = 0xFF;
        AssertThrows("invalid UTF-8", () => EndfieldSparkBuffer.ParseBytes(payload));
    }

    private static void AssertThrows(string expected, Action action)
    {
        try
        {
            action();
        }
        catch (EndfieldSparkBufferException exception)
        {
            if (!exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SparkBuffer negative fixture expected '{expected}', got '{exception.Message}'");
            }
            return;
        }
        throw new InvalidOperationException($"SparkBuffer negative fixture did not fail: {expected}");
    }

    private static byte[] BuildRootMapFixture(bool includeArrayByteField = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        var typeOffset = checked((int)stream.Position);
        writer.Write(1); // one bean definition
        writer.Write((byte)8); // Bean
        Align4(writer);
        writer.Write(123);
        WriteCString(writer, "B");
        Align4(writer);
        writer.Write(includeArrayByteField ? 2 : 1);
        WriteCString(writer, "x");
        writer.Write((byte)2); // Int
        if (includeArrayByteField)
        {
            WriteCString(writer, "bad");
            writer.Write((byte)9); // Array
            writer.Write((byte)1); // Byte item kind, rejected by the reader
        }

        var rootOffset = checked((int)stream.Position);
        writer.Write((byte)10); // Map
        WriteCString(writer, "Root");
        writer.Write((byte)7); // String key
        writer.Write((byte)8); // Bean value
        Align4(writer);
        writer.Write(123);

        var dataOffset = checked((int)stream.Position);
        writer.Write(1); // one map item
        writer.Write(0L); // reserved pointer table prefix
        var keyOffsetPosition = checked((int)stream.Position);
        writer.Write(0); // key string offset
        var beanOffsetPosition = checked((int)stream.Position);
        writer.Write(0); // bean pointer
        var keyOffset = checked((int)stream.Position);
        WriteCString(writer, "a");
        var beanOffset = checked((int)stream.Position);
        writer.Write(42);
        var arrayOffsetPosition = -1;
        if (includeArrayByteField)
        {
            arrayOffsetPosition = checked((int)stream.Position);
            writer.Write(0); // array data pointer
            writer.Write(1); // one Byte item
            writer.Write((byte)1);
        }

        var payload = stream.ToArray();
        BitConverter.GetBytes(typeOffset).CopyTo(payload, 0);
        BitConverter.GetBytes(rootOffset).CopyTo(payload, 4);
        BitConverter.GetBytes(dataOffset).CopyTo(payload, 8);
        BitConverter.GetBytes(keyOffset).CopyTo(payload, keyOffsetPosition);
        BitConverter.GetBytes(beanOffset).CopyTo(payload, beanOffsetPosition);
        if (arrayOffsetPosition >= 0)
        {
            var arrayOffset = arrayOffsetPosition + sizeof(int);
            BitConverter.GetBytes(arrayOffset).CopyTo(payload, arrayOffsetPosition);
        }
        return payload;
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void Align4(BinaryWriter writer)
    {
        while ((writer.BaseStream.Position & 3) != 0)
        {
            writer.Write((byte)0);
        }
    }
}
