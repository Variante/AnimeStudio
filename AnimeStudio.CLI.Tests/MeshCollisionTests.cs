using AnimeStudio;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

static class MeshCollisionTests
{
    private static ObjectReader Reader(byte[] bytes, GameType game = GameType.ArknightsEndfield)
    {
        var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
        file.header = new SerializedFileHeader();
        file.fileName = "synthetic";
        return new ObjectReader(new EndianBinaryReader(new MemoryStream(bytes), EndianType.LittleEndian),
            file, new ObjectInfo { classID = (int)ClassIDType.Mesh, byteSize = (uint)bytes.Length, m_PathID = 42 },
            new Game(game, "test"));
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }

    public static int Run()
    {
        var tree = new TypeTree { m_Nodes = new()
        {
            new("Mesh", "Base", 0, false),
            new("bool", "m_CollisionMeshBaked", 1, true),
            new("CompressedMesh", "m_CompressedMesh", 1, false),
            new("int", "packed", 2, false),
            new("int", "tail", 1, false)
        }};
        var baked = new byte[] { 1, 0, 0, 0, 77, 0, 0, 0 };
        using (var reader = Reader(baked))
        {
            var dump = TypeTreeHelper.ReadTypeString(tree, reader);
            Check(dump.Contains("tail = 77") && !dump.Contains("m_CompressedMesh"), "baked Dump layout");
            var typed = TypeTreeHelper.ReadType(tree, reader);
            Check(typed["tail"] is int tail && tail == 77 && !typed.Contains("m_CompressedMesh"), "baked dictionary layout");
        }
        Check(tree.m_Nodes.Count == 5, "shared tree must not change");
        using (var reader = Reader(new byte[] { 0, 0, 0, 0, 88, 0, 0, 0, 77, 0, 0, 0 }))
        {
            var dump = TypeTreeHelper.ReadTypeString(tree, reader);
            Check(dump.Contains("packed = 88") && dump.Contains("tail = 77"), "unbaked retains compressed data");
            Check(TypeTreeHelper.ReadType(tree, reader).Contains("m_CompressedMesh"), "unbaked dictionary");
        }
        using (var reader = Reader(new byte[] { 1, 0, 0, 0, 88, 0, 0, 0, 77, 0, 0, 0 }, GameType.Normal))
            Check(TypeTreeHelper.ReadTypeString(tree, reader).Contains("packed = 88"), "other games retain tree");
        foreach (var bytes in new[] { baked[..6], baked.Concat(new byte[4]).ToArray() })
        {
            using var reader = Reader(bytes);
            try { TypeTreeHelper.ReadTypeString(tree, reader); throw new Exception("malformed baked Mesh accepted"); }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException) { }
        }
        var info = CollisionPayloadInfo.Describe(new byte[] { 78, 88, 83, 1 });
        Check(info.ByteCount == 4 && info.HeaderHex == "4E585301" && info.Sha256.Length == 64, "collision diagnostics");
        Check(CollisionPayloadInfo.Describe(null).ByteCount == 0, "absent collision diagnostics");
        var mesh = (Mesh)RuntimeHelpers.GetUninitializedObject(typeof(Mesh));
        mesh.m_CollisionMeshOnly = mesh.m_CollisionMeshBaked = true;
        var json = JsonConvert.SerializeObject(mesh);
        Check(json.Contains("\"m_CollisionMeshOnly\":true") && json.Contains("\"m_CollisionMeshBaked\":true"), "typed JSON exposes flags");
        Console.WriteLine("Mesh collision type-tree and diagnostic tests passed.");
        return 0;
    }
}
