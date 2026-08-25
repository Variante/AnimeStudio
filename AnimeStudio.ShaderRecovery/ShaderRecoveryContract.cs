using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.ShaderRecovery;

/// <summary>Dependency-free contract for shader recovery artifacts.</summary>
public static class ShaderRecoveryContract
{
    public const string SchemaVersion = "animestudio.shader-recovery.v1";

    public static string ComputeSha256Hex(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    public static string NormalizeText(string? text)
        => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    public static byte[] Utf8(string? text)
        => Encoding.UTF8.GetBytes(NormalizeText(text));
}

public sealed record ShaderRecoveryProvenance(
    string Tool,
    string ToolVersion,
    string InputSha256,
    string? MetadataSha256 = null);

public sealed record ShaderRecoveryDiagnostic(
    string Code,
    string Message,
    string Severity = "warning");

public sealed record ShaderRecoveryOutput(
    string Schema,
    string SourceText,
    ShaderRecoveryProvenance Provenance,
    IReadOnlyList<ShaderRecoveryDiagnostic> Diagnostics)
{
    public static ShaderRecoveryOutput FromText(
        ReadOnlySpan<byte> input,
        string? sourceText,
        ShaderRecoveryProvenance provenance,
        IEnumerable<ShaderRecoveryDiagnostic>? diagnostics = null)
        => new(
            ShaderRecoveryContract.SchemaVersion,
            ShaderRecoveryContract.NormalizeText(sourceText),
            provenance with { InputSha256 = ShaderRecoveryContract.ComputeSha256Hex(input) },
            (diagnostics ?? Array.Empty<ShaderRecoveryDiagnostic>()).ToArray());
}
