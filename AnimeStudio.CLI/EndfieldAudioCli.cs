using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeStudio.Endfield;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class EndfieldAudioCli
    {
        public static void Run(string[] args)
        {
            var options = ParseOptions(args);
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);

            Console.WriteLine("Loading AudioDialog.json...");
            var audioDialog = LoadAudioDialog(loader);
            var converter = options.Format == AudioOutputFormat.Wav
                ? EndfieldVgmstreamConverter.CreateDefault()
                : null;

            var totalSuccess = 0;
            var totalErrors = 0;
            var totalUnmapped = 0;

            foreach (var language in options.Languages)
            {
                var audioMap = EndfieldAudioMap.FromAudioDialog(audioDialog, language);
                Console.WriteLine($"  Found {audioMap.Count} {language.Name()} audio entries");

                foreach (var blockType in options.BlockTypes(language))
                {
                    Console.WriteLine($"Extracting {language.Name()} audio files from {blockType.GetName()}...");
                    List<(string name, byte[] data)> pckFiles;
                    try
                    {
                        pckFiles = ExtractPckFiles(loader, blockType);
                    }
                    catch (EndfieldVfsException)
                    {
                        Console.WriteLine($"  Skip: No PCK files found in {blockType.GetName()}");
                        continue;
                    }

                    if (pckFiles.Count == 0)
                    {
                        Console.WriteLine("  Skip: No PCK files found");
                        continue;
                    }

                    Console.WriteLine($"  Found {pckFiles.Count} PCK files");
                    foreach (var (pckName, pckData) in pckFiles)
                    {
                        Console.WriteLine($"  Processing {pckName}");
                        EndfieldAkpkPackage package;
                        try
                        {
                            package = EndfieldAkpkPackage.Parse(pckData);
                        }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine($"    Error: Failed to parse {pckName}: {e.Message}");
                            continue;
                        }

                        var successCount = 0;
                        var errorCount = 0;
                        var unmappedCount = 0;
                        var entries = package.Entries.ToArray();

                        Parallel.ForEach(entries, entry =>
                        {
                            var wemData = package.GetWemData(entry);
                            if (wemData.Length < 4 || (!HasMagic(wemData, "RIFF") && !HasMagic(wemData, "RIFX")))
                            {
                                Interlocked.Increment(ref errorCount);
                                return;
                            }

                            var hash = entry.Id.ToString("x");
                            string outputPath;
                            var mappedPath = audioMap.GetPath(hash);
                            if (!string.IsNullOrEmpty(mappedPath))
                            {
                                outputPath = options.Format == AudioOutputFormat.Wav
                                    ? Path.Combine(options.Output, mappedPath.Replace(".wem", ".wav", StringComparison.Ordinal))
                                    : Path.Combine(options.Output, mappedPath);
                            }
                            else
                            {
                                Interlocked.Increment(ref unmappedCount);
                                outputPath = Path.Combine(
                                    options.Output,
                                    "unmapped",
                                    language.Lowercase(),
                                    $"{entry.Id}.{options.Format.Extension()}"
                                );
                            }

                            try
                            {
                                WriteAudioFile(wemData, outputPath, options.Format, converter);
                                Interlocked.Increment(ref successCount);
                            }
                            catch (Exception e)
                            {
                                Console.Error.WriteLine($"    Error: Failed to write {hash}: {e.Message}");
                                Interlocked.Increment(ref errorCount);
                            }
                        });

                        totalSuccess += successCount;
                        totalErrors += errorCount;
                        totalUnmapped += unmappedCount;
                        Console.WriteLine($"    Done: Processed {successCount}/{entries.Length} entries");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Complete: Extracted {totalSuccess} files ({totalUnmapped} unmapped, {totalErrors} errors)");
        }

        public static void PrintHelp()
        {
            Console.WriteLine("Usage: AnimeStudio.CLI audio -s <StreamingAssets> [-o <output>] [-l <language>] [-f <wem|wav>] [-b <block>] [--fallback-assets <StreamingAssets>]");
        }

        private static JToken LoadAudioDialog(EndfieldVfsLoader loader)
        {
            var blockInfo = loader.LoadBlockInfo(EndfieldVfsBlockType.Table);
            foreach (var chunk in blockInfo.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    var data = loader.ExtractFileToBytes(EndfieldVfsBlockType.Table, chunk, file);
                    try
                    {
                        var parsed = EndfieldSparkBuffer.ParseBytes(data);
                        if (parsed.Name == "AudioDialog")
                        {
                            return parsed.Data;
                        }
                    }
                    catch
                    {
                        // fluffy-dumper ignores non-SparkBuffer table rows while looking for AudioDialog.
                    }
                }
            }

            throw new EndfieldVfsException("AudioDialog.bytes not found in Table block");
        }

        private static List<(string name, byte[] data)> ExtractPckFiles(EndfieldVfsLoader loader, EndfieldVfsBlockType blockType)
        {
            var blockInfo = loader.LoadBlockInfo(blockType);

            var files = new List<(string name, byte[] data)>();
            foreach (var chunk in blockInfo.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    if (file.FileName.EndsWith(".pck", StringComparison.Ordinal))
                    {
                        files.Add((file.FileName, loader.ExtractFileToBytes(blockType, chunk, file)));
                    }
                }
            }
            return files;
        }

        private static void WriteAudioFile(byte[] wemData, string outputPath, AudioOutputFormat format, EndfieldVgmstreamConverter converter)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (format == AudioOutputFormat.Wem)
            {
                File.WriteAllBytes(outputPath, wemData);
            }
            else
            {
                converter.ConvertBytes(wemData, outputPath);
            }
        }

        private static bool HasMagic(byte[] data, string magic)
        {
            if (data.Length < magic.Length)
            {
                return false;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (data[i] != (byte)magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static AudioOptions ParseOptions(string[] args)
        {
            var options = new AudioOptions
            {
                Output = "./output",
                LanguageMode = "all",
                Format = AudioOutputFormat.Wav,
                BlockMode = AudioBlockMode.All,
            };

            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
                string value = null;
                var equalsIndex = token.IndexOf('=');
                if (equalsIndex > 0)
                {
                    value = token[(equalsIndex + 1)..];
                    token = token[..equalsIndex];
                }

                switch (token)
                {
                    case "-s":
                    case "--streaming-assets":
                        options.StreamingAssets = value ?? NextValue(args, ref i, token);
                        break;
                    case "--fallback-assets":
                        options.FallbackAssets = value ?? NextValue(args, ref i, token);
                        break;
                    case "-o":
                    case "--output":
                        options.Output = value ?? NextValue(args, ref i, token);
                        break;
                    case "-l":
                    case "--language":
                        options.LanguageMode = value ?? NextValue(args, ref i, token);
                        break;
                    case "-f":
                    case "--format":
                        var rawFormat = value ?? NextValue(args, ref i, token);
                        options.Format = rawFormat.ToLowerInvariant() switch
                        {
                            "wem" => AudioOutputFormat.Wem,
                            "wav" => AudioOutputFormat.Wav,
                            _ => throw new ArgumentException($"unknown format: {rawFormat}"),
                        };
                        break;
                    case "-b":
                    case "--block":
                        options.BlockMode = ParseBlockMode(value ?? NextValue(args, ref i, token));
                        break;
                    default:
                        throw new ArgumentException($"unexpected argument: {token}");
                }
            }

            if (string.IsNullOrEmpty(options.StreamingAssets))
            {
                throw new ArgumentException("--streaming-assets is required");
            }

            return options;
        }

        private static string NextValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value");
            }
            index++;
            return args[index];
        }

        private static AudioBlockMode ParseBlockMode(string value) => value.ToLowerInvariant() switch
        {
            "all" => AudioBlockMode.All,
            "voice" => AudioBlockMode.Voice,
            "audio" => AudioBlockMode.Audio,
            "initial-audio" => AudioBlockMode.InitialAudio,
            "initialaudio" => AudioBlockMode.InitialAudio,
            "audit-audio" => AudioBlockMode.AuditAudio,
            "auditaudio" => AudioBlockMode.AuditAudio,
            _ => throw new ArgumentException($"unknown audio block: {value}"),
        };

        private sealed class AudioOptions
        {
            public string StreamingAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string Output { get; set; }
            public string LanguageMode { get; set; }
            public AudioOutputFormat Format { get; set; }
            public AudioBlockMode BlockMode { get; set; }

            public IReadOnlyList<EndfieldAudioLanguage> Languages
            {
                get
                {
                    if (string.Equals(LanguageMode, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        return EndfieldAudioLanguages.All;
                    }

                    if (EndfieldAudioLanguages.TryParse(LanguageMode, out var language))
                    {
                        return new[] { language };
                    }

                    throw new ArgumentException($"unknown language: {LanguageMode}");
                }
            }

            public IReadOnlyList<EndfieldVfsBlockType> BlockTypes(EndfieldAudioLanguage language) => BlockMode switch
            {
                AudioBlockMode.All => new[]
                {
                    EndfieldVfsBlockType.Audio,
                    EndfieldVfsBlockType.InitialAudio,
                    EndfieldVfsBlockType.AuditAudio,
                    VoiceBlock(language),
                },
                AudioBlockMode.Voice => new[] { VoiceBlock(language) },
                AudioBlockMode.Audio => new[] { EndfieldVfsBlockType.Audio },
                AudioBlockMode.InitialAudio => new[] { EndfieldVfsBlockType.InitialAudio },
                AudioBlockMode.AuditAudio => new[] { EndfieldVfsBlockType.AuditAudio },
                _ => Array.Empty<EndfieldVfsBlockType>(),
            };

            private static EndfieldVfsBlockType VoiceBlock(EndfieldAudioLanguage language) => language switch
            {
                EndfieldAudioLanguage.Chinese => EndfieldVfsBlockType.AudioChinese,
                EndfieldAudioLanguage.English => EndfieldVfsBlockType.AudioEnglish,
                EndfieldAudioLanguage.Japanese => EndfieldVfsBlockType.AudioJapanese,
                EndfieldAudioLanguage.Korean => EndfieldVfsBlockType.AudioKorean,
                _ => EndfieldVfsBlockType.Audio,
            };
        }

        public enum AudioOutputFormat
        {
            Wem,
            Wav,
        }

        private enum AudioBlockMode
        {
            All,
            Voice,
            Audio,
            InitialAudio,
            AuditAudio,
        }
    }

    internal static class AudioOutputFormatExtensions
    {
        public static string Extension(this EndfieldAudioCli.AudioOutputFormat format) =>
            format == EndfieldAudioCli.AudioOutputFormat.Wav ? "wav" : "wem";
    }
}
