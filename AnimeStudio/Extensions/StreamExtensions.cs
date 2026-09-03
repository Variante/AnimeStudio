using System.IO;

using System;

namespace AnimeStudio
{
    public static class StreamExtensions
    {
        private const int BufferSize = 81920;

        public static void CopyTo(this Stream source, Stream destination, long size)
        {
            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), size, "Copy length must be non-negative.");
            }

            var buffer = new byte[BufferSize];
            var copied = 0L;
            while (copied < size)
            {
                var toRead = (int)Math.Min(BufferSize, size - copied);
                var read = source.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Sized stream copy truncated: expected={size}, actual={copied}."
                    );
                }
                destination.Write(buffer, 0, read);
                copied += read;
            }
        }

        public static void AlignStream(this Stream stream)
        {
            stream.AlignStream(4);
        }

        public static void AlignStream(this Stream stream, int alignment)
        {
            var pos = stream.Position;
            var mod = pos % alignment;
            if (mod != 0)
            {
                var rem = alignment - mod;
                for (int _ = 0; _ < rem; _++)
                {
                    if (!stream.CanWrite)
                    {
                        throw new IOException("End of stream");
                    }

                    stream.WriteByte(0);
                }
            }
        }
    }
}
