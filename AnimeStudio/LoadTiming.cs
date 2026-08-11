using System;
using System.Diagnostics;
using System.Text;

namespace AnimeStudio
{
    /// <summary>
    /// Opt-in phase timing for the asset load path.
    ///
    /// The .NET sample profiler cannot attribute this path reliably: the block
    /// loop dispatches through <c>dynamic</c>, which corrupts stack walks and
    /// folds native time onto whatever managed frame happens to be resident.
    /// Set <c>ANIMESTUDIO_LOAD_TIMING=1</c> to get exact wall time and call
    /// counts per phase instead. Disabled by default and free when disabled.
    /// </summary>
    public static class LoadTiming
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("ANIMESTUDIO_LOAD_TIMING") == "1";

        public sealed class Phase
        {
            public string Name;
            public long Ticks;
            public long Count;
        }

        private static readonly Phase[] Phases = CreatePhases();

        public enum Id
        {
            OpenTopLevelFile = 0,
            PreProcessing,
            FilterOffsets,
            BlockOffsetLoop,
            SubReaderCtor,
            ContainerFileCtor,
            InnerFileLoop,
            AssetsFromMemory,
            SerializedFileCtor,
            ReadAssets,
            ProcessAssets,
            BuildAssetData,
            ExportAssets,
            ManagerClear,
            Count,
        }

        private static Phase[] CreatePhases()
        {
            var names = Enum.GetNames(typeof(Id));
            var phases = new Phase[(int)Id.Count];
            for (var i = 0; i < phases.Length; i++)
            {
                phases[i] = new Phase { Name = names[i] };
            }
            return phases;
        }

        /// <summary>Times a scope when enabled; a no-op struct otherwise.</summary>
        public readonly struct Scope : IDisposable
        {
            private readonly int index;
            private readonly long started;

            public Scope(Id id)
            {
                index = (int)id;
                started = Enabled ? Stopwatch.GetTimestamp() : 0L;
            }

            public void Dispose()
            {
                if (!Enabled)
                {
                    return;
                }
                var phase = Phases[index];
                phase.Ticks += Stopwatch.GetTimestamp() - started;
                phase.Count++;
            }
        }

        public static Scope Measure(Id id) => new Scope(id);

        public static void Reset()
        {
            // Counters are cumulative for the whole process: the CLI calls Load
            // once per input file, so per-Load resets would hide the total.
        }

        public static void Report(string label)
        {
            if (!Enabled)
            {
                return;
            }
            var text = new StringBuilder();
            text.AppendLine($"[load-timing] {label}");
            foreach (var phase in Phases)
            {
                if (phase.Count == 0)
                {
                    continue;
                }
                var seconds = (double)phase.Ticks / Stopwatch.Frequency;
                var each = seconds * 1000.0 / phase.Count;
                text.AppendLine(
                    $"[load-timing]   {phase.Name,-20} {seconds,9:F2}s  calls={phase.Count,8:N0}  {each,9:F3} ms/call");
            }
            Console.Error.Write(text.ToString());
            Console.Error.Flush();
        }
    }
}
