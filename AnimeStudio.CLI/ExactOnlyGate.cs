using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// Keeps exported object JSON to content whose layout is proven.
    ///
    /// Endfield payload decoders mark sub-trees they could only partly recover
    /// ($inferred field names, $partial tails, $unparsed bytes, $heuristic
    /// choices). With the gate on, each marked node is replaced by an
    /// <c>{"$undecoded": ...}</c> stub that records where the unproven bytes are,
    /// never guessed values. Proven TypeTree fields around it are kept.
    /// </summary>
    public static class ExactOnlyGate
    {
        /// <summary>On by default; --allow_partial_json turns it off for diagnostics.</summary>
        public static bool Enabled { get; set; } = true;

        private static long excludedCount;

        /// <summary>Objects left out on purpose because they are not exactly decodable.</summary>
        public static long ExcludedCount => System.Threading.Interlocked.Read(ref excludedCount);

        /// <summary>
        /// Record an object the gate leaves out. It is not an export failure:
        /// callers subtract these from "requested but not exported".
        /// </summary>
        public static void RecordExclusion(AssetItem item, string reason)
        {
            System.Threading.Interlocked.Increment(ref excludedCount);
            ExportManifestJsonlWriter.Current?.RecordExcluded(item, reason);
        }

        private static readonly string[] NonExactMarkers =
        {
            "$inferred",
            "$partial",
            "$unparsed",
            "$heuristic",
            "$partialDecoded",
            "$partialDecodeStoppedAt",
            "$unknown",
        };

        private static readonly string[] LocationKeys = { "rid", "type", "class", "offset", "length", "sha256", "size" };

        /// <summary>Replace non-exact nodes in place; returns the number of stubs written.</summary>
        public static int Apply(object payload)
        {
            if (!Enabled || payload == null)
            {
                return 0;
            }
            var count = 0;
            Visit(payload, ref count);
            return count;
        }

        private static object Visit(object node, ref int count)
        {
            switch (node)
            {
                case IDictionary dictionary:
                    if (IsNonExact(dictionary))
                    {
                        count++;
                        return Stub(dictionary);
                    }
                    foreach (var key in dictionary.Keys.Cast<object>().ToList())
                    {
                        if (key is string name && name == "$animestudio")
                        {
                            continue;
                        }
                        dictionary[key] = Visit(dictionary[key], ref count);
                    }
                    return dictionary;
                case IList list when !(node is string):
                    for (var index = 0; index < list.Count; index++)
                    {
                        list[index] = Visit(list[index], ref count);
                    }
                    return list;
                default:
                    return node;
            }
        }

        private static bool IsNonExact(IDictionary dictionary)
        {
            foreach (var marker in NonExactMarkers)
            {
                if (dictionary.Contains(marker) && !(dictionary[marker] is bool flag && !flag))
                {
                    return true;
                }
            }
            return false;
        }

        private static OrderedDictionary Stub(IDictionary dictionary)
        {
            var detail = new OrderedDictionary
            {
                { "markers", NonExactMarkers.Where(dictionary.Contains).ToList() },
            };
            foreach (var key in LocationKeys)
            {
                if (dictionary.Contains(key) && !(dictionary[key] is IDictionary) && !(dictionary[key] is IList))
                {
                    detail[key] = dictionary[key];
                }
            }
            return new OrderedDictionary { { "$undecoded", detail } };
        }
    }
}
