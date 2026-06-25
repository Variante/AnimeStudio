using System;
using System.Diagnostics;
using System.IO;

namespace AnimeStudio.Endfield
{
    public sealed class EndfieldVgmstreamConverter
    {
        private readonly string cliPath;
        private readonly string workingDirectory;

        private EndfieldVgmstreamConverter(string cliPath)
        {
            this.cliPath = cliPath;
            workingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory;
        }

        public static EndfieldVgmstreamConverter CreateDefault()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("ANIMESTUDIO_VGMSTREAM_CLI"),
                Path.Combine(AppContext.BaseDirectory, "vgmstream", "vgmstream-cli.exe"),
                Path.Combine(AppContext.BaseDirectory, "vgmstream-cli.exe"),
                Path.Combine(Environment.CurrentDirectory, "tools", "fluffy-dumper-src", "vgmstream", "bin", "windows", "vgmstream-cli.exe"),
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    return new EndfieldVgmstreamConverter(Path.GetFullPath(candidate));
                }
            }

            throw new FileNotFoundException(
                "vgmstream-cli.exe not found. Set ANIMESTUDIO_VGMSTREAM_CLI or place vgmstream-cli.exe beside AnimeStudio.CLI."
            );
        }

        public void ConvertBytes(byte[] wemData, string outputPath)
        {
            var tempInput = Path.Combine(Path.GetTempPath(), $"AnimeStudio_{Guid.NewGuid():N}.wem");
            try
            {
                File.WriteAllBytes(tempInput, wemData);
                Convert(tempInput, outputPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempInput))
                    {
                        File.Delete(tempInput);
                    }
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }

        private void Convert(string inputPath, string outputPath)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(outputPath);
            process.StartInfo.ArgumentList.Add(inputPath);

            process.Start();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new EndfieldVfsException($"conversion failed: exit code {process.ExitCode}, stderr: {stderr}");
            }
        }
    }
}
