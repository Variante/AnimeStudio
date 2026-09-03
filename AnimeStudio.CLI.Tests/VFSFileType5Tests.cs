using System.Reflection;
using AnimeStudio;

/// <summary>
/// Focused fixtures for the Endfield VFS inner type-5 block gate.
///
/// The shared CLI test entry point is intentionally not edited by this file;
/// call <see cref="Run"/> from that entry point when running the focused suite.
/// </summary>
internal static class VFSFileType5Tests
{
    private static readonly MethodInfo ReadBlocksMethod = typeof(VFSFile).GetMethod(
        "ReadBlocks",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VFSFile.ReadBlocks was not found");
    private static readonly MethodInfo ValidateCompressedBlockConsumptionMethod = typeof(VFSFile).GetMethod(
        "ValidateCompressedBlockConsumption",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VFSFile.ValidateCompressedBlockConsumption was not found");

    public static void Run()
    {
        TestSuccessfulType5BlockPublishesExactlyDecodedBytes();
        TestType5DecoderExceptionIsTerminalAndDoesNotPublishPooledBytes();
        TestType5LengthMismatchIsTerminalAndDoesNotPublishPooledBytes();
        TestType5TruncatedInputIsTerminalAndDoesNotPublishPooledBytes();
        TestStoredBlockConsumptionUsesStoredBytes();
    }

    private static void TestSuccessfulType5BlockPublishesExactlyDecodedBytes()
    {
        using var output = new MemoryStream();
        InvokeReadBlocks(new byte[] { 0x03, (byte)'A', (byte)'B', (byte)'C' }, 4, 3, output);
        AssertBytes(new byte[] { (byte)'A', (byte)'B', (byte)'C' }, output.ToArray(), "successful type-5 block");
    }

    private static void TestType5DecoderExceptionIsTerminalAndDoesNotPublishPooledBytes()
    {
        var exception = AssertReadBlocksFails(new byte[] { 0x03, (byte)'A' }, 2, 3);
        AssertContains(exception.Message, "type-5 decode failed", "type-5 decoder exception status");
        AssertContains(exception.Message, "expected=3", "type-5 decoder exception expected length");
        AssertContains(exception.Message, "actual=unknown", "type-5 decoder exception actual length");
    }

    private static void TestType5LengthMismatchIsTerminalAndDoesNotPublishPooledBytes()
    {
        var exception = AssertReadBlocksFails(new byte[] { 0x03, (byte)'A', (byte)'B', (byte)'C' }, 4, 4);
        AssertContains(exception.Message, "expected=4", "type-5 length mismatch expected length");
        AssertContains(exception.Message, "actual=3", "type-5 length mismatch actual length");
    }

    private static void TestType5TruncatedInputIsTerminalAndDoesNotPublishPooledBytes()
    {
        var exception = AssertReadBlocksFails(new byte[] { 0x03, (byte)'A', (byte)'B' }, 4, 3);
        AssertContains(exception.Message, "compressed payload truncated", "type-5 truncation status");
        AssertContains(exception.Message, "expected=4, actual=3", "type-5 truncation expected/actual input length");
    }

    private static void TestStoredBlockConsumptionUsesStoredBytes()
    {
        var vfs = (VFSFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VFSFile));
        var blocksField = typeof(VFSFile).GetField("m_BlocksInfo", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VFSFile.m_BlocksInfo was not found");
        blocksField.SetValue(vfs, new List<BundleFile.StorageBlock>
        {
            new()
            {
                flags = 0,
                compressedSize = 0,
                uncompressedSize = 3,
            },
        });
        vfs.Offset = 0;
        vfs.m_Header = new BundleFile.Header
        {
            encFlags = 0,
            flags = 0,
            compressedBlocksInfoSize = 0,
        };
        using var reader = new FileReader(
            "vfs-stored-consumption-fixture", new MemoryStream(new byte[43]), leaveOpen: false);
        reader.Position = 43;
        try
        {
            ValidateCompressedBlockConsumptionMethod.Invoke(
                vfs, new object[] { reader, "vfs-stored-consumption-fixture" });
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static void InvokeReadBlocks(byte[] payload, int compressedSize, int uncompressedSize, MemoryStream output)
    {
        var vfs = (VFSFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VFSFile));
        var blocksField = typeof(VFSFile).GetField("m_BlocksInfo", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VFSFile.m_BlocksInfo was not found");
        blocksField.SetValue(vfs, new List<BundleFile.StorageBlock>
        {
            new()
            {
                flags = (StorageBlockFlags)5,
                compressedSize = (uint)compressedSize,
                uncompressedSize = (uint)uncompressedSize,
            },
        });

        // VFS type-5 bytes are encrypted before the inverted-LZ4 stream. The
        // first-block transform is reversible for these bounded fixtures, so
        // apply the production transform once to create the on-disk bytes.
        var encryptedPayload = payload.ToArray();
        VFSUtils.DecryptBlock(encryptedPayload, GameType.ArknightsEndfield);
        using var reader = new FileReader("vfs-type5-fixture", new MemoryStream(encryptedPayload), leaveOpen: false);
        ReadBlocksMethod.Invoke(vfs, new object[] { reader, output, GameType.ArknightsEndfield, "vfs-type5-fixture" });
    }

    private static Exception AssertReadBlocksFails(byte[] payload, int compressedSize, int uncompressedSize)
    {
        using var output = new MemoryStream();
        try
        {
            InvokeReadBlocks(payload, compressedSize, uncompressedSize, output);
            throw new InvalidOperationException(
                $"type-5 fixture unexpectedly succeeded and published {output.Length} bytes");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            if (output.Length != 0)
            {
                throw new InvalidOperationException(
                    $"failed type-5 fixture published {output.Length} pooled bytes");
            }
            return exception.InnerException;
        }
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string label)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{label}: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");
        }
    }

    private static void AssertContains(string actual, string expected, string label)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}' in '{actual}'");
        }
    }
}
