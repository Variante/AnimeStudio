#nullable enable
using System.IO.Compression;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Text;
using AnimeStudio.ShaderRecovery;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI;

/// <summary>Offline, format-driven recovery probes that do not load Unity bundles.</summary>
internal static class RecoveryCli
{
    private sealed record ReplayResult(
        [property: JsonProperty("line")] int Line,
        [property: JsonProperty("pathId")] long PathId,
        [property: JsonProperty("source")] string? Source,
        [property: JsonProperty("type")] string? Type,
        [property: JsonProperty("matchCount")] int MatchCount,
        [property: JsonProperty("matches")] JToken[] Matches);

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !IsCommand(args[0])) return false;
        try
        {
            exitCode = args[0].ToLowerInvariant() switch
            {
                "shader-recover" => ShaderRecover(args),
                "inspect-object" => InspectObject(args),
                "audit-refs" => AuditRefs(args),
                "certify-index" => CertifyIndex(args),
                "replay" => Replay(args),
                "schema-diff" => SchemaDiff(args),
                _ => 1,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonConvert.SerializeObject(new { error = ex.Message }, Formatting.Indented));
            exitCode = 1;
        }
        return true;
    }

    private static bool IsCommand(string value)
        => value.Equals("shader-recover", StringComparison.OrdinalIgnoreCase)
        || value.Equals("inspect-object", StringComparison.OrdinalIgnoreCase)
        || value.Equals("audit-refs", StringComparison.OrdinalIgnoreCase)
        || value.Equals("certify-index", StringComparison.OrdinalIgnoreCase)
        || value.Equals("replay", StringComparison.OrdinalIgnoreCase)
        || value.Equals("schema-diff", StringComparison.OrdinalIgnoreCase);

    private static int ShaderRecover(string[] args)
    {
        var input = RequiredPath(args, "--input", 1);
        var output = OptionalPath(args, "--output");
        var bytes = File.ReadAllBytes(input);
        if (!SpirvHlslEmitter.TryEmit(bytes, null, 0, 50, out var source, out var diagnostic))
        {
            WriteJson(output, new
            {
                schema = ShaderRecoveryContract.SchemaVersion,
                status = "error",
                input,
                inputSha256 = ShaderRecoveryContract.ComputeSha256Hex(bytes),
                diagnostic,
            });
            return 2;
        }

        output ??= Path.ChangeExtension(input, ".hlsl");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, source, new UTF8Encoding(false));
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            schema = ShaderRecoveryContract.SchemaVersion,
            status = "recovered",
            input,
            output,
            inputSha256 = ShaderRecoveryContract.ComputeSha256Hex(bytes),
            sourceSha256 = ShaderRecoveryContract.ComputeSha256Hex(ShaderRecoveryContract.Utf8(source)),
        }, Formatting.Indented));
        return 0;
    }

    private static int InspectObject(string[] args)
    {
        var index = RequiredPath(args, "--index", 1);
        var pathId = RequiredLong(args, "--path-id");
        var source = OptionalValue(args, "--source");
        var type = OptionalValue(args, "--type");
        var matches = SelectObjectMatches(ReadIndex(index), pathId, source, type).ToArray();
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            schema = "animestudio.recovery.inspect-object.v1",
            index,
            pathId,
            source,
            type,
            matchCount = matches.Length,
            matches,
        }, Formatting.Indented));
        return matches.Length == 0 ? 2 : 0;
    }

    private static int AuditRefs(string[] args)
    {
        var index = RequiredPath(args, "--index", 1);
        var rows = ReadIndex(index).Where(IsObjectRow).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var samples = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var status in FindStatuses(row))
            {
                counts[status] = counts.TryGetValue(status, out var count) ? count + 1 : 1;
                samples.TryAdd(status, row);
            }
        }
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            schema = "animestudio.recovery.audit-refs.v1",
            index,
            objectCount = rows.Length,
            statusCounts = counts.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
            samples = samples.ToDictionary(pair => pair.Key, pair => pair.Value),
        }, Formatting.Indented));
        return 0;
    }

    private static int CertifyIndex(string[] args)
    {
        var index = RequiredPath(args, "--index", 1);
        var lines = ReadRawLines(index).ToArray();
        var valid = new List<JToken>();
        var malformed = new List<object>();
        foreach (var (line, number) in lines.Select((line, i) => (line, i + 1)))
        {
            try { valid.Add(JToken.Parse(line)); }
            catch (Exception ex) { malformed.Add(new { line = number, error = ex.Message }); }
        }
        var summary = valid.LastOrDefault(IsSummaryRow) as JObject;
        var complete = summary?["complete"]?.Value<bool>() == true;
        var report = new
        {
            schema = "animestudio.recovery.certify-index.v1",
            index,
            lineCount = lines.Length,
            validLineCount = valid.Count,
            malformedLineCount = malformed.Count,
            objectCount = valid.Count(IsObjectRow),
            summaryPresent = summary != null,
            complete,
            certified = summary != null && complete && malformed.Count == 0,
            statusCounts = valid.Where(IsObjectRow)
                .SelectMany(FindStatuses)
                .GroupBy(status => status, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => group.Count()),
            malformed,
        };
        Console.WriteLine(JsonConvert.SerializeObject(report, Formatting.Indented));
        return report.certified ? 0 : 2;
    }

    private static int Replay(string[] args)
    {
        var index = RequiredPath(args, "--index", 1);
        var requests = RequiredPath(args, "--requests", 2);
        var results = new List<ReplayResult>();
        foreach (var (line, number) in ReadRawLines(requests).Select((line, i) => (line, i + 1)))
        {
            var request = JObject.Parse(line);
            var pathId = GetLong(request, "pathId", "PathID")
                ?? throw new ArgumentException($"Replay request line {number} is missing pathId.");
            var source = request["source"]?.Value<string>();
            var type = request["type"]?.Value<string>();
            var matches = SelectObjectMatches(ReadIndex(index), pathId, source, type).ToArray();
            results.Add(new ReplayResult(number, pathId, source, type, matches.Length, matches));
        }
        var missing = results.Count(result => result.MatchCount == 0);
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            schema = "animestudio.recovery.replay.v1",
            index,
            requests,
            requestCount = results.Count,
            missingCount = missing,
            results,
        }, Formatting.Indented));
        return missing == 0 ? 0 : 2;
    }

    private static int SchemaDiff(string[] args)
    {
        var left = JToken.Parse(File.ReadAllText(RequiredPath(args, "--left", 1)));
        var right = JToken.Parse(File.ReadAllText(RequiredPath(args, "--right", 1)));
        var differences = new List<object>();
        CompareShape(left, right, "$", differences);
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            schema = "animestudio.recovery.schema-diff.v1",
            equal = differences.Count == 0,
            differences,
        }, Formatting.Indented));
        return differences.Count == 0 ? 0 : 2;
    }

    private static IEnumerable<JToken> ReadIndex(string path)
        => ReadRawLines(path).Select(JToken.Parse);

    private static IEnumerable<JToken> SelectObjectMatches(IEnumerable<JToken> rows, long pathId, string? source, string? type)
        => rows.Where(row => IsObjectRow(row)
            && GetLong(row, "pathId", "PathID") == pathId
            && (source == null || ContainsValue(row, source, "source", "sourceFile", "cab", "container"))
            && (type == null || ContainsValue(row, type, "type", "className", "class")));

    private static IEnumerable<string> ReadRawLines(string path)
    {
        using var stream = File.OpenRead(path);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(stream, CompressionMode.Decompress)
            : stream;
        using var reader = new StreamReader(input, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) yield return line;
    }

    private static bool IsObjectRow(JToken token)
        => token is JObject obj && !IsSummaryRow(obj) && GetLong(obj, "pathId", "PathID") != null;

    private static bool IsSummaryRow(JToken token)
        => token["kind"]?.Value<string>()?.Equals("summary", StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<string> FindStatuses(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (property.Name.Equals("resolutionStatus", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    var value = property.Value.Value<string>();
                    if (!string.IsNullOrWhiteSpace(value)) yield return value!;
                }
                foreach (var nested in FindStatuses(property.Value)) yield return nested;
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
                foreach (var nested in FindStatuses(item)) yield return nested;
        }
    }

    private static void CompareShape(JToken left, JToken right, string path, List<object> differences)
    {
        if (left.Type != right.Type)
        {
            differences.Add(new { path, kind = "type", left = left.Type.ToString(), right = right.Type.ToString() });
            return;
        }
        if (left is JObject leftObject && right is JObject rightObject)
        {
            var names = leftObject.Properties().Select(p => p.Name)
                .Concat(rightObject.Properties().Select(p => p.Name)).Distinct().OrderBy(name => name);
            foreach (var name in names)
            {
                var l = leftObject[name];
                var r = rightObject[name];
                if (l == null || r == null)
                    differences.Add(new { path = path + "." + name, kind = "property", left = l != null, right = r != null });
                else CompareShape(l, r, path + "." + name, differences);
            }
        }
        else if (left is JArray leftArray && right is JArray rightArray && leftArray.Count > 0 && rightArray.Count > 0)
        {
            CompareShape(leftArray[0], rightArray[0], path + "[0]", differences);
        }
    }

    private static long? GetLong(JToken token, params string[] names)
    {
        foreach (var name in names)
            if (token[name]?.Type == JTokenType.Integer) return token[name]!.Value<long>();
        return null;
    }

    private static bool ContainsValue(JToken token, string expected, params string[] names)
        => names.Any(name => token[name]?.Value<string>()?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true);

    private static string RequiredPath(string[] args, string option, int positionalIndex)
        => OptionalPath(args, option) ?? (args.Length > positionalIndex ? Path.GetFullPath(args[positionalIndex]) : throw new ArgumentException($"Missing {option} <PATH>."));

    private static string? OptionalPath(string[] args, string option)
        => OptionalValue(args, option) is { } value ? Path.GetFullPath(value) : null;

    private static string? OptionalValue(string[] args, string option)
    {
        var index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static long RequiredLong(string[] args, string option)
        => long.TryParse(OptionalValue(args, option), out var value) ? value : throw new ArgumentException($"Missing or invalid {option} <PATH_ID>.");

    private static void WriteJson(string? output, object payload)
    {
        var text = JsonConvert.SerializeObject(payload, Formatting.Indented);
        if (output == null) Console.WriteLine(text);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, text, new UTF8Encoding(false));
        }
    }
}
