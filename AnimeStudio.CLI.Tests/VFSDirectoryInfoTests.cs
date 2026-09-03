using System.Text;
using AnimeStudio;

internal static class VFSDirectoryInfoTests
{
    public static void Run()
    {
        TestCurrentNodeNameDecodesExactly();
        TestNodeNameRequiresNullTerminator();
    }

    private static void TestCurrentNodeNameDecodesExactly()
    {
        const string name = "CAB-7ab855cd79160017ab6767479aa5a52d";
        var encoded = Encoding.ASCII.GetBytes(name);
        for (var index = 0; index < encoded.Length; index++)
        {
            encoded[index] ^= (byte)((index ^ 0x97) & 0xFF);
        }

        using var stream = new MemoryStream(encoded.Concat(new byte[] { 0 }).ToArray());
        using var reader = new EndianBinaryReader(stream);
        var actual = VFSUtils.ReadDirectoryNodeName(reader, GameType.ArknightsEndfield, 7);
        AssertEqual(name, actual, "current VFS node name");
        AssertEqual(0L, reader.Remaining, "current VFS node name exact consumption");
    }

    private static void TestNodeNameRequiresNullTerminator()
    {
        using var stream = new MemoryStream(Enumerable.Repeat((byte)1, 64).ToArray());
        using var reader = new EndianBinaryReader(stream);
        try
        {
            VFSUtils.ReadDirectoryNodeName(reader, GameType.ArknightsEndfield, 9);
            throw new InvalidOperationException("unterminated VFS node name unexpectedly succeeded");
        }
        catch (InvalidDataException exception)
        {
            AssertContains(exception.Message, "node 9", "unterminated VFS node identity");
            AssertContains(exception.Message, "not null-terminated", "unterminated VFS node status");
            AssertContains(exception.Message, "encodedLength=64", "unterminated VFS node bounded length");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
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
