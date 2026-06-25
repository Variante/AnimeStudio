using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AnimeStudio.Endfield
{
    public static class EndfieldDumpProcessors
    {
        public static string ProcessTableFile(byte[] data, string output)
        {
            var parseResult = EndfieldSparkBuffer.ParseBytes(data);
            var outputPath = Path.Combine(output, "Table", $"{parseResult.Name}.json");
            CreateParentDirectory(outputPath);

            var json = parseResult.Data.ToString(Formatting.Indented)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            return outputPath;
        }

        public static string ProcessVideoFile(byte[] data, string fileName, string output)
        {
            var outputName = fileName.EndsWith(".usm", StringComparison.Ordinal)
                ? $"{fileName[..^".usm".Length]}.mp4"
                : fileName;
            var outputPath = Path.Combine(output, outputName);
            CreateParentDirectory(outputPath);

            if (fileName.EndsWith(".usm", StringComparison.Ordinal))
            {
                EndfieldUsmConverter.ConvertBytesToMp4(data, outputPath);
            }
            else
            {
                File.WriteAllBytes(outputPath, data);
            }

            return outputPath;
        }
        public static string ProcessLuaFile(byte[] data, string fileName, string output)
        {
            var content = Encoding.UTF8.GetString(data);
            byte[] encryptedData;
            try
            {
                encryptedData = Convert.FromBase64String(content.Trim());
            }
            catch (FormatException e)
            {
                throw new FormatException($"base64 decode error: {e.Message}", e);
            }

            var decrypted = EndfieldXxtea.Decrypt(encryptedData, EndfieldVfsKeys.XxteaKey);
            var normalized = NormalizeLuaNewlines(decrypted);
            var outputPath = Path.Combine(output, "Lua", LuaOutputName(fileName));
            CreateParentDirectory(outputPath);

            File.WriteAllBytes(outputPath, normalized);
            return outputPath;
        }

        private static string LuaOutputName(string fileName)
        {
            if (fileName.EndsWith(".lua", StringComparison.Ordinal))
            {
                return fileName;
            }

            const string encryptedSuffix = ".lua.enc";
            while (fileName.EndsWith(encryptedSuffix, StringComparison.Ordinal))
            {
                fileName = fileName[..^encryptedSuffix.Length];
            }
            return $"{fileName}.lua";
        }

        private static byte[] NormalizeLuaNewlines(byte[] data)
        {
            using var output = new MemoryStream(data.Length);
            var seenNonWhitespace = false;
            var lastWasEmpty = false;

            foreach (var b in data)
            {
                if (b == 0x0d)
                {
                    continue;
                }

                if (b == 0x0a)
                {
                    if (seenNonWhitespace)
                    {
                        output.WriteByte(0x0a);
                        lastWasEmpty = false;
                    }
                    else if (!lastWasEmpty)
                    {
                        output.WriteByte(0x0a);
                        lastWasEmpty = true;
                    }
                    seenNonWhitespace = false;
                    continue;
                }

                if (b != 0x20 && b != 0x09)
                {
                    seenNonWhitespace = true;
                }

                output.WriteByte(b);
                lastWasEmpty = false;
            }

            return output.ToArray();
        }

        private static void CreateParentDirectory(string outputPath)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }
    }
}
