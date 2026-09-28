using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace AnimeStudio
{
    /// <summary>The texture a Sprite's pixels come from, and the rectangle AnimeStudio cuts out of it.</summary>
    public sealed class SpriteSource
    {
        public Texture2D Texture { get; init; }
        /// <summary>The SpriteAtlas that supplied the texture, or null when the Sprite's own render data did.</summary>
        public SpriteAtlas Atlas { get; init; }
        public Rectf TextureRect { get; init; }
        public Vector2 TextureRectOffset { get; init; }
        public float DownscaleMultiplier { get; init; }
        public SpriteSettings Settings { get; init; }

        /// <summary>True when the texture is resized to <see cref="ScaledWidth"/> x <see cref="ScaledHeight"/> before the crop.</summary>
        public bool Downscaled => DownscaleMultiplier > 0f && DownscaleMultiplier != 1f;
        public int ScaledWidth => Downscaled ? (int)(Texture.m_Width / DownscaleMultiplier) : Texture.m_Width;
        public int ScaledHeight => Downscaled ? (int)(Texture.m_Height / DownscaleMultiplier) : Texture.m_Height;

        /// <summary>The cut rectangle in Unity's bottom-up image of the (scaled) texture.</summary>
        public Rectangle BottomUpRect
        {
            get
            {
                var rectX = (int)Math.Floor(TextureRect.x);
                var rectY = (int)Math.Floor(TextureRect.y);
                var rectRight = Math.Min((int)Math.Ceiling(TextureRect.x + TextureRect.width), ScaledWidth);
                var rectBottom = Math.Min((int)Math.Ceiling(TextureRect.y + TextureRect.height), ScaledHeight);
                return new Rectangle(rectX, rectY, rectRight - rectX, rectBottom - rectY);
            }
        }

        /// <summary>The same rectangle in the top-down image, the orientation of an exported Texture2D PNG.</summary>
        public Rectangle TopDownRect
        {
            get
            {
                var rect = BottomUpRect;
                return new Rectangle(rect.X, ScaledHeight - rect.Bottom, rect.Width, rect.Height);
            }
        }
    }

    /// <summary>How the cut rectangle is turned into the Sprite image, in the top-down orientation.</summary>
    public enum SpriteCropTransform
    {
        None,
        FlipX,
        FlipY,
        Rotate180,
        RotateClockwise90,
        RotateCounterClockwise90,
    }

    /// <summary>
    /// A Sprite image expressed over its texture: crop <see cref="SpriteSource.TopDownRect"/>
    /// out of the top-down (scaled) texture, apply <see cref="Transform"/>, set every
    /// pixel of <see cref="ClearRows"/> to transparent black, and set every fully
    /// transparent pixel of <see cref="ZeroRows"/> to transparent black. The result
    /// equals <see cref="SpriteHelper.GetImage"/> pixel for pixel.
    /// </summary>
    public sealed class SpriteCropPlan
    {
        public SpriteSource Source { get; init; }
        public SpriteCropTransform Transform { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>Rows of the result with cleared pixels (outside a Tight mesh): <c>[y, x0, length0, x1, length1, ...]</c>.</summary>
        public IReadOnlyList<int[]> ClearRows { get; init; }
        /// <summary>
        /// Rows whose fully transparent pixels lose their color, in the same form:
        /// the area the Tight mask fill passes over, where ImageSharp's blend drops
        /// the color of alpha-zero pixels. Only invisible pixels change.
        /// </summary>
        public IReadOnlyList<int[]> ZeroRows { get; init; }
    }

    public static class SpriteHelper
    {
        /// <summary>The texture and rectangle a Sprite is cut from, or null when neither its render data nor its atlas resolves one.</summary>
        public static SpriteSource GetSource(this Sprite m_Sprite)
        {
            if (m_Sprite.m_SpriteAtlas != null && m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlas))
            {
                if (m_SpriteAtlas.m_RenderDataMap.TryGetValue(m_Sprite.m_RenderDataKey, out var spriteAtlasData) && spriteAtlasData.texture.TryGet(out var m_Texture2D))
                {
                    return new SpriteSource
                    {
                        Texture = m_Texture2D,
                        Atlas = m_SpriteAtlas,
                        TextureRect = spriteAtlasData.textureRect,
                        TextureRectOffset = spriteAtlasData.textureRectOffset,
                        DownscaleMultiplier = spriteAtlasData.downscaleMultiplier,
                        Settings = spriteAtlasData.settingsRaw,
                    };
                }
            }
            else
            {
                if (m_Sprite.m_RD.texture.TryGet(out var m_Texture2D))
                {
                    return new SpriteSource
                    {
                        Texture = m_Texture2D,
                        TextureRect = m_Sprite.m_RD.textureRect,
                        TextureRectOffset = m_Sprite.m_RD.textureRectOffset,
                        DownscaleMultiplier = m_Sprite.m_RD.downscaleMultiplier,
                        Settings = m_Sprite.m_RD.settingsRaw,
                    };
                }
            }
            return null;
        }

        public static Image<Bgra32> GetImage(this Sprite m_Sprite)
        {
            var source = m_Sprite.GetSource();
            if (source == null)
            {
                return null;
            }
            var originalImage = source.Texture.ConvertToImage(false);
            if (originalImage == null)
            {
                return null;
            }
            using (originalImage)
            {
                if (source.Downscaled)
                {
                    originalImage.Mutate(x => x.Resize(source.ScaledWidth, source.ScaledHeight));
                }
                var rect = source.BottomUpRect;
                var spriteImage = originalImage.Clone(x => x.Crop(rect));
                return Shape(m_Sprite, source, spriteImage);
            }
        }

        /// <summary>
        /// The Sprite as a crop of its texture. The rotation and the cleared
        /// pixels are read off <see cref="Shape"/> itself, run over probe images
        /// whose pixels name their own coordinates, so the plan reproduces
        /// <see cref="GetImage"/> by construction. Returns null when
        /// <see cref="GetSource"/> does.
        /// </summary>
        public static SpriteCropPlan GetCropPlan(this Sprite m_Sprite)
        {
            var source = m_Sprite.GetSource();
            if (source == null)
            {
                return null;
            }
            var rect = source.BottomUpRect;
            if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > source.ScaledWidth)
            {
                throw new InvalidDataException(
                    $"Sprite {m_Sprite.m_Name} cuts {rect} outside its {source.ScaledWidth}x{source.ScaledHeight} texture {source.Texture.m_Name}");
            }
            var rotated = source.Settings.packed == 1 && source.Settings.packingRotation != SpritePackingRotation.None;
            if (!rotated && source.Settings.packingMode != SpritePackingMode.Tight)
            {
                return new SpriteCropPlan
                {
                    Source = source,
                    Transform = SpriteCropTransform.None,
                    Width = rect.Width,
                    Height = rect.Height,
                    ClearRows = Array.Empty<int[]>(),
                    ZeroRows = Array.Empty<int[]>(),
                };
            }
            using var probeX = ShapeProbe(m_Sprite, source, rect, (x, y) => new Bgra32((byte)(x & 0xFF), (byte)(x >> 8), 0, 255));
            using var probeY = ShapeProbe(m_Sprite, source, rect, (x, y) => new Bgra32((byte)(y & 0xFF), (byte)(y >> 8), 0, 255));
            using var probeZero = ShapeProbe(m_Sprite, source, rect, (x, y) => new Bgra32(255, 255, 255, 0));
            return ReadProbe(m_Sprite.m_Name, source, rect.Width, rect.Height, probeX, probeY, probeZero);
        }

        /// <summary>
        /// Render a plan from the top-down texture image (<c>ConvertToImage(true)</c>,
        /// the exported Texture2D) the way a consumer of the plan does. Returns null
        /// when the texture does not decode.
        /// </summary>
        public static Image<Bgra32> RenderCropPlan(SpriteCropPlan plan)
        {
            var texture = plan.Source.Texture.ConvertToImage(true);
            if (texture == null)
            {
                return null;
            }
            using (texture)
            {
                if (plan.Source.Downscaled)
                {
                    texture.Mutate(x => x.Resize(plan.Source.ScaledWidth, plan.Source.ScaledHeight));
                }
                var crop = texture.Clone(x => x.Crop(plan.Source.TopDownRect));
                switch (plan.Transform)
                {
                    case SpriteCropTransform.FlipX:
                        crop.Mutate(x => x.Flip(FlipMode.Horizontal));
                        break;
                    case SpriteCropTransform.FlipY:
                        crop.Mutate(x => x.Flip(FlipMode.Vertical));
                        break;
                    case SpriteCropTransform.Rotate180:
                        crop.Mutate(x => x.Flip(FlipMode.Horizontal).Flip(FlipMode.Vertical));
                        break;
                    case SpriteCropTransform.RotateClockwise90:
                        crop.Mutate(x => x.Rotate(RotateMode.Rotate90));
                        break;
                    case SpriteCropTransform.RotateCounterClockwise90:
                        crop.Mutate(x => x.Rotate(RotateMode.Rotate270));
                        break;
                }
                crop.ProcessPixelRows(rows =>
                {
                    foreach (var clear in plan.ClearRows)
                    {
                        var row = rows.GetRowSpan(clear[0]);
                        for (var i = 1; i + 1 < clear.Length; i += 2)
                        {
                            row.Slice(clear[i], clear[i + 1]).Clear();
                        }
                    }
                    foreach (var zero in plan.ZeroRows)
                    {
                        var row = rows.GetRowSpan(zero[0]);
                        for (var i = 1; i + 1 < zero.Length; i += 2)
                        {
                            foreach (ref var pixel in row.Slice(zero[i], zero[i + 1]))
                            {
                                if (pixel.A == 0) pixel = default;
                            }
                        }
                    }
                });
                return crop;
            }
        }

        // The rotation, Tight mesh mask and final flip GetImage applies to the
        // bottom-up cut. Nothing here reads pixel values, so a probe image of
        // the cut's size takes exactly the same path as the real pixels.
        private static Image<Bgra32> Shape(Sprite m_Sprite, SpriteSource source, Image<Bgra32> spriteImage)
        {
            var settingsRaw = source.Settings;
            var textureRectOffset = source.TextureRectOffset;
            var rect = new Rectangle(0, 0, spriteImage.Width, spriteImage.Height);
            if (settingsRaw.packed == 1)
            {
                //RotateAndFlip
                switch (settingsRaw.packingRotation)
                {
                    case SpritePackingRotation.FlipHorizontal:
                        spriteImage.Mutate(x => x.Flip(FlipMode.Horizontal));
                        break;
                    case SpritePackingRotation.FlipVertical:
                        spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                        break;
                    case SpritePackingRotation.Rotate180:
                        spriteImage.Mutate(x => x.Rotate(180));
                        break;
                    case SpritePackingRotation.Rotate90:
                        spriteImage.Mutate(x => x.Rotate(270));
                        break;
                }
            }

            //Tight
            if (settingsRaw.packingMode == SpritePackingMode.Tight)
            {
                try
                {
                    var matrix = Matrix3x2.CreateScale(m_Sprite.m_PixelsToUnits);
                    matrix *= Matrix3x2.CreateTranslation(m_Sprite.m_Rect.width * m_Sprite.m_Pivot.X - textureRectOffset.X, m_Sprite.m_Rect.height * m_Sprite.m_Pivot.Y - textureRectOffset.Y);
                    var triangles = GetTriangles(m_Sprite.m_RD);
                    var points = triangles.Select(x => x.Select(y => new PointF(y.X, y.Y)));
                    var pathBuilder = new PathBuilder(matrix);
                    foreach (var p in points)
                    {
                        pathBuilder.AddLines(p);
                        pathBuilder.CloseFigure();
                    }
                    var path = pathBuilder.Build();
                    var options = new DrawingOptions
                    {
                        GraphicsOptions = new GraphicsOptions
                        {
                            Antialias = false,
                            AlphaCompositionMode = PixelAlphaCompositionMode.DestOut
                        }
                    };
                    if (triangles.Length < 1024)
                    {
                        var rectP = new RectangularPolygon(0, 0, rect.Width, rect.Height);
                        try
                        {
                            spriteImage.Mutate(x => x.Fill(options, SixLabors.ImageSharp.Color.Red, rectP.Clip(path.Clip())));
                            spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                            return spriteImage;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // ignored
                        }
                    }
                    using (var mask = new Image<Bgra32>(rect.Width, rect.Height, SixLabors.ImageSharp.Color.Black))
                    {
                        mask.Mutate(x => x.Fill(options, SixLabors.ImageSharp.Color.Red, path));
                        var brush = new ImageBrush(mask);
                        spriteImage.Mutate(x => x.Fill(options, brush));
                        spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
                        return spriteImage;
                    }
                }
                catch (Exception e)
                {
                    Logger.Warning($"{m_Sprite.m_Name} Unable to render the packed sprite correctly.\n{e}");
                }
            }

            //Rectangle
            spriteImage.Mutate(x => x.Flip(FlipMode.Vertical));
            return spriteImage;
        }

        // A bottom-up image of the cut whose pixel at top-down (x, y) is
        // pixel(x, y), run through Shape.
        private static Image<Bgra32> ShapeProbe(Sprite m_Sprite, SpriteSource source, Rectangle rect, Func<int, int, Bgra32> pixel)
        {
            var probe = new Image<Bgra32>(rect.Width, rect.Height);
            probe.ProcessPixelRows(rows =>
            {
                for (var bottomUpY = 0; bottomUpY < rows.Height; bottomUpY++)
                {
                    var topDownY = rect.Height - 1 - bottomUpY;
                    var row = rows.GetRowSpan(bottomUpY);
                    for (var x = 0; x < row.Length; x++)
                    {
                        row[x] = pixel(x, topDownY);
                    }
                }
            });
            try
            {
                return Shape(m_Sprite, source, probe);
            }
            catch
            {
                probe.Dispose();
                throw;
            }
        }

        private static (int Width, int Height, Func<int, int, (int X, int Y)> Source) TransformOf(SpriteCropTransform transform, int width, int height)
        {
            // The top-down cut pixel each result pixel (u, v) shows.
            return transform switch
            {
                SpriteCropTransform.None => (width, height, (u, v) => (u, v)),
                SpriteCropTransform.FlipX => (width, height, (u, v) => (width - 1 - u, v)),
                SpriteCropTransform.FlipY => (width, height, (u, v) => (u, height - 1 - v)),
                SpriteCropTransform.Rotate180 => (width, height, (u, v) => (width - 1 - u, height - 1 - v)),
                SpriteCropTransform.RotateClockwise90 => (height, width, (u, v) => (v, height - 1 - u)),
                SpriteCropTransform.RotateCounterClockwise90 => (height, width, (u, v) => (width - 1 - v, u)),
                _ => throw new ArgumentOutOfRangeException(nameof(transform)),
            };
        }

        private static SpriteCropPlan ReadProbe(string spriteName, SpriteSource source, int width, int height, Image<Bgra32> probeX, Image<Bgra32> probeY, Image<Bgra32> probeZero)
        {
            if (probeX.Width != probeY.Width || probeX.Height != probeY.Height || probeX.Width != probeZero.Width || probeX.Height != probeZero.Height)
            {
                throw new InvalidDataException($"Sprite {spriteName} probes disagree on the result size");
            }
            int outWidth = probeX.Width, outHeight = probeX.Height;
            var xs = new int[outWidth * outHeight];
            var ys = new int[outWidth * outHeight];
            var cleared = new bool[outWidth * outHeight];
            var zeroed = new bool[outWidth * outHeight];
            probeZero.ProcessPixelRows(rows =>
            {
                for (var v = 0; v < rows.Height; v++)
                {
                    var row = rows.GetRowSpan(v);
                    for (var u = 0; u < row.Length; u++)
                    {
                        var pixel = row[u];
                        if (pixel.A != 0)
                        {
                            throw new InvalidDataException($"Sprite {spriteName} shape makes a transparent pixel visible at ({u}, {v})");
                        }
                        if (pixel.R == 0 && pixel.G == 0 && pixel.B == 0)
                        {
                            zeroed[v * outWidth + u] = true;
                        }
                        else if (pixel.R != 255 || pixel.G != 255 || pixel.B != 255)
                        {
                            throw new InvalidDataException($"Sprite {spriteName} shape recolors a transparent pixel at ({u}, {v}) to {pixel}");
                        }
                    }
                }
            });
            void Read(Image<Bgra32> probe, int[] values)
            {
                probe.ProcessPixelRows(rows =>
                {
                    for (var v = 0; v < rows.Height; v++)
                    {
                        var row = rows.GetRowSpan(v);
                        for (var u = 0; u < row.Length; u++)
                        {
                            var pixel = row[u];
                            var index = v * outWidth + u;
                            if (pixel.A == 0)
                            {
                                cleared[index] = true;
                            }
                            else if (pixel.A != 255)
                            {
                                throw new InvalidDataException($"Sprite {spriteName} shape leaves partial alpha {pixel.A} at ({u}, {v})");
                            }
                            values[index] = pixel.R | (pixel.G << 8);
                        }
                    }
                });
            }
            Read(probeX, xs);
            Read(probeY, ys);

            SpriteCropTransform? found = null;
            foreach (var candidate in Enum.GetValues<SpriteCropTransform>())
            {
                var (candidateWidth, candidateHeight, sourceOf) = TransformOf(candidate, width, height);
                if (candidateWidth != outWidth || candidateHeight != outHeight)
                {
                    continue;
                }
                var matches = true;
                for (var v = 0; v < outHeight && matches; v++)
                {
                    for (var u = 0; u < outWidth; u++)
                    {
                        var index = v * outWidth + u;
                        if (cleared[index])
                        {
                            continue;
                        }
                        var (sx, sy) = sourceOf(u, v);
                        if (xs[index] != sx || ys[index] != sy)
                        {
                            matches = false;
                            break;
                        }
                    }
                }
                if (matches)
                {
                    found = candidate;
                    break;
                }
            }
            if (found == null)
            {
                throw new InvalidDataException($"Sprite {spriteName} shape is not a crop under any flip or quarter rotation");
            }

            // A cleared pixel is transparent black already; list it once.
            for (var i = 0; i < zeroed.Length; i++)
            {
                if (cleared[i]) zeroed[i] = false;
            }
            return new SpriteCropPlan
            {
                Source = source,
                Transform = found.Value,
                Width = outWidth,
                Height = outHeight,
                ClearRows = Runs(cleared, outWidth, outHeight),
                ZeroRows = Runs(zeroed, outWidth, outHeight),
            };
        }

        // The set pixels of a mask as rows [y, x0, length0, x1, length1, ...].
        private static List<int[]> Runs(bool[] mask, int width, int height)
        {
            var rows = new List<int[]>();
            var runs = new List<int>();
            for (var v = 0; v < height; v++)
            {
                runs.Clear();
                var u = 0;
                while (u < width)
                {
                    if (!mask[v * width + u])
                    {
                        u++;
                        continue;
                    }
                    var start = u;
                    while (u < width && mask[v * width + u])
                    {
                        u++;
                    }
                    runs.Add(start);
                    runs.Add(u - start);
                }
                if (runs.Count > 0)
                {
                    var row = new int[runs.Count + 1];
                    row[0] = v;
                    runs.CopyTo(row, 1);
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static Vector2[][] GetTriangles(SpriteRenderData m_RD)
        {
            if (m_RD.vertices != null) //5.6 down
            {
                var vertices = m_RD.vertices.Select(x => (Vector2)x.pos).ToArray();
                var triangleCount = m_RD.indices.Length / 3;
                var triangles = new Vector2[triangleCount][];
                for (int i = 0; i < triangleCount; i++)
                {
                    var first = m_RD.indices[i * 3];
                    var second = m_RD.indices[i * 3 + 1];
                    var third = m_RD.indices[i * 3 + 2];
                    var triangle = new[] { vertices[first], vertices[second], vertices[third] };
                    triangles[i] = triangle;
                }
                return triangles;
            }
            else //5.6 and up
            {
                var triangles = new List<Vector2[]>();
                var m_VertexData = m_RD.m_VertexData;
                var m_Channel = m_VertexData.m_Channels[0]; //kShaderChannelVertex
                var m_Stream = m_VertexData.m_Streams[m_Channel.stream];
                using (var vertexReader = new EndianBinaryReader(new MemoryStream(m_VertexData.m_DataSize), EndianType.LittleEndian))
                {
                    using (var indexReader = new EndianBinaryReader(new MemoryStream(m_RD.m_IndexBuffer), EndianType.LittleEndian))
                    {
                        foreach (var subMesh in m_RD.m_SubMeshes)
                        {
                            vertexReader.BaseStream.Position = m_Stream.offset + subMesh.firstVertex * m_Stream.stride + m_Channel.offset;

                            var vertices = new Vector2[subMesh.vertexCount];
                            for (int v = 0; v < subMesh.vertexCount; v++)
                            {
                                vertices[v] = new Vector3(vertexReader.ReadSingle(), vertexReader.ReadSingle(), vertexReader.ReadSingle());
                                vertexReader.BaseStream.Position += m_Stream.stride - 12;
                            }

                            indexReader.BaseStream.Position = subMesh.firstByte;

                            var triangleCount = subMesh.indexCount / 3u;
                            for (int i = 0; i < triangleCount; i++)
                            {
                                var first = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var second = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var third = indexReader.ReadUInt16() - subMesh.firstVertex;
                                var triangle = new[] { vertices[first], vertices[second], vertices[third] };
                                triangles.Add(triangle);
                            }
                        }
                    }
                }
                return triangles.ToArray();
            }
        }
    }
}
