using System;
using System.Diagnostics;
using System.IO;
using CUETools.Codecs.FLAKE;

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
            ConvertBytes(wemData, outputPath, flac: false);
        }

        public void ConvertBytesToFlac(byte[] wemData, string outputPath)
        {
            ConvertBytes(wemData, outputPath, flac: true);
        }

        private void ConvertBytes(byte[] wemData, string outputPath, bool flac)
        {
            var tempInput = Path.Combine(Path.GetTempPath(), $"AnimeStudio_{Guid.NewGuid():N}.wem");
            try
            {
                File.WriteAllBytes(tempInput, wemData);
                if (flac)
                {
                    ConvertToFlac(tempInput, outputPath);
                }
                else
                {
                    ConvertToWav(tempInput, outputPath);
                }
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

        private void ConvertToWav(string inputPath, string outputPath)
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

        private void ConvertToFlac(string inputPath, string outputPath)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var tempOutput = outputPath + $".{Guid.NewGuid():N}.tmp";
            FlakeWriter writer = null;
            try
            {
                using var decoder = CreateVgmstreamPipeProcess(inputPath);
                decoder.Start();

                var decoderErrorTask = decoder.StandardError.ReadToEndAsync();
                var reader = new WAVReader(string.Empty, decoder.StandardOutput.BaseStream);
                writer = new FlakeWriter(
                    tempOutput,
                    null,
                    new FlakeWriterSettings
                    {
                        PCM = reader.PCM,
                        EncoderMode = "5",
                    }
                )
                {
                    DoSeekTable = false,
                };
                var buffer = new AudioBuffer(reader, 65536);
                while (reader.Read(buffer, -1) > 0)
                {
                    writer.Write(buffer);
                }
                reader.Close();
                writer.Close();
                writer = null;

                decoder.WaitForExit();
                var decoderError = decoderErrorTask.GetAwaiter().GetResult();

                if (decoder.ExitCode != 0)
                {
                    throw new EndfieldVfsException(
                        $"vgmstream conversion failed: exit code {decoder.ExitCode}, stderr: {decoderError}"
                    );
                }
                File.Move(tempOutput, outputPath, true);
            }
            finally
            {
                if (writer != null)
                {
                    try
                    {
                        writer.Delete();
                    }
                    catch
                    {
                        // Best-effort encoder cleanup.
                    }
                }
                try
                {
                    if (File.Exists(tempOutput))
                    {
                        File.Delete(tempOutput);
                    }
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }

        private Process CreateVgmstreamPipeProcess(string inputPath)
        {
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
            process.StartInfo.ArgumentList.Add("-p");
            process.StartInfo.ArgumentList.Add(inputPath);
            return process;
        }

    }
}
