using System.Buffers.Binary;
using AnimeStudio;

static class Program
{
    private static int Main()
    {
        TestValidDxbcStage();
        TestDxbcMalformedHeadersFailClosed();
        TestD3D11SnippetBoundsFailClosed();
        TestSpirVSnippetBoundsFailClosed();
        Console.WriteLine("Shader boundary synthetic tests passed.");
        return 0;
    }

    private static void TestValidDxbcStage()
    {
        var valid = BuildDxbc(64, 36, 20, 0x00010000u);
        AssertEqual("vertex", ShaderSubProgram.TryGetDxbcProgramStage(valid), "valid DXBC stage");
    }

    private static void TestDxbcMalformedHeadersFailClosed()
    {
        AssertEqual(string.Empty, ShaderSubProgram.TryGetDxbcProgramStage(new byte[31]), "truncated DXBC header");

        var badTotalLength = BuildDxbc(64, 36, 20, 0x00010000u);
        BinaryPrimitives.WriteUInt32LittleEndian(badTotalLength.AsSpan(24, 4), 63);
        AssertEqual(string.Empty, ShaderSubProgram.TryGetDxbcProgramStage(badTotalLength), "bad DXBC total length");

        var badChunkOffset = BuildDxbc(64, 36, 20, 0x00010000u);
        BinaryPrimitives.WriteUInt32LittleEndian(badChunkOffset.AsSpan(32, 4), 28);
        AssertEqual(string.Empty, ShaderSubProgram.TryGetDxbcProgramStage(badChunkOffset), "DXBC chunk offset below header");

        var truncatedChunk = BuildDxbc(64, 36, 20, 0x00010000u);
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedChunk.AsSpan(40, 4), 1024);
        AssertEqual(string.Empty, ShaderSubProgram.TryGetDxbcProgramStage(truncatedChunk), "truncated DXBC chunk");
    }

    private static void TestD3D11SnippetBoundsFailClosed()
    {
        var overflow = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(overflow.AsSpan(4, 4), int.MaxValue - 8);
        BinaryPrimitives.WriteInt32LittleEndian(overflow.AsSpan(8, 4), 32);
        AssertEqual(0, ShaderSubProgram.EnumerateEndfieldD3D11Snippets(overflow).Count, "D3D11 offset/size overflow");

        var uintOffsetOverflow = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(uintOffsetOverflow.AsSpan(4, 4), uint.MaxValue - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(uintOffsetOverflow.AsSpan(8, 4), 32);
        AssertEqual(0, ShaderSubProgram.EnumerateEndfieldD3D11Snippets(uintOffsetOverflow).Count, "D3D11 uint32 offset overflow");

        var valid = new byte[96];
        BinaryPrimitives.WriteInt32LittleEndian(valid.AsSpan(4, 4), 32);
        BinaryPrimitives.WriteInt32LittleEndian(valid.AsSpan(8, 4), 64);
        BuildDxbc(64, 36, 20, 0x00010000u).CopyTo(valid, 32);
        var snippets = ShaderSubProgram.EnumerateEndfieldD3D11Snippets(valid);
        AssertEqual(1, snippets.Count, "valid D3D11 wrapper snippet count");
        AssertEqual((32, 64), snippets[0], "valid D3D11 wrapper snippet");
    }

    private static void TestSpirVSnippetBoundsFailClosed()
    {
        var overflow = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(overflow.AsSpan(4, 4), int.MaxValue - 8);
        BinaryPrimitives.WriteInt32LittleEndian(overflow.AsSpan(8, 4), 32);
        AssertEqual(0, ShaderSubProgram.EnumerateEndfieldSpirVSnippets(overflow).Count, "SPIR-V offset/size overflow");

        var uintOffsetOverflow = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(uintOffsetOverflow.AsSpan(4, 4), uint.MaxValue - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(uintOffsetOverflow.AsSpan(8, 4), 32);
        AssertEqual(0, ShaderSubProgram.EnumerateEndfieldSpirVSnippets(uintOffsetOverflow).Count, "SPIR-V uint32 offset overflow");

        var valid = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(valid.AsSpan(4, 4), 24);
        BinaryPrimitives.WriteInt32LittleEndian(valid.AsSpan(8, 4), 16);
        valid[24] = (byte)'L';
        valid[25] = (byte)'O';
        valid[26] = (byte)'M';
        valid[27] = (byte)'S';
        var snippets = ShaderSubProgram.EnumerateEndfieldSpirVSnippets(valid);
        AssertEqual(1, snippets.Count, "valid SPIR-V wrapper snippet count");
        AssertEqual((24, 16), snippets[0], "valid SPIR-V wrapper snippet");
    }

    private static byte[] BuildDxbc(int totalLength, int chunkOffset, int chunkSize, uint versionToken)
    {
        var data = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x43425844u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32, 4), (uint)chunkOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(chunkOffset, 4), 0x52444853u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(chunkOffset + 4, 4), (uint)chunkSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(chunkOffset + 8, 4), versionToken);
        return data;
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
        }
    }
}
