using System.Collections.Generic;

namespace AnimeStudio
{
    /// <summary>
    /// Unity Cubemap assets use the Texture2D serialized payload followed by
    /// six optional source-texture pointers. Keeping the shared texture fields
    /// lets the existing BC/HDR decoder consume individual face/mip slices.
    /// </summary>
    public sealed class Cubemap : Texture2D
    {
        public List<PPtr<Texture2D>> m_SourceTextures;

        public Cubemap(ObjectReader reader) : base(reader)
        {
            // Texture2D intentionally leaves the reader at the beginning of
            // inline image bytes because ordinary Texture2D has no fields after
            // the payload. Cubemap appends source-texture pointers, so advance
            // past inline data before reading the derived fields. Streamed
            // Cubemaps are already positioned after StreamingInfo.
            if (string.IsNullOrEmpty(m_StreamData?.path) && image_data?.Size > 0)
            {
                reader.Position = checked(reader.Position + image_data.Size);
                reader.AlignStream();
            }

            // Older Cubemap layouts may end after the shared texture payload.
            if (reader.Remaining == 0)
            {
                m_SourceTextures = new List<PPtr<Texture2D>>();
                return;
            }
            if (reader.Remaining < sizeof(int))
            {
                throw new System.IO.InvalidDataException(
                    $"Cubemap has {reader.Remaining} trailing byte(s), too few for sourceTextureCount.");
            }

            var sourceTextureCount = reader.ReadInt32Count(12, "sourceTextureCount");
            m_SourceTextures = new List<PPtr<Texture2D>>(sourceTextureCount);
            for (var i = 0; i < sourceTextureCount; i++)
            {
                m_SourceTextures.Add(new PPtr<Texture2D>(reader));
            }
        }
    }
}
