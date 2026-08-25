using System.Buffers.Binary;
using AnimeStudio;
using AnimeStudio.ShaderRecovery;

static class Program
{
    private static int Main()
    {
        TestValidDxbcStage();
        TestDxbcMalformedHeadersFailClosed();
        TestD3D11SnippetBoundsFailClosed();
        TestSpirVSnippetBoundsFailClosed();
        TestEndfieldConstantBufferTable();
        TestEndfieldConstantBufferTableFailsClosed();
        TestShaderRecoveryContract();
        TestSpirvHlslEmitter();
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

    private static void TestEndfieldConstantBufferTable()
    {
        var record = BuildEndfieldParameterRecord(structCount: 0, secondFieldOffset: 16);
        AssertEqual(true, EndfieldShaderParameterRecord.TryParse(record, out var parsed), "valid Endfield parameter record");
        AssertEqual(true, parsed.ConstantBufferTableParsed, "valid Endfield constant-buffer table status");
        AssertEqual(1, parsed.ConstantBuffers.Count, "valid Endfield constant-buffer count");
        var buffer = parsed.ConstantBuffers[0];
        AssertEqual("UnityPerMaterial", buffer.Name, "valid Endfield constant-buffer name");
        AssertEqual(64, buffer.Size, "valid Endfield constant-buffer size");
        AssertEqual(2, buffer.Fields.Count, "valid Endfield constant-buffer field count");
        AssertEqual(("_Color", 0, 1, 4, 0, 0),
            (buffer.Fields[0].Name, buffer.Fields[0].Kind, buffer.Fields[0].RowCount,
                buffer.Fields[0].ColumnCount, buffer.Fields[0].ArraySize, buffer.Fields[0].ByteOffset),
            "valid Endfield vector field");
        AssertEqual(("_Matrix", 0, 4, 4, 0, 16),
            (buffer.Fields[1].Name, buffer.Fields[1].Kind, buffer.Fields[1].RowCount,
                buffer.Fields[1].ColumnCount, buffer.Fields[1].ArraySize, buffer.Fields[1].ByteOffset),
            "valid Endfield matrix field");
    }

    private static void TestEndfieldConstantBufferTableFailsClosed()
    {
        var outOfRange = BuildEndfieldParameterRecord(structCount: 0, secondFieldOffset: 64);
        AssertEqual(true, EndfieldShaderParameterRecord.TryParse(outOfRange, out var parsedOutOfRange), "descriptor tail survives invalid field offset");
        AssertEqual(false, parsedOutOfRange.ConstantBufferTableParsed, "invalid field offset fails closed");

        var unsupportedStruct = BuildEndfieldParameterRecord(structCount: 1, secondFieldOffset: 16);
        AssertEqual(true, EndfieldShaderParameterRecord.TryParse(unsupportedStruct, out var parsedStruct), "descriptor tail survives unsupported struct table");
        AssertEqual(false, parsedStruct.ConstantBufferTableParsed, "unsupported struct table fails closed");
    }

    private static void TestShaderRecoveryContract()
    {
        var input = new byte[] { 0x01, 0x02, 0x03 };
        var provenance = new ShaderRecoveryProvenance("AnimeStudio", "test", string.Empty);
        var output = ShaderRecoveryOutput.FromText(
            input,
            "line 1\r\nline 2\rline 3",
            provenance,
            new[] { new ShaderRecoveryDiagnostic("test", "synthetic") });

        AssertEqual("animestudio.shader-recovery.v1", output.Schema, "shader recovery schema");
        AssertEqual("line 1\nline 2\nline 3", output.SourceText, "shader recovery line endings");
        AssertEqual(ShaderRecoveryContract.ComputeSha256Hex(input), output.Provenance.InputSha256, "shader recovery input hash");
        AssertEqual(1, output.Diagnostics.Count, "shader recovery diagnostics");
    }

    private static void TestSpirvHlslEmitter()
    {
        var words = new uint[]
        {
            0x07230203, 0x00010000, 0, 6, 0,
            0x00020011, 1,
            0x0003000E, 0, 100,
            0x0005000F, 0, 4, 0x6E69616D, 0,
            0x00020013, 1,
            0x00040021, 2, 1, 1,
            0x00050036, 1, 4, 0, 2,
            0x000200F8, 5,
            0x000100FD,
            0x00010038,
        };
        var spirv = new byte[words.Length * sizeof(uint)];
        Buffer.BlockCopy(words, 0, spirv, 0, spirv.Length);

        AssertEqual(true,
            SpirvHlslEmitter.TryEmit(spirv, "main", 0, 50, out var hlsl, out var diagnostic),
            $"SPIRV-Cross HLSL emission: {diagnostic}");
        if (!hlsl.Contains("main", StringComparison.Ordinal))
            throw new InvalidOperationException("SPIRV-Cross HLSL emission did not contain the entry point.");
    }


    private static byte[] BuildEndfieldParameterRecord(int structCount, int secondFieldOffset)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x0C11FFE2);
        writer.Write(new byte[20]);

        WriteAlignedString(writer, "UnityPerMaterial");
        writer.Write(64);
        writer.Write(2);
        WriteConstantField(writer, "_Color", 0, 1, 4, 0, 0, 0);
        WriteConstantField(writer, "_Matrix", 0, 4, 4, 0, 0, secondFieldOffset);
        writer.Write(structCount);

        writer.Write(2);
        WriteAlignedString(writer, "_MainTex");
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(2u);
        writer.Write(3u);
        WriteAlignedString(writer, "UnityPerMaterial");
        writer.Write(1u);
        writer.Write(4u);
        writer.Write(5u);

        writer.Write(1);
        WriteAlignedString(writer, "Global");
        writer.Write(0);
        writer.Write(1);
        writer.Write(0);
        WriteAlignedString(writer, "_MainTex");
        writer.Write(0);
        writer.Write(1);
        writer.Write(0u);
        writer.Write(0u);
        return stream.ToArray();
    }

    private static void WriteConstantField(
        BinaryWriter writer,
        string name,
        int kind,
        int rows,
        int columns,
        int arraySize,
        int unknown,
        int byteOffset)
    {
        WriteAlignedString(writer, name);
        writer.Write(kind);
        writer.Write(rows);
        writer.Write(columns);
        writer.Write(arraySize);
        writer.Write(unknown);
        writer.Write(byteOffset);
    }

    private static void WriteAlignedString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        while ((writer.BaseStream.Position & 3) != 0)
        {
            writer.Write((byte)0);
        }
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
