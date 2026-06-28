using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.Endfield
{
    public enum EndfieldAudioLanguage
    {
        Chinese,
        English,
        Japanese,
        Korean,
    }

    public static class EndfieldAudioLanguages
    {
        private static readonly EndfieldAudioLanguage[] AllItems =
        {
            EndfieldAudioLanguage.Chinese,
            EndfieldAudioLanguage.English,
            EndfieldAudioLanguage.Japanese,
            EndfieldAudioLanguage.Korean,
        };

        public static IReadOnlyList<EndfieldAudioLanguage> All => AllItems;

        public static string Name(this EndfieldAudioLanguage language) => language switch
        {
            EndfieldAudioLanguage.Chinese => "Chinese",
            EndfieldAudioLanguage.English => "English",
            EndfieldAudioLanguage.Japanese => "Japanese",
            EndfieldAudioLanguage.Korean => "Korean",
            _ => language.ToString(),
        };

        public static string Lowercase(this EndfieldAudioLanguage language) => language switch
        {
            EndfieldAudioLanguage.Chinese => "chinese",
            EndfieldAudioLanguage.English => "english",
            EndfieldAudioLanguage.Japanese => "japanese",
            EndfieldAudioLanguage.Korean => "korean",
            _ => language.ToString().ToLowerInvariant(),
        };

        public static bool TryParse(string value, out EndfieldAudioLanguage language)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "chinese":
                case "cn":
                    language = EndfieldAudioLanguage.Chinese;
                    return true;
                case "english":
                case "en":
                    language = EndfieldAudioLanguage.English;
                    return true;
                case "japanese":
                case "jp":
                    language = EndfieldAudioLanguage.Japanese;
                    return true;
                case "korean":
                case "kr":
                    language = EndfieldAudioLanguage.Korean;
                    return true;
                default:
                    language = default;
                    return false;
            }
        }
    }

    public sealed class EndfieldAudioMap
    {
        private const ulong FnvOffset = 0xcbf29ce484222325UL;
        private const ulong FnvPrime = 0x100000001b3UL;
        private readonly Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);

        public static EndfieldAudioMap FromAudioDialog(JToken audioDialog, EndfieldAudioLanguage language)
        {
            var map = new EndfieldAudioMap();
            if (audioDialog is not JObject obj)
            {
                return map;
            }

            foreach (var property in obj.Properties())
            {
                var path = property.Value?["path"]?.Value<string>();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                map.entries[PathToHash(path, language.Lowercase())] = MakeOutputPath(path);
            }

            return map;
        }

        public string GetPath(string hash) =>
            entries.TryGetValue(hash, out var path) ? path : null;

        public int Count => entries.Count;

        // The canonical voice path the game hashed to the Wwise media id. Used only
        // for hashing (PathToHash); it must keep the language segment and the leading
        // v<major>d<minor> batch folder so the hash matches.
        public static string MakeVoicePath(string path, string language) =>
            $"voice/{language}/{path}".Replace('\\', '/').ToLowerInvariant();

        // The on-disk output layout. Drops the redundant language segment (already in
        // the output root) and the leading v<major>d<minor> batch folder (v1d0..v1d3),
        // merging the batches into a single voice/ tree.
        public static string MakeOutputPath(string path)
        {
            var normalized = path.Replace('\\', '/').ToLowerInvariant();
            var slash = normalized.IndexOf('/');
            if (slash > 0 && IsBatchFolder(normalized[..slash]))
            {
                normalized = normalized[(slash + 1)..];
            }
            return $"voice/{normalized}";
        }

        private static bool IsBatchFolder(string segment)
        {
            // Matches batch folders like v1d0, v1d3, v2d10: 'v', digits, 'd', digits.
            if (segment.Length < 4 || segment[0] != 'v')
            {
                return false;
            }
            var d = segment.IndexOf('d');
            if (d <= 1 || d >= segment.Length - 1)
            {
                return false;
            }
            for (var i = 1; i < segment.Length; i++)
            {
                if (i != d && !char.IsDigit(segment[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public static string PathToHash(string path, string language)
        {
            var fullPath = MakeVoicePath(path, language);
            return Fnv1A64(System.Text.Encoding.UTF8.GetBytes(fullPath)).ToString("x");
        }

        public static ulong Fnv1A64(ReadOnlySpan<byte> data)
        {
            unchecked
            {
                var hash = FnvOffset;
                foreach (var b in data)
                {
                    hash *= FnvPrime;
                    hash ^= b;
                }
                return hash;
            }
        }
    }
}
