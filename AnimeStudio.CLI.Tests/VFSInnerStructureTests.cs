using System.Reflection;
using AnimeStudio;
using AnimeStudio.CLI;
using AnimeStudio.Endfield;

/// <summary>Negative and exact-copy fixtures for Endfield's nested VFS containers.</summary>
internal static class VFSInnerStructureTests
{
    private static readonly MethodInfo ReadFilesMethod = typeof(VFSFile).GetMethod(
        "ReadFiles", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VFSFile.ReadFiles was not found");

    public static void Run()
    {
        TestNodeCopiesExactlyDeclaredLength();
        TestOverlappingNodesFailClosed();
        TestDuplicateNodePathsFailClosed();
        TestTraversalNodePathFailsClosed();
        TestShortNodeReadFailsClosed();
        TestInnerAuditRejectsOutputCollision();
        TestInnerAuditRejectsMixedUnsupportedBlockTypes();
        TestExplicitChunkPathIsTheAuditedSource();
        TestUnparseableSelectedMetadataIsTerminal();
    }

    private static void TestNodeCopiesExactlyDeclaredLength()
    {
        var vfs = InvokeReadFiles(
            new byte[] { 1, 2, 3, 4, 5 },
            new BundleFile.Node { path = "CAB-a", offset = 1, size = 2 });
        AssertEqual(1, vfs.fileList.Count, "exact node count");
        using var stream = vfs.fileList[0].stream;
        AssertBytes(new byte[] { 2, 3 }, ReadAll(stream), "exact node bytes");
    }

    private static void TestOverlappingNodesFailClosed()
    {
        var exception = AssertReadFilesFails(
            new byte[] { 1, 2, 3, 4 },
            new BundleFile.Node { path = "CAB-a", offset = 0, size = 3 },
            new BundleFile.Node { path = "CAB-b", offset = 2, size = 2 });
        AssertContains(exception.Message, "overlapping", "overlap diagnostic");
        AssertContains(exception.Message, "CAB-a", "overlap first path");
        AssertContains(exception.Message, "CAB-b", "overlap second path");
    }

    private static void TestDuplicateNodePathsFailClosed()
    {
        var exception = AssertReadFilesFails(
            new byte[] { 1, 2 },
            new BundleFile.Node { path = "CAB-a", offset = 0, size = 1 },
            new BundleFile.Node { path = "CAB-a", offset = 1, size = 1 });
        AssertContains(exception.Message, "duplicate", "duplicate path diagnostic");
        AssertContains(exception.Message, "CAB-a", "duplicate path identity");
    }

    private static void TestTraversalNodePathFailsClosed()
    {
        var exception = AssertReadFilesFails(
            new byte[] { 1 },
            new BundleFile.Node { path = "../CAB-a", offset = 0, size = 1 });
        AssertContains(exception.Message, "path traversal", "traversal diagnostic");
        AssertContains(exception.Message, "../CAB-a", "traversal path identity");
    }

    private static void TestShortNodeReadFailsClosed()
    {
        var vfs = (VFSFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VFSFile));
        var field = typeof(VFSFile).GetField("m_DirectoryInfo", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VFSFile.m_DirectoryInfo was not found");
        field.SetValue(vfs, new List<BundleFile.Node>
        {
            new() { path = "CAB-a", offset = 0, size = 2 },
        });
        Exception exception;
        try
        {
            InvokeReadFiles(vfs, new ShortReadStream(new byte[] { 1, 2 }, 1), "vfs-inner-fixture");
            throw new InvalidOperationException("short VFS inner fixture unexpectedly succeeded");
        }
        catch (InvalidOperationException e) when (e.Message == "short VFS inner fixture unexpectedly succeeded")
        {
            throw;
        }
        catch (Exception e)
        {
            exception = e;
        }
        AssertContains(exception.Message, "short read", "short read diagnostic");
        AssertContains(exception.Message, "expected=2", "short read expected length");
        AssertContains(exception.Message, "actual=1", "short read actual length");
    }

    private static void TestInnerAuditRejectsOutputCollision()
    {
        var output = Path.Combine(Path.GetTempPath(), "vfs-inner-audit-collision.json");
        var handled = EndfieldVfsCli.TryRun(new[]
        {
            "vfs-inner-audit",
            "--streaming-assets", ".",
            "--block-type", "bundle",
            "--output", output,
            "--summary-json", output,
        }, out var exitCode);
        AssertEqual(true, handled, "output collision command handled");
        AssertEqual(1, exitCode, "output collision exit code");
    }

    private static void TestInnerAuditRejectsMixedUnsupportedBlockTypes()
    {
        var handled = EndfieldVfsCli.TryRun(new[]
        {
            "vfs-inner-audit",
            "--streaming-assets", ".",
            "--block-type", "bundle",
            "--block-type", "table",
        }, out var exitCode);
        AssertEqual(true, handled, "mixed block command handled");
        AssertEqual(1, exitCode, "mixed block exit code");
    }

    private static void TestExplicitChunkPathIsTheAuditedSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vfs-explicit-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var selectedPath = Path.Combine(root, "selected.chk");
            File.WriteAllBytes(selectedPath, new byte[] { 4, 5, 6 });
            var loader = new EndfieldVfsLoader(root);
            var chunk = new EndfieldVfsChunkInfo { Length = 3 };
            var file = new EndfieldVfsFileInfo { Offset = 0, Length = 3 };
            using var output = new MemoryStream();
            var actual = loader.ExtractFileFromPath(selectedPath, chunk, file, output);
            AssertEqual(3L, actual, "explicit source byte count");
            AssertBytes(new byte[] { 4, 5, 6 }, output.ToArray(), "explicit source bytes");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestUnparseableSelectedMetadataIsTerminal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vfs-inner-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var loader = new EndfieldVfsLoader(root);
            var hashDirectory = loader.BlockDirectoryName(EndfieldVfsBlockType.Bundle);
            var blockDirectory = Path.Combine(root, EndfieldVfsLoader.VfsDirectoryName, hashDirectory);
            Directory.CreateDirectory(blockDirectory);
            File.WriteAllBytes(Path.Combine(blockDirectory, hashDirectory + ".blc"), new byte[] { 0 });
            var ledger = Path.Combine(root, "ledger.jsonl.gz");
            var summary = Path.Combine(root, "summary.json");
            var handled = EndfieldVfsCli.TryRun(new[]
            {
                "vfs-inner-audit",
                "--streaming-assets", root,
                "--block-type", "bundle",
                "--output", ledger,
                "--summary-json", summary,
            }, out var exitCode);
            AssertEqual(true, handled, "unparseable metadata command handled");
            AssertEqual(1, exitCode, "unparseable metadata exit code");
            var text = File.ReadAllText(summary);
            AssertContains(text, "\"blockFailureCount\": 1", "unparseable metadata block failure count");
            AssertContains(text, "unparseable_metadata", "unparseable metadata terminal status");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static VFSFile InvokeReadFiles(byte[] bytes, params BundleFile.Node[] nodes)
    {
        var vfs = (VFSFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VFSFile));
        var field = typeof(VFSFile).GetField("m_DirectoryInfo", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VFSFile.m_DirectoryInfo was not found");
        field.SetValue(vfs, nodes.ToList());
        using var input = new MemoryStream(bytes, writable: false);
        InvokeReadFiles(vfs, input, "vfs-inner-fixture");
        return vfs;
    }

    private static void InvokeReadFiles(VFSFile vfs, Stream input, string path)
    {
        try
        {
            ReadFilesMethod.Invoke(vfs, new object[] { input, path });
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static Exception AssertReadFilesFails(byte[] bytes, params BundleFile.Node[] nodes)
    {
        try
        {
            InvokeReadFiles(bytes, nodes);
            throw new InvalidOperationException("malformed VFS inner fixture unexpectedly succeeded");
        }
        catch (Exception exception) when (exception is not InvalidOperationException
            || exception.Message != "malformed VFS inner fixture unexpectedly succeeded")
        {
            return exception;
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string label)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"{label}: byte sequences differ");
    }

    private static void AssertContains(string actual, string expected, string label)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: expected '{expected}' in '{actual}'");
    }

    private sealed class ShortReadStream : MemoryStream
    {
        private readonly int _firstReadLimit;
        private bool _didRead;

        public ShortReadStream(byte[] bytes, int firstReadLimit) : base(bytes, writable: false)
        {
            _firstReadLimit = firstReadLimit;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_didRead)
                return 0;
            _didRead = true;
            return base.Read(buffer, offset, Math.Min(count, _firstReadLimit));
        }
    }
}
