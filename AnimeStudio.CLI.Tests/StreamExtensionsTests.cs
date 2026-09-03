using AnimeStudio;

internal static class StreamExtensionsTests
{
    public static void Run()
    {
        TestSizedCopyAcceptsFragmentedReads();
        TestSizedCopyRejectsTruncation();
        TestSizedCopyRejectsNegativeLength();
    }

    private static void TestSizedCopyAcceptsFragmentedReads()
    {
        using var source = new FragmentedReadStream(new byte[] { 1, 2, 3, 4, 5 }, 2);
        using var destination = new MemoryStream();
        source.CopyTo(destination, 5L);
        AssertBytes(new byte[] { 1, 2, 3, 4, 5 }, destination.ToArray(), "fragmented sized copy");
    }

    private static void TestSizedCopyRejectsTruncation()
    {
        using var source = new FragmentedReadStream(new byte[] { 1, 2, 3 }, 2);
        using var destination = new MemoryStream();
        try
        {
            source.CopyTo(destination, 5L);
            throw new InvalidOperationException("truncated sized copy unexpectedly succeeded");
        }
        catch (EndOfStreamException exception)
        {
            AssertContains(exception.Message, "expected=5", "truncated expected length");
            AssertContains(exception.Message, "actual=3", "truncated actual length");
        }
    }

    private static void TestSizedCopyRejectsNegativeLength()
    {
        using var source = new MemoryStream();
        using var destination = new MemoryStream();
        try
        {
            source.CopyTo(destination, -1L);
            throw new InvalidOperationException("negative sized copy unexpectedly succeeded");
        }
        catch (ArgumentOutOfRangeException)
        {
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

    private sealed class FragmentedReadStream : MemoryStream
    {
        private readonly int _maxRead;

        public FragmentedReadStream(byte[] buffer, int maxRead) : base(buffer)
        {
            _maxRead = maxRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, _maxRead));
        }

        public override int Read(Span<byte> buffer)
        {
            return base.Read(buffer[..Math.Min(buffer.Length, _maxRead)]);
        }
    }
}
