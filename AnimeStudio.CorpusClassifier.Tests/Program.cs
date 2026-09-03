using System.IO.Compression;
using System.Text;
using AnimeStudio.CLI;
using AnimeStudio.Endfield;

static class Program
{
    private static int Main()
    {
        TestExactPngAndCompressionObservation();
        TestShortAndOverlongStreamsFailClosed();
        TestJsonlGzipIsDeterministicAndBounded();
        TestDuplicateAggregationAndTerminalStatuses();
        TestProfileFailurePublishesTerminalOutputs();
        Console.WriteLine("VFS corpus classifier tests passed.");
        return 0;
    }

    private static void TestExactPngAndCompressionObservation()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03 };
        var input = Make("Table/Foo.JSON", bytes, offset: 8, chunkLength: 32);
        var row = EndfieldVfsCorpusClassifier.Observe(input, boundedByteLimit: 4);
        Equal("profiled", row.Status, "exact row status");
        Equal(".json", row.Suffix, "suffix normalized");
        Equal("png", row.Magic, "PNG magic");
        Equal("89504E47", row.MagicHex, "bounded magic bytes");
        Equal("89504E47", row.FirstBytesHex, "first bytes are bounded");
        Equal("00010203", row.LastBytesHex, "last bounded bytes");
        Equal(4, row.BoundedByteLimit, "bound recorded");
        Equal(4, row.Alignment.CommonAlignment, "common offset/size alignment");

        var gzip = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var gzipRow = EndfieldVfsCorpusClassifier.Observe(Make("Audio/clip.wem", gzip));
        Equal("gzip", gzipRow.Magic, "gzip magic");
        Equal("gzip", gzipRow.CompressionSignatures[0], "gzip compression signature");
    }

    private static void TestShortAndOverlongStreamsFailClosed()
    {
        var shortInput = Make("bad.bin", new byte[] { 1, 2, 3 }, chunkLength: 4, declaredLength: 4);
        var shortRow = EndfieldVfsCorpusClassifier.Observe(shortInput);
        Equal("short_read", shortRow.Status, "short stream status");
        Equal(3L, shortRow.BytesRead, "short stream bytes read");
        Equal("short logical file: expected 4, received 3", shortRow.Diagnostic, "short diagnostic");

        var overlongInput = Make("bad.bin", new byte[] { 1, 2, 3, 4 }, declaredLength: 3);
        var overlongRow = EndfieldVfsCorpusClassifier.Observe(overlongInput);
        Equal("failed", overlongRow.Status, "overlong stream status");
        Equal("logical stream has bytes beyond declared length", overlongRow.Diagnostic, "overlong diagnostic");
    }

    private static void TestJsonlGzipIsDeterministicAndBounded()
    {
        var inputs = new[]
        {
            Make("z/path.txt", Encoding.UTF8.GetBytes("hello"), blockId: 19, source: "fallback"),
            Make("a/path.bin", new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x01 }, blockId: 222, source: "primary"),
        };
        var root = Path.Combine(Path.GetTempPath(), "animestudio-vfs-classifier-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstLedger = Path.Combine(root, "one.jsonl.gz");
            var firstSummary = Path.Combine(root, "one.summary.json");
            var secondLedger = Path.Combine(root, "two.jsonl.gz");
            var secondSummary = Path.Combine(root, "two.summary.json");
            var first = EndfieldVfsCorpusClassifier.WriteJsonlGzip(inputs, firstLedger, firstSummary, 3);
            var second = EndfieldVfsCorpusClassifier.WriteJsonlGzip(inputs.Reverse(), secondLedger, secondSummary, 3);
            Equal(2, first.FileCount, "summary file count");
            Equal(true, first.Complete, "complete summary");
            Equal(2, ReadJsonlGzipLineCount(firstLedger), "JSONL row count");
            Equal(true, File.ReadAllBytes(firstLedger).SequenceEqual(File.ReadAllBytes(secondLedger)), "deterministic gzip ledger");
            Equal(File.ReadAllText(firstSummary), File.ReadAllText(secondSummary), "deterministic summary");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestDuplicateAggregationAndTerminalStatuses()
    {
        var bytes = Encoding.UTF8.GetBytes("same payload");
        var inputs = new[]
        {
            Make("Table/one.txt", bytes),
            Make("Table/two.txt", bytes),
            Make("AudioEnglish/skip.wem", bytes, blockId: 102, source: "excluded", status: "excluded", diagnostic: "voice excluded"),
            Make("Table/missing.bin", bytes, status: "unavailable", diagnostic: "chunk missing"),
        };
        var root = Path.Combine(Path.GetTempPath(), "animestudio-vfs-classifier-aggregation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var summary = EndfieldVfsCorpusClassifier.WriteJsonlGzip(
                inputs, Path.Combine(root, "ledger.jsonl.gz"), Path.Combine(root, "summary.json"));
            Equal("table", EndfieldVfsCorpusClassifier.Observe(inputs[0]).PathFamily, "path family");
            Equal("tiny", EndfieldVfsCorpusClassifier.Observe(inputs[0]).SizeBand, "size band");
            Equal(1, summary.ExactDuplicateGroupCount, "exact duplicate group count");
            Equal(2, summary.ExactDuplicateFileCount, "exact duplicate file count");
            Equal((long)bytes.Length * 2, summary.ExactDuplicateBytes, "exact duplicate bytes");
            Equal(1, summary.ExcludedCount, "excluded row count");
            Equal(1, summary.UnavailableCount, "unavailable row count");
            Equal(false, summary.Complete, "unavailable rows block complete publication");
            Equal(true, summary.ClusterCountsReconciled, "cluster file reconciliation");
            Equal(true, summary.ClusterBytesReconciled, "cluster byte reconciliation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestProfileFailurePublishesTerminalOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "animestudio-vfs-profile-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ledger = Path.Combine(root, "ledger.jsonl.gz");
        var summary = Path.Combine(root, "summary.json");
        try
        {
            var handled = EndfieldVfsCli.TryRun(new[]
            {
                "vfs-profile", "--streaming-assets", root, "--output", ledger, "--summary-json", summary,
            }, out var exitCode);
            Equal(true, handled, "profile command recognized");
            Equal(1, exitCode, "missing selected VFS blocks fail nonzero");
            Equal(true, File.Exists(ledger), "failed profile publishes terminal ledger");
            Equal(true, File.Exists(summary), "failed profile publishes terminal summary marker");
            Equal(0, ReadJsonlGzipLineCount(ledger), "empty-root terminal ledger rows");
            var summaryText = File.ReadAllText(summary);
            Equal(true, summaryText.Contains("\"complete\": false", StringComparison.Ordinal),
                "failed profile summary is explicitly incomplete");
            Equal(true, summaryText.Contains("\"unavailableBlockCount\": 23", StringComparison.Ordinal),
                "failed profile reports missing selected blocks");
            Equal(true, summaryText.Contains("\"excludedBlockCount\": 3", StringComparison.Ordinal),
                "failed profile reports deferred voice blocks separately");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int ReadJsonlGzipLineCount(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var count = 0;
        while (reader.ReadLine() != null) count++;
        return count;
    }

    private static EndfieldVfsCorpusFile Make(
        string path,
        byte[] bytes,
        long offset = 0,
        long chunkLength = -1,
        long? declaredLength = null,
        byte blockId = 18,
        string source = "fixture",
        string? status = null,
        string? diagnostic = null)
        => MakeWithStatus(path, bytes, offset, chunkLength, declaredLength, blockId, source, status, diagnostic);

    private static EndfieldVfsCorpusFile MakeWithStatus(
        string path,
        byte[] bytes,
        long offset,
        long chunkLength,
        long? declaredLength,
        byte blockId,
        string source,
        string? status,
        string? diagnostic)
    {
        var length = declaredLength ?? bytes.LongLength;
        return new EndfieldVfsCorpusFile
        {
            BlockTypeValue = blockId,
            VirtualPath = path,
            ChunkFileName = "fixture.chk",
            ChunkMd5 = "00112233445566778899AABBCCDDEEFF",
            ChunkContentMd5 = "FFEEDDCCBBAA99887766554433221100",
            ChunkSource = source,
            ChunkLength = chunkLength < 0 ? offset + bytes.LongLength : chunkLength,
            Offset = offset,
            Length = length,
            MetadataVerified = true,
            StatusOverride = status,
            DiagnosticOverride = diagnostic,
            OpenStream = () => new MemoryStream(bytes, writable: false),
        };
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}
