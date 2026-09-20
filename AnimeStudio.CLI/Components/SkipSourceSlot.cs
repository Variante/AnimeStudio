namespace AnimeStudio.CLI
{
    /// <summary>One bundle slot for --skip_sources_file: a VFS chunk path and the bundle offset inside it.</summary>
    public sealed class SkipSourceSlot
    {
        public string Source { get; set; }
        public long Offset { get; set; }
    }
}
