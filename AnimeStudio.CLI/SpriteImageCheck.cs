using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// <c>--sprite_images</c>: keep AnimeStudio's own image of every exported
    /// Sprite (<see cref="SpriteHelper.GetImage"/>, the former Sprite PNG
    /// export) as a row of a separate store, and check that the Sprite's crop
    /// document, rendered from the texture as a consumer renders it, gives the
    /// same pixels. The check's counts and first mismatches are written to the
    /// store's <c>meta</c> row <see cref="ReportMetaKey"/>, which the wrapper
    /// reads to fail the stage.
    /// </summary>
    internal sealed class SpriteImageCheck : IDisposable
    {
        public const string Schema = "endfield.sprite-image-store.v1";
        public const string ReportMetaKey = "spriteCheck";
        private const int MaxReported = 200;

        private readonly UnityDocumentStoreWriter store;
        private readonly object sync = new object();
        private readonly List<string> mismatches = new List<string>();
        private long checkedCount;
        private long matchedCount;
        private long mismatchedCount;

        private SpriteImageCheck(UnityDocumentStoreWriter store)
        {
            this.store = store;
            Current = this;
        }

        public static SpriteImageCheck Current { get; private set; }

        public static SpriteImageCheck Open(FileInfo path)
        {
            if (path == null) return null;
            if (Current != null) throw new InvalidOperationException("A sprite image check is already active.");
            return new SpriteImageCheck(UnityDocumentStoreWriter.OpenDetached(path, Schema));
        }

        public void Check(Sprite sprite, SpriteCropPlan plan, string documentPath)
        {
            var name = Path.GetFileNameWithoutExtension(documentPath) + ".png";
            string problem;
            using (var expected = sprite.GetImage())
            {
                if (expected == null)
                {
                    problem = "AnimeStudio renders no image";
                }
                else
                {
                    using (var png = new MemoryStream())
                    {
                        expected.WriteToStream(png, ImageFormat.Png);
                        store.Put("Sprite", name, png.ToArray());
                    }
                    using var rendered = SpriteHelper.RenderCropPlan(plan);
                    problem = Compare(expected, rendered);
                }
            }
            Interlocked.Increment(ref checkedCount);
            if (problem == null)
            {
                Interlocked.Increment(ref matchedCount);
                return;
            }
            Interlocked.Increment(ref mismatchedCount);
            lock (sync)
            {
                if (mismatches.Count < MaxReported) mismatches.Add($"{name}: {problem}");
            }
            Logger.Warning($"[sprite_images] {name}: {problem}");
        }

        private static string Compare(Image<Bgra32> expected, Image<Bgra32> rendered)
        {
            if (rendered == null)
            {
                return "the crop document's texture does not decode";
            }
            if (expected.Width != rendered.Width || expected.Height != rendered.Height)
            {
                return $"size {rendered.Width}x{rendered.Height} from the crop, {expected.Width}x{expected.Height} from AnimeStudio";
            }
            var expectedPixels = new Bgra32[expected.Width * expected.Height];
            var renderedPixels = new Bgra32[rendered.Width * rendered.Height];
            expected.CopyPixelDataTo(expectedPixels);
            rendered.CopyPixelDataTo(renderedPixels);
            var differing = 0;
            var first = -1;
            for (var i = 0; i < expectedPixels.Length; i++)
            {
                if (!expectedPixels[i].Equals(renderedPixels[i]))
                {
                    differing++;
                    if (first < 0) first = i;
                }
            }
            if (differing == 0)
            {
                return null;
            }
            var x = first % expected.Width;
            var y = first / expected.Width;
            return $"{differing} pixel(s) differ, first at ({x}, {y}): crop {renderedPixels[first]}, AnimeStudio {expectedPixels[first]}";
        }

        public void Complete()
        {
            string report;
            lock (sync)
            {
                report = JsonConvert.SerializeObject(new
                {
                    @checked = Interlocked.Read(ref checkedCount),
                    matched = Interlocked.Read(ref matchedCount),
                    mismatched = Interlocked.Read(ref mismatchedCount),
                    mismatches,
                });
            }
            store.Complete(new Dictionary<string, string> { [ReportMetaKey] = report });
        }

        public void Dispose()
        {
            store.Dispose();
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }
}
