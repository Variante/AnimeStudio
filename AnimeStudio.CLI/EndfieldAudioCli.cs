using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeStudio.Endfield;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class EndfieldAudioCli
    {
        public static int RunAudit(string[] args)
        {
            var options = ParseAuditOptions(args);
            // Identities to walk from. Supplied by the caller so the reader never has to
            // decide what counts as a name.
            EndfieldAkpkPackage.NamedIdentityHashes = string.IsNullOrEmpty(options.NamedHashFile)
                ? new HashSet<uint>()
                : new HashSet<uint>(File.ReadAllLines(options.NamedHashFile)
                    .Select(line => line.Trim())
                    .Where(line => line.Length == 8)
                    .Select(line => Convert.ToUInt32(line, 16)));
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
            var rows = new List<Dictionary<string, object?>>();
            var failures = 0;
            foreach (var blockType in options.BlockTypes)
            {
                List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> pckFiles;
                EndfieldVfsBlockMainInfo blockInfo;
                try
                {
                    blockInfo = loader.LoadBlockInfo(blockType);
                    pckFiles = ExtractPckFiles(blockInfo);
                }
                catch (EndfieldVfsException e)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["status"] = "missing_block",
                        ["diagnostic"] = BoundDiagnostic(e.Message),
                    });
                    failures++;
                    continue;
                }

                if (IsAudioBlock(blockType)
                    && blockInfo.Chunks.Count > 0
                    && blockInfo.Chunks.All(chunk => IsChunkMissing(loader, blockType, chunk)))
                {
                    var exclusionStatus = blockType is EndfieldVfsBlockType.AudioEnglish
                        or EndfieldVfsBlockType.AudioJapanese
                        or EndfieldVfsBlockType.AudioKorean
                        ? "excluded_missing_voice"
                        : "excluded_missing_audio";
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["status"] = exclusionStatus,
                        ["source"] = "missing_both",
                        ["declaredChunks"] = blockInfo.Chunks.Count,
                        ["declaredFiles"] = blockInfo.GroupFileInfoNum,
                        ["expected"] = "at least one declared audio chunk in primary or fallback root",
                        ["actual"] = 0,
                        ["diagnostic"] = "every declared audio chunk is absent from primary and fallback roots; the block is conditionally ignored",
                    });
                    continue;
                }

                if (pckFiles.Count == 0)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["status"] = "empty_block",
                        ["source"] = options.StreamingAssets,
                        ["declaredChunks"] = blockInfo.Chunks.Count,
                        ["declaredFiles"] = blockInfo.GroupFileInfoNum,
                        ["expected"] = "at least one .pck logical file",
                        ["actual"] = 0,
                        ["diagnostic"] = "audio block metadata contains no .pck logical files",
                    });
                    failures++;
                    continue;
                }

                foreach (var (chunk, file) in pckFiles)
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["path"] = file.FileName,
                        ["chunk"] = chunk.FileName,
                        ["source"] = "unresolved",
                        ["declaredBytes"] = file.Length,
                        ["status"] = "failed",
                    };
                    try
                    {
                        row["source"] = loader.ResolveChunkPath(blockType, chunk);
                        var pckBytes = loader.ExtractFileToBytes(blockType, chunk, file, verifyMd5: true);
                        row["verifiedFileDataMd5"] = Convert.ToHexString(MD5.HashData(pckBytes));
                        var package = EndfieldAkpkPackage.Parse(pckBytes);
                        var mediaRiff = 0;
                        var mediaPlugin = 0;
                        var mediaInvalid = 0;
                        var invalidExamples = new List<Dictionary<string, object?>>();
                        if (!options.HircOnly)
                        {
                            foreach (var entry in package.Entries)
                            {
                                var media = package.GetWemData(entry);
                                if (HasMagic(media, "PLUG"))
                                {
                                    mediaPlugin++;
                                }
                                else if (HasMagic(media, "RIFF") || HasMagic(media, "RIFX"))
                                {
                                    mediaRiff++;
                                }
                                else
                                {
                                    mediaInvalid++;
                                    if (invalidExamples.Count < 8)
                                    {
                                        invalidExamples.Add(new Dictionary<string, object?>
                                        {
                                            ["id"] = entry.Id.ToString("x"),
                                            ["offset"] = entry.Offset,
                                            ["declaredBytes"] = entry.Size,
                                            ["magic"] = MagicPreview(media),
                                        });
                                    }
                                }
                            }
                        }
                        row["package"] = new Dictionary<string, object?>
                        {
                            ["headerSize"] = package.HeaderSize,
                            ["version"] = package.Version,
                            ["encryptedHeader"] = package.EncryptedHeader,
                            ["languageSectorBytes"] = package.LanguageSectorSize,
                            ["banksSectorBytes"] = package.BanksSectorSize,
                            ["soundsSectorBytes"] = package.SoundsSectorSize,
                            ["externalsSectorBytes"] = package.ExternalsSectorSize,
                            ["languages"] = package.Languages.Count,
                            ["banks"] = package.BankCount,
                            ["sounds"] = package.SoundCount,
                            ["externals"] = package.ExternalCount,
                            ["mediaEntries"] = package.Entries.Count,
                            ["mediaRiff"] = mediaRiff,
                            ["mediaPlugin"] = mediaPlugin,
                            ["mediaInvalid"] = mediaInvalid,
                            ["mediaVerification"] = options.HircOnly ? "skipped" : "verified",
                            ["invalidExamples"] = invalidExamples,
                            ["languageNames"] = package.Languages.Values.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray(),
                            ["bnkPayloads"] = package.BnkStructures.Count,
                            ["bnkSections"] = package.BnkStructures.Sum(x => x.Sections.Count),
                            ["hircObjects"] = package.BnkStructures.Sum(x => checked((long)x.HircObjectCount)),
                            ["hircType02Prefix"] = new Dictionary<string, object?>
                            {
                                ["count"] = package.BnkStructures.Sum(x => (long)x.Type2PrefixCount),
                                ["prefixBytes"] = package.BnkStructures.Sum(x => (long)x.Type2PrefixBytes),
                                ["opaqueTailBytes"] = package.BnkStructures.Sum(x => (long)x.Type2OpaqueTailBytes),
                                ["minOpaqueTailBytes"] = package.BnkStructures.Where(x => x.Type2PrefixCount > 0).Select(x => x.Type2MinOpaqueTailBytes).DefaultIfEmpty(0u).Min(),
                                ["maxOpaqueTailBytes"] = package.BnkStructures.Select(x => x.Type2MaxOpaqueTailBytes).DefaultIfEmpty(0u).Max(),
                                ["pluginTypeCounts"] = package.BnkStructures
                                    .SelectMany(x => x.Type2PluginTypeCounts)
                                    .GroupBy(x => x.Key)
                                    .OrderBy(x => x.Key)
                                        .ToDictionary(x => $"0x{x.Key:X}", x => x.Sum(y => (long)y.Value)),
                                ["pluginIdCounts"] = package.BnkStructures
                                    .SelectMany(x => x.Type2PluginIdCounts)
                                    .GroupBy(x => x.Key, StringComparer.Ordinal)
                                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                                        .ToDictionary(x => x.Key, x => x.Sum(y => (long)y.Value)),
                            },
                            ["hircType03ActionFrame"] = BuildType3ActionFrameSummary(package.BnkStructures),
                            ["hircType04U32VectorFrame"] = BuildType4U32VectorFrameSummary(package.BnkStructures),
                            ["hircType02BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type2Body),
                            ["hircType05BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type5Body),
                            ["hircType06BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type6Body),
                            ["hircReferenceCensus"] = BuildReferenceCensusSummary(package.BnkStructures),
                            ["hircNamedReachCensus"] = BuildNamedReachSummary(package.BnkStructures),
                            ["hircType07BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type7Body),
                            ["hircType14BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type14Body),
                            ["hircType08BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type08Body),
                            ["hircType11BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type11Body),
                            ["hircType12BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type12Body),
                            ["hircType08Tail"] = BuildType08TailSummary(package.BnkStructures),
                            ["hircType12Tail"] = BuildType08TailSummary(package.BnkStructures, x => x.Type12Tail),
                            ["hircType08TailWords"] = new Dictionary<string, object?>
                            {
                                ["heads"] = package.Type08TailWords.Heads,
                                ["packagePopulation"] = package.Type08TailWords.PackagePopulation,
                                ["firstWordSameBank"] = package.Type08TailWords.FirstWordSameBank,
                                ["firstWordOtherBankInPackage"] = package.Type08TailWords.FirstWordOtherBankInPackage,
                                ["firstWordOutsidePackage"] = package.Type08TailWords.FirstWordOutsidePackage,
                                ["secondWordResolves"] = package.Type08TailWords.SecondWordResolves,
                                ["firstWordTargetTypeCounts"] = package.Type08TailWords.FirstWordTargetTypeCounts.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["secondWordTargetTypeCounts"] = package.Type08TailWords.SecondWordTargetTypeCounts.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircType11EntryHeaders"] = new Dictionary<string, object?>
                            {
                                ["entries"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.Entries),
                                ["rangeTested"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.RangeTested),
                                ["rangeIsSymmetric"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.RangeIsSymmetric),
                                ["rangeIsOrdered"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.RangeIsOrdered),
                                ["rangeControlTested"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.RangeControlTested),
                                ["rangeControlIsSymmetric"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.RangeControlIsSymmetric),
                                ["rangeControlIsOrdered"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.RangeControlIsOrdered),
                                ["fractionsTested"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.FractionsTested),
                                ["fractionsAreSmall"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.FractionsAreSmall),
                                ["fractionControlsTested"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.FractionControlsTested),
                                ["fractionControlsAreSmall"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.FractionControlsAreSmall),
                                ["elementCountValues"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11EntryHeaders.ElementCountValues)),
                                ["entryCountValues"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11EntryHeaders.EntryCountValues)),
                                ["curveRecords"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.CurveRecords),
                                ["curveCodesInRange"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.CurveCodesInRange),
                                ["curveControlsTested"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.CurveControlsTested),
                                ["curveControlsInRange"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.CurveControlsInRange),
                                ["curveCodes"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11EntryHeaders.CurveCodes)),
                                ["boundedFloatsTested"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.BoundedFloatsTested),
                                ["boundedFloatsInBand"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.BoundedFloatsInBand),
                                ["floatControlsTested"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.FloatControlsTested),
                                ["floatControlsInBand"] = package.BnkStructures.Sum(b => (long)b.Type11EntryHeaders.FloatControlsInBand),
                            },
                            ["hircType11Elements"] = new Dictionary<string, object?>
                            {
                                ["bodies"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.Bodies),
                                ["notASingleEntry"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.NotASingleEntry),
                                ["notASingleElement"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.NotASingleElement),
                                ["elements"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.Elements),
                                ["trailerIsAmbiguous"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.TrailerIsAmbiguous),
                                ["bodyIsNotWholeRecords"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.BodyIsNotWholeRecords),
                                ["framed"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.Framed),
                                ["elementsWithRecords"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.ElementsWithRecords),
                                ["countFieldAgrees"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.CountFieldAgrees),
                                ["elementsWithRuns"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.ElementsWithRuns),
                                ["elementFrames"] = package.BnkStructures.Sum(b => (long)b.Type11Elements.ElementFrames),
                                ["frameCloses"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.FrameCloses)),
                                ["frameClosesWithRuns"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.FrameClosesWithRuns)),
                                ["runsPerElement"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.RunsPerElement)),
                                ["recordsPerFramedElement"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.RecordsPerFramedElement)),
                                ["trailerForm"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.TrailerForm)),
                                ["recordsPerElement"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.RecordsPerElement)),
                                ["anchorSelectsOneTrailer"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.AnchorSelectsOneTrailer)),
                                ["anchorLeavesWholeRecords"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type11Elements.AnchorLeavesWholeRecords)),
                            },
                            ["hircParentField"] = new Dictionary<string, object?>
                            {
                                ["checkable"] = package.BnkStructures.Sum(b => (long)b.ParentField.Checkable),
                                ["parentNamesTheChildBack"] = package.BnkStructures.Sum(b => (long)b.ParentField.ParentNamesTheChildBack),
                                ["parentDoesNotNameTheChild"] = package.BnkStructures.Sum(b => (long)b.ParentField.ParentDoesNotNameTheChild),
                                ["namesSomethingOutsideTheBank"] = package.BnkStructures.Sum(b => (long)b.ParentField.NamesSomethingOutsideTheBank),
                                ["parentDeclaresNoChildren"] = package.BnkStructures.Sum(b => (long)b.ParentField.ParentDeclaresNoChildren),
                                ["edgeTypes"] = MergeCensus(package.BnkStructures.SelectMany(b => b.ParentField.EdgeTypes)),
                                ["disagreementTypes"] = MergeCensus(package.BnkStructures.SelectMany(b => b.ParentField.DisagreementTypes)),
                            },
                            ["hircType0CArray"] = new Dictionary<string, object?>
                            {
                                ["bodies"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.Bodies),
                                ["tooShort"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.TooShort),
                                ["selectorOutOfRange"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.SelectorOutOfRange),
                                ["countPastTheEnd"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.CountPastTheEnd),
                                ["countOutOfRange"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.CountOutOfRange),
                                ["arrayPastTheEnd"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.ArrayPastTheEnd),
                                ["arraysTested"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.ArraysTested),
                                ["arraysFullyResolving"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.ArraysFullyResolving),
                                ["rivalArraysTested"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.RivalArraysTested),
                                ["rivalArraysFullyResolving"] = package.BnkStructures.Sum(b => (long)b.Type0CArray.RivalArraysFullyResolving),
                                ["selectorValues"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type0CArray.SelectorValues)),
                                ["arrayLengths"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type0CArray.ArrayLengths)),
                                ["targetTypes"] = MergeCensus(package.BnkStructures.SelectMany(b => b.Type0CArray.TargetTypes)),
                            },
                            ["hircType0CHierarchy"] = new Dictionary<string, object?>
                            {
                                ["banks"] = package.Type0CHierarchy.Banks,
                                ["objects"] = package.Type0CHierarchy.Objects,
                                ["objectsNamingAParent"] = package.Type0CHierarchy.ObjectsNamingAParent,
                                ["rootsWithNoParent"] = package.Type0CHierarchy.RootsWithNoParent,
                                ["parentsOutsideTheBank"] = package.Type0CHierarchy.ParentsOutsideTheBank,
                                ["cycles"] = package.Type0CHierarchy.Cycles,
                                ["parentsWithSeveralChildren"] = package.Type0CHierarchy.ParentsWithSeveralChildren,
                                ["depths"] = package.Type0CHierarchy.Depths.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["childrenPerParent"] = package.Type0CHierarchy.ChildrenPerParent.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["edgeTypes"] = package.Type0CHierarchy.EdgeTypes.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircMusicMutuality"] = new Dictionary<string, object?>
                            {
                                ["sameBankEdges"] = package.MusicMutuality.SameBankEdges,
                                ["edgesIntoUnscannedObjects"] = package.MusicMutuality.EdgesIntoUnscannedObjects,
                                ["edgesBetweenScannedObjects"] = package.MusicMutuality.EdgesBetweenScannedObjects,
                                ["mutualEdges"] = package.MusicMutuality.MutualEdges,
                                ["mutualEdgeKinds"] = package.MusicMutuality.MutualEdgeKinds.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircHierarchy"] = new Dictionary<string, object?>
                            {
                                ["banks"] = package.Hierarchy.Banks,
                                ["objects"] = package.Hierarchy.Objects,
                                ["cycles"] = package.Hierarchy.Cycles,
                                ["rootsWithNoParent"] = package.Hierarchy.RootsWithNoParent,
                                ["rootsNamingOutsideTheBank"] = package.Hierarchy.RootsNamingOutsideTheBank,
                                ["rootsPerBank"] = package.Hierarchy.RootsPerBank.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["rootTypes"] = package.Hierarchy.RootTypes.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["outsideBankTypes"] = package.Hierarchy.OutsideBankTypes.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["internalTypes"] = package.Hierarchy.InternalTypes.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["leafTypes"] = package.Hierarchy.LeafTypes.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["depths"] = package.Hierarchy.Depths.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["parentsWithSeveralChildren"] = package.Hierarchy.ParentsWithSeveralChildren,
                                ["childrenPerParent"] = package.Hierarchy.ChildrenPerParent.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircSharedConstants"] = new Dictionary<string, object?>
                            {
                                ["bodies"] = package.SharedConstants.Bodies,
                                ["chosen"] = package.SharedConstants.Chosen.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["bodiesExercising"] = package.SharedConstants.BodiesExercising.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["zeroTrailerByCandidate"] = package.SharedConstants.ZeroTrailerByCandidate.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["closesByCandidate"] = package.SharedConstants.ClosesByCandidate.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircType0ACountedArray"] = new Dictionary<string, object?>
                            {
                                ["bodies"] = package.Type0ACountedArray.Bodies,
                                ["bodiesWithNoReference"] = package.Type0ACountedArray.BodiesWithNoReference,
                                ["noRoomForACount"] = package.Type0ACountedArray.NoRoomForACount,
                                ["checkable"] = package.Type0ACountedArray.Checkable,
                                ["countMatchesTheRun"] = package.Type0ACountedArray.CountMatchesTheRun,
                                ["countDoesNotMatch"] = package.Type0ACountedArray.CountDoesNotMatch,
                                ["runLengths"] = package.Type0ACountedArray.RunLengths.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircType0AEndAnchor"] = new Dictionary<string, object?>
                            {
                                ["bodies"] = package.Type0AEndAnchor.Bodies,
                                ["anchorNamesTheTargetType"] = package.Type0AEndAnchor.AnchorNamesTheTargetType,
                                ["controlsNameTheTargetType"] = package.Type0AEndAnchor.ControlsNameTheTargetType,
                                ["anchorHits"] = package.Type0AEndAnchor.AnchorHits.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["controlHits"] = package.Type0AEndAnchor.ControlHits.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircType0AHead"] = new Dictionary<string, object?>
                            {
                                ["bodies"] = package.Type0AHead.Bodies,
                                ["bodiesWhereTheRuleApplies"] = package.Type0AHead.BodiesWhereTheRuleApplies,
                                ["namesTheSourceType"] = package.Type0AHead.NamesTheSourceType.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["namesTheSourceTypeWhereTheRuleApplies"] = package.Type0AHead.NamesTheSourceTypeWhereTheRuleApplies.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["headWordTargets"] = package.Type0AHead.HeadWordTargets.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["tailBytesByOutcome"] = package.Type0AHead.TailBytesByOutcome.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["tailFloats"] = package.Type0AHead.TailFloats,
                                ["tailFloatsInBand"] = package.Type0AHead.TailFloatsInBand,
                                ["tailFloatsWhole"] = package.Type0AHead.TailFloatsWhole,
                                ["neighbourFloats"] = package.Type0AHead.NeighbourFloats,
                                ["neighbourFloatsWhole"] = package.Type0AHead.NeighbourFloatsWhole,
                                ["fractionCandidates"] = package.Type0AHead.FractionCandidates,
                                ["fractionsWithASmallDenominator"] = package.Type0AHead.FractionsWithASmallDenominator,
                                ["fractionControls"] = package.Type0AHead.FractionControls,
                                ["fractionControlsWithASmallDenominator"] = package.Type0AHead.FractionControlsWithASmallDenominator,
                                ["decibelBodies"] = package.Type0AHead.DecibelBodies,
                                ["decibelsInRange"] = package.Type0AHead.DecibelsInRange,
                                ["decibelsWhole"] = package.Type0AHead.DecibelsWhole,
                                ["decibelControlsInRange"] = package.Type0AHead.DecibelControlsInRange,
                                ["wordFiveNonZero"] = package.Type0AHead.WordFiveNonZero,
                                ["wordFiveInPackage"] = package.Type0AHead.WordFiveInPackage,
                                ["wordFiveValues"] = package.Type0AHead.WordFiveValues.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["elementTotal"] = (long)package.Type0AElements.Total,
                                ["elementLeadingByteNotZero"] = (long)package.Type0AElements.LeadingByteNotZero,
                                ["elementPadNotZero"] = (long)package.Type0AElements.PadNotZero,
                                ["elementValueCounts"] = package.Type0AElements.ValueCounts.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => (long)z.Value),
                            },
                            ["hircMusicReferences"] = new Dictionary<string, object?>
                            {
                                ["bodies"] = package.MusicReferences.Bodies,
                                ["packagePopulation"] = package.MusicReferences.PackagePopulation,
                                ["wordsOffered"] = package.MusicReferences.WordsOffered,
                                ["references"] = package.MusicReferences.References,
                                ["bodiesWithNoReference"] = package.MusicReferences.BodiesWithNoReference,
                                ["referencesPerBody"] = package.MusicReferences.ReferencesPerBody.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["edgeCounts"] = package.MusicReferences.EdgeCounts.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["distinctTargets"] = package.MusicReferences.DistinctTargets.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["targetsReachedTwice"] = package.MusicReferences.TargetsReachedTwice.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["targetPopulation"] = package.MusicReferences.TargetPopulation.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["edgeDistanceFromEnd"] = package.MusicReferences.EdgeDistanceFromEnd.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircType12TailWords"] = new Dictionary<string, object?>
                            {
                                ["heads"] = package.Type12TailWords.Heads,
                                ["packagePopulation"] = package.Type12TailWords.PackagePopulation,
                                ["firstWordSameBank"] = package.Type12TailWords.FirstWordSameBank,
                                ["firstWordOtherBankInPackage"] = package.Type12TailWords.FirstWordOtherBankInPackage,
                                ["firstWordOutsidePackage"] = package.Type12TailWords.FirstWordOutsidePackage,
                                ["secondWordResolves"] = package.Type12TailWords.SecondWordResolves,
                                ["firstWordTargetTypeCounts"] = package.Type12TailWords.FirstWordTargetTypeCounts.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["secondWordTargetTypeCounts"] = package.Type12TailWords.SecondWordTargetTypeCounts.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircType22BodyFrame"] = BuildBodyFrameSummary(package.BnkStructures, x => x.Type22Body),
                            ["hircMusicHeadReferences"] = BuildMusicHeadSummary(package.BnkStructures),
                            ["hircType11Sources"] = BuildType11SourceSummary(package.BnkStructures),
                            ["hircType08Head"] = BuildType08HeadSummary(package.BnkStructures),
                            ["hircType17"] = BuildType17Summary(package.BnkStructures),
                            ["hircType09"] = BuildType09Summary(package.BnkStructures),
                            ["hircSmallTypes"] = BuildSmallTypeSummary(package.BnkStructures),
                            ["hircType03Targets"] = new Dictionary<string, object?>
                            {
                                ["objects"] = package.Type03Targets.Objects,
                                ["zero"] = package.Type03Targets.Zero,
                                ["sameBank"] = package.Type03Targets.SameBank,
                                ["otherBankInPackage"] = package.Type03Targets.OtherBankInPackage,
                                ["outsidePackage"] = package.Type03Targets.OutsidePackage,
                                ["sameBankByActionByte"] = package.Type03Targets.SameBankByActionByte.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["otherBankByActionByte"] = package.Type03Targets.OtherBankByActionByte.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                                ["outsideByActionByte"] = package.Type03Targets.OutsideByActionByte.OrderBy(z => z.Key, StringComparer.Ordinal).ToDictionary(z => z.Key, z => z.Value),
                            },
                            ["hircMediaJoin"] = new Dictionary<string, object?>
                            {
                                ["mediaEntries"] = package.MediaJoin.MediaEntries,
                                ["mediaIds"] = package.MediaJoin.MediaIds.ToArray(),
                                ["sourceIdsByPlugin"] = package.MediaJoin.SourceIdsByPlugin
                                    .OrderBy(z => z.Key, StringComparer.Ordinal)
                                    .ToDictionary(z => z.Key, z => z.Value.ToArray()),
                            },
                            ["hircObjectTypeCounts"] = package.BnkStructures
                                .SelectMany(x => x.HircObjectTypeCounts)
                                .GroupBy(x => x.Key)
                                .OrderBy(x => x.Key)
                                .ToDictionary(x => $"0x{x.Key:X2}", x => x.Sum(y => (long)y.Value)),
                            ["hircObjectTypeStats"] = package.BnkStructures
                                .SelectMany(x => x.HircObjectTypeStats)
                                .GroupBy(x => x.Key)
                                .OrderBy(x => x.Key)
                                .ToDictionary(
                                    x => $"0x{x.Key:X2}",
                                    x => new Dictionary<string, object?>
                                    {
                                        ["count"] = x.Sum(y => (long)y.Value.Count),
                                        ["declaredLengthBytes"] = x.Sum(y => (long)y.Value.DeclaredLengthBytes),
                                        ["minDeclaredLength"] = x.Min(y => y.Value.MinDeclaredLength),
                                        ["maxDeclaredLength"] = x.Max(y => y.Value.MaxDeclaredLength),
                                    }),
                            ["bnkSectionTagCounts"] = package.BnkStructures
                                .SelectMany(x => x.Sections)
                                .GroupBy(x => x.Tag, StringComparer.Ordinal)
                                .OrderBy(x => x.Key, StringComparer.Ordinal)
                                .ToDictionary(x => x.Key, x => x.LongCount(), StringComparer.Ordinal),
                            ["bnkStructures"] = package.BnkStructures.Select(x => new Dictionary<string, object?>
                            {
                                ["bankId"] = x.BankId,
                                ["byteLength"] = x.ByteLength,
                                ["version"] = x.Version,
                                ["sections"] = x.Sections.Select(section => new Dictionary<string, object?>
                                {
                                    ["tag"] = section.Tag,
                                    ["offset"] = section.Offset,
                                    ["declaredSize"] = section.DeclaredSize,
                                }).ToArray(),
                                ["hircObjectCount"] = x.HircObjectCount,
                                ["hircType02Prefix"] = new Dictionary<string, object?>
                                {
                                    ["count"] = x.Type2PrefixCount,
                                    ["prefixBytes"] = x.Type2PrefixBytes,
                                    ["opaqueTailBytes"] = x.Type2OpaqueTailBytes,
                                    ["minOpaqueTailBytes"] = x.Type2MinOpaqueTailBytes,
                                    ["maxOpaqueTailBytes"] = x.Type2MaxOpaqueTailBytes,
                                    ["pluginTypeCounts"] = x.Type2PluginTypeCounts
                                        .OrderBy(pair => pair.Key)
                                        .ToDictionary(pair => $"0x{pair.Key:X}", pair => pair.Value),
                                    ["pluginIdCounts"] = x.Type2PluginIdCounts
                                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                        .ToDictionary(pair => pair.Key, pair => pair.Value),
                                },
                                ["hircType03ActionFrame"] = BuildType3ActionFrameSummary(new[] { x }),
                                ["hircType04U32VectorFrame"] = BuildType4U32VectorFrameSummary(new[] { x }),
                                ["hircType02BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type2Body),
                                ["hircType05BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type5Body),
                                ["hircType06BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type6Body),
                                ["hircReferenceCensus"] = BuildReferenceCensusSummary(new[] { x }),
                                ["hircType07BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type7Body),
                                ["hircType14BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type14Body),
                                ["hircType08BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type08Body),
                                ["hircType12BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type12Body),
                                ["hircType08Tail"] = BuildType08TailSummary(new[] { x }),
                                ["hircType22BodyFrame"] = BuildBodyFrameSummary(new[] { x }, y => y.Type22Body),
                                ["hircMusicHeadReferences"] = BuildMusicHeadSummary(new[] { x }),
                                ["hircType11Sources"] = BuildType11SourceSummary(new[] { x }),
                                ["hircType08Head"] = BuildType08HeadSummary(new[] { x }),
                                ["hircType17"] = BuildType17Summary(new[] { x }),
                                ["hircType09"] = BuildType09Summary(new[] { x }),
                                ["hircSmallTypes"] = BuildSmallTypeSummary(new[] { x }),
                                ["hircObjectTypeStats"] = x.HircObjectTypeStats
                                    .OrderBy(pair => pair.Key)
                                    .ToDictionary(
                                        pair => $"0x{pair.Key:X2}",
                                        pair => new Dictionary<string, object?>
                                        {
                                            ["count"] = pair.Value.Count,
                                            ["declaredLengthBytes"] = pair.Value.DeclaredLengthBytes,
                                            ["minDeclaredLength"] = pair.Value.MinDeclaredLength,
                                            ["maxDeclaredLength"] = pair.Value.MaxDeclaredLength,
                                        }),
                            }).ToArray(),
                        };
                        if (mediaInvalid != 0)
                        {
                            var firstInvalidId = invalidExamples.Count == 0 ? "unknown" : invalidExamples[0].GetValueOrDefault("id")?.ToString();
                            throw new InvalidDataException($"AKPK media entries without RIFF/RIFX/PLUG: count={mediaInvalid}; first={firstInvalidId}");
                        }
                        row["status"] = "verified";
                    }
                    catch (Exception e)
                    {
                        row["diagnostic"] = BoundDiagnostic(e.Message);
                        failures++;
                    }
                    rows.Add(row);
                }
            }

            var report = new Dictionary<string, object?>
            {
                ["schemaVersion"] = "akpk-structure-audit-v1",
                ["streamingAssets"] = options.StreamingAssets,
                ["fallbackAssets"] = options.FallbackAssets,
                ["conditionalExclusions"] = "any audio block is ignored only when every declared chunk is absent from both roots",
                ["blocks"] = options.BlockTypes.Select(x => x.GetName()).ToArray(),
                ["rows"] = rows,
                ["summary"] = new Dictionary<string, object?>
                {
                    ["packages"] = rows.Count(x => x.TryGetValue("path", out _)),
                    ["verified"] = rows.Count(x => Equals(x.GetValueOrDefault("status"), "verified")),
                    ["failures"] = failures,
                    ["missingBlocks"] = rows.Count(x => Equals(x.GetValueOrDefault("status"), "missing_block")),
                    ["excluded"] = rows.Count(x =>
                        x.GetValueOrDefault("status") is string status
                        && status.StartsWith("excluded_", StringComparison.Ordinal)),
                },
            };
            var outputParent = Path.GetDirectoryName(Path.GetFullPath(options.Output));
            if (!string.IsNullOrEmpty(outputParent))
            {
                Directory.CreateDirectory(outputParent);
            }
            File.WriteAllText(options.Output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            var packageCount = rows.Count(x => x.TryGetValue("path", out _));
            var verifiedCount = rows.Count(x => Equals(x.GetValueOrDefault("status"), "verified"));
            Console.WriteLine($"AKPK audit: {packageCount} packages, {verifiedCount} verified, {failures} failures");
            return failures == 0 ? 0 : 1;
        }

        public static void Run(string[] args)
        {
            var options = ParseOptions(args);
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);

            Console.WriteLine("Loading AudioDialog.json...");
            var audioDialog = LoadAudioDialog(loader);
            var converter = options.Format != AudioOutputFormat.Wem
                ? EndfieldVgmstreamConverter.CreateDefault()
                : null;

            var totalSuccess = 0;
            var totalErrors = 0;
            var totalUnmapped = 0;
            var totalPluginMedia = 0;
            var totalDuplicatePathUnavailablePackages = 0;
            var totalPackageErrors = 0;

            foreach (var language in options.Languages)
            {
                var audioMap = EndfieldAudioMap.FromAudioDialog(audioDialog, language);
                var processedPckNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"  Found {audioMap.Count} {language.Name()} audio entries");

                foreach (var blockType in options.BlockTypes(language))
                {
                    Console.WriteLine($"Extracting {language.Name()} audio files from {blockType.GetName()}...");
                    List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> pckFiles;
                    try
                    {
                        pckFiles = ExtractPckFiles(loader, blockType);
                    }
                    catch (EndfieldVfsException)
                    {
                        Console.WriteLine($"  Skip: No PCK files found in {blockType.GetName()}");
                        continue;
                    }

                    if (pckFiles.Count == 0)
                    {
                        Console.WriteLine("  Skip: No PCK files found");
                        continue;
                    }

                    Console.WriteLine($"  Found {pckFiles.Count} PCK files");
                    foreach (var (chunk, file) in pckFiles)
                    {
                        var pckName = file.FileName;
                        Console.WriteLine($"  Processing {pckName}");
                        EndfieldAkpkPackage package;
                        try
                        {
                            // Extract one PCK at a time instead of retaining every
                            // package in the block during the full conversion pass.
                            var pckData = loader.ExtractFileToBytes(blockType, chunk, file);
                            package = EndfieldAkpkPackage.Parse(pckData);
                        }
                        catch (EndfieldVfsChunkNotFoundException e) when (processedPckNames.Contains(pckName))
                        {
                            Console.WriteLine(
                                $"    Skip: {e.Message}; the same logical PCK path was already processed from an earlier block"
                            );
                            totalDuplicatePathUnavailablePackages++;
                            continue;
                        }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine($"    Error: Failed to parse {pckName}: {e.Message}");
                            totalPackageErrors++;
                            continue;
                        }
                        processedPckNames.Add(pckName);

                        var successCount = 0;
                        var errorCount = 0;
                        var unmappedCount = 0;
                        var pluginMediaCount = 0;
                        var entries = package.Entries.ToArray();

                        Parallel.ForEach(entries, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = options.Jobs,
                        }, entry =>
                        {
                            try
                            {
                                var wemData = package.GetWemData(entry);
                                if (HasMagic(wemData, "PLUG"))
                                {
                                    Interlocked.Increment(ref pluginMediaCount);
                                    return;
                                }
                                if (wemData.Length < 4 || (!HasMagic(wemData, "RIFF") && !HasMagic(wemData, "RIFX")))
                                {
                                    Console.Error.WriteLine(
                                        $"    Error: Unsupported media entry {entry.Id:x} in {pckName}: " +
                                        $"expected RIFF/RIFX, got {MagicPreview(wemData)} ({wemData.Length} bytes)"
                                    );
                                    Interlocked.Increment(ref errorCount);
                                    return;
                                }

                                var hash = entry.Id.ToString("x");
                                var outputRoot = options.OutputForBlock(blockType);
                                string outputPath;
                                var mappedPath = audioMap.GetPath(hash);
                                if (!string.IsNullOrEmpty(mappedPath))
                                {
                                    outputPath = options.Format == AudioOutputFormat.Wem
                                        ? Path.Combine(outputRoot, mappedPath)
                                        : Path.Combine(
                                            outputRoot,
                                            mappedPath.Replace(
                                                ".wem",
                                                $".{options.Format.Extension()}",
                                                StringComparison.Ordinal
                                            )
                                        );
                                }
                                else
                                {
                                    Interlocked.Increment(ref unmappedCount);
                                    // The language is already encoded in the output root
                                    // (Audio/<LANG> or Audio/shared); unmapped media are
                                    // grouped by their source bank instead of a redundant
                                    // language subfolder. A Wwise event-category subfolder
                                    // is added later by the Python indexer where resolvable.
                                    outputPath = Path.Combine(
                                        outputRoot,
                                        "unmapped",
                                        UnmappedBankFolder(pckName),
                                        $"{entry.Id}.{options.Format.Extension()}"
                                    );
                                }

                                WriteAudioFile(wemData, outputPath, options.Format, converter);
                                Interlocked.Increment(ref successCount);
                            }
                            catch (Exception e)
                            {
                                Console.Error.WriteLine($"    Error: Failed to extract/write media {entry.Id:x}: {e.Message}");
                                Interlocked.Increment(ref errorCount);
                            }
                        });

                        totalSuccess += successCount;
                        totalErrors += errorCount;
                        totalUnmapped += unmappedCount;
                        totalPluginMedia += pluginMediaCount;
                        Console.WriteLine(
                            $"    Done: Extracted {successCount}/{entries.Length} audio entries" +
                            (pluginMediaCount > 0
                                ? $" ({pluginMediaCount} Wwise FX plugin-media entries skipped)"
                                : string.Empty)
                        );
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                $"Complete: Extracted {totalSuccess} files ({totalUnmapped} unmapped, " +
                $"{totalPluginMedia} Wwise FX plugin-media entries skipped, " +
                $"{totalDuplicatePathUnavailablePackages} duplicate-path packages unavailable, " +
                $"{totalPackageErrors + totalErrors} errors)"
            );
        }

        public static void PrintHelp()
        {
            Console.WriteLine("Usage: AnimeStudio.CLI audio -s <StreamingAssets> [-o <output>] [--shared-output <output>] [-l <language>] [-f <flac|wav|wem>] [-b <block>] [-j <jobs>] [--fallback-assets <StreamingAssets>]");
        }

        private static JToken LoadAudioDialog(EndfieldVfsLoader loader)
        {
            var merged = new JObject();
            var layerLoaders = new List<EndfieldVfsLoader> { loader };
            if (!string.IsNullOrEmpty(loader.FallbackAssetsPath)
                && !string.Equals(loader.StreamingAssetsPath, loader.FallbackAssetsPath, StringComparison.OrdinalIgnoreCase))
            {
                // Keep the primary loader's fallback resolution: some primary
                // Table metadata references a chunk that is present only in
                // the fallback VFS.  The second loader reads the fallback
                // table itself so its Persistent rows are also merged.
                layerLoaders.Add(new EndfieldVfsLoader(loader.FallbackAssetsPath));
            }

            foreach (var layerLoader in layerLoaders)
            {
                try
                {
                    var layer = LoadAudioDialogLayer(layerLoader);
                    foreach (var property in layer.Properties())
                    {
                        // Persistent is the overlay layer, so rows in it win
                        // when the same authored id exists in both roots.
                        merged[property.Name] = property.Value.DeepClone();
                    }
                }
                catch (EndfieldVfsBlockNotFoundException)
                {
                    // A language/install root may legitimately omit the Table
                    // block; keep loading the other root if it has one.
                }
            }

            if (merged.Count > 0)
            {
                return merged;
            }

            throw new EndfieldVfsException("AudioDialog.bytes not found in Table block");
        }

        private static string BoundDiagnostic(string message) =>
            string.IsNullOrEmpty(message) ? "unknown AKPK failure" : message.Length <= 240 ? message : message[..240];

        private static Dictionary<string, object?> BuildType3ActionFrameSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var rows = structures.ToArray();
            var failureExamples = rows
                .SelectMany(row => row.Type3ActionFailureExamples)
                .Take(16)
                .Select(failure => new Dictionary<string, object?>
                {
                    ["bankId"] = failure.BankId,
                    ["ordinal"] = failure.Ordinal,
                    ["objectId"] = failure.ObjectId,
                    ["operationCode"] = failure.OperationCode is ushort operationCode
                        ? $"0x{operationCode:X4}"
                        : null,
                    ["status"] = failure.Status,
                    ["failureCategory"] = failure.FailureCategory,
                    ["cursorOffset"] = failure.CursorOffset,
                    ["expectedBytes"] = failure.ExpectedBytes,
                    ["actualBytes"] = failure.ActualBytes,
                })
                .ToArray();
            return new Dictionary<string, object?>
            {
                ["count"] = rows.Sum(row => (long)row.Type3ActionFrameCount),
                ["exact"] = rows.Sum(row => (long)row.Type3ActionExactCount),
                ["unsupported"] = rows.Sum(row => (long)row.Type3ActionUnsupportedCount),
                ["failed"] = rows.Sum(row => (long)row.Type3ActionFailedCount),
                ["ambiguous"] = 0,
                ["bodyBytes"] = rows.Sum(row => (long)row.Type3ActionBodyBytes),
                ["exactCursorBytes"] = rows.Sum(row => (long)row.Type3ActionExactCursorBytes),
                ["operationCounts"] = rows
                    .SelectMany(row => row.Type3ActionOperationCounts)
                    .GroupBy(pair => pair.Key)
                    .OrderBy(group => group.Key)
                    .ToDictionary(
                        group => $"0x{group.Key:X4}",
                        group => group.Sum(pair => (long)pair.Value)),
                ["failureCategories"] = rows
                    .SelectMany(row => row.Type3ActionFailureCounts)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(pair => (long)pair.Value),
                        StringComparer.Ordinal),
                ["nonExactExamples"] = failureExamples,
            };
        }

        private static Dictionary<string, object?> BuildNamedReachSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var rows = structures.Select(row => row.NamedReachCensus).ToArray();
            return new Dictionary<string, object?>
            {
                ["matchedObjects"] = rows.Sum(row => (long)row.MatchedObjects),
                ["matchedNamedType"] = rows.Sum(row => (long)row.MatchedNamedType),
                ["reachingASource"] = rows.Sum(row => (long)row.ReachingASource),
                ["reachingNoSource"] = rows.Sum(row => (long)row.ReachingNoSource),
                ["reachedSourceIds"] = rows.Sum(row => (long)row.ReachedSourceIds),
                ["walkEdgesLeavingThePackage"] = rows.Sum(row => (long)row.WalkEdgesLeavingThePackage),
                ["matchesByObjectType"] = rows
                    .SelectMany(row => row.MatchesByObjectType)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(pair => (long)pair.Value),
                        StringComparer.Ordinal),
                ["reachedSourceIdsByIdentity"] = rows
                    .SelectMany(row => row.ReachedSourceIdsByIdentity)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Max(pair => (long)pair.Value),
                        StringComparer.Ordinal),
                ["banksMatched"] = rows.Sum(row => (long)row.BanksMatched),
                ["banksSeen"] = rows.Sum(row => (long)row.BanksSeen),
                ["mediaMatched"] = rows.Sum(row => (long)row.MediaMatched),
                ["mediaSeen"] = rows.Sum(row => (long)row.MediaSeen),
                ["reachedSourceIdListByIdentity"] = rows
                    .SelectMany(row => row.ReachedSourceIdListByIdentity)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.SelectMany(pair => pair.Value).Distinct().OrderBy(id => id).ToArray(),
                        StringComparer.Ordinal),
            };
        }

        private static Dictionary<string, object?> BuildReferenceCensusSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var rows = structures.Select(row => row.ReferenceCensus).ToArray();
            return new Dictionary<string, object?>
            {
                ["references"] = rows.Sum(row => (long)row.References),
                ["resolvedSameBank"] = rows.Sum(row => (long)row.ResolvedSameBank),
                ["unresolvedInBank"] = rows.Sum(row => (long)row.UnresolvedInBank),
                ["selfReferences"] = rows.Sum(row => (long)row.SelfReferences),
                ["targetsWithMultipleReferrers"] = rows.Sum(row => (long)row.TargetsWithMultipleReferrers),
                ["duplicateObjectIds"] = rows.Sum(row => (long)row.DuplicateObjectIds),
                ["referencesToDuplicateIds"] = rows.Sum(row => (long)row.ReferencesToDuplicateIds),
                ["candidateWords"] = rows.Sum(row => (long)row.CandidateWords),
                ["candidateWordsMatchingAnObject"] = rows.Sum(row => (long)row.CandidateWordsMatchingAnObject),
                ["referenceCycleOrFeedingNodes"] = rows.Sum(row => (long)row.ReferenceCycleOrFeedingNodes),
                ["distinctDuplicateObjectIds"] = rows.Sum(row => (long)row.DistinctDuplicateObjectIds),
                ["objectCountsByType"] = rows
                    .SelectMany(row => row.ObjectCountsByType)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(pair => (long)pair.Value),
                        StringComparer.Ordinal),
                ["maximumReferenceDepth"] = rows.Select(row => row.MaximumReferenceDepth).DefaultIfEmpty(0u).Max(),
                ["edgeCounts"] = rows
                    .SelectMany(row => row.EdgeCounts)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(pair => (long)pair.Value),
                        StringComparer.Ordinal),
            };
        }







        private static Dictionary<string, object?> BuildSmallTypeSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var bodies = 0U; var exact = 0U; var failed = 0U;
            var exactBytes = 0U; var bodyBytes = 0U; var withSecond = 0U; var secondEntries = 0U;
            var byType = new Dictionary<string, uint>(StringComparer.Ordinal);
            var failures = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var structure in structures)
            {
                var census = structure.SmallTypes;
                bodies = checked(bodies + census.Bodies);
                exact = checked(exact + census.Exact);
                failed = checked(failed + census.Failed);
                exactBytes = checked(exactBytes + census.ExactBytes);
                bodyBytes = checked(bodyBytes + census.BodyBytes);
                withSecond = checked(withSecond + census.BodiesWithSecondBlock);
                secondEntries = checked(secondEntries + census.SecondBlockEntries);
                foreach (var pair in census.BodiesByType)
                {
                    byType.TryGetValue(pair.Key, out var existing);
                    byType[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.FailureCounts)
                {
                    failures.TryGetValue(pair.Key, out var existing);
                    failures[pair.Key] = checked(existing + pair.Value);
                }
            }
            return new Dictionary<string, object?>
            {
                ["bodies"] = bodies,
                ["exact"] = exact,
                ["failed"] = failed,
                ["exactBytes"] = exactBytes,
                ["bodyBytes"] = bodyBytes,
                ["bodiesWithSecondBlock"] = withSecond,
                ["secondBlockEntries"] = secondEntries,
                ["bodiesByType"] = byType.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["failureCounts"] = failures.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private static Dictionary<string, object?> BuildType09Summary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var bodies = 0U; var exact = 0U; var open = 0U; var failed = 0U;
            var exactBytes = 0U; var bodyBytes = 0U; var entries = 0U;
            var failures = new Dictionary<string, uint>(StringComparer.Ordinal);
            var flags = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var structure in structures)
            {
                var census = structure.Type09;
                bodies = checked(bodies + census.Bodies);
                exact = checked(exact + census.Exact);
                open = checked(open + census.UnestablishedSecondRun);
                failed = checked(failed + census.Failed);
                exactBytes = checked(exactBytes + census.ExactBytes);
                bodyBytes = checked(bodyBytes + census.BodyBytes);
                entries = checked(entries + census.RunEntries);
                foreach (var pair in census.FailureCounts)
                {
                    failures.TryGetValue(pair.Key, out var existing);
                    failures[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.TailFlagCounts)
                {
                    flags.TryGetValue(pair.Key, out var existing);
                    flags[pair.Key] = checked(existing + pair.Value);
                }
            }
            return new Dictionary<string, object?>
            {
                ["bodies"] = bodies,
                ["exact"] = exact,
                ["unestablishedSecondRun"] = open,
                ["failed"] = failed,
                ["exactBytes"] = exactBytes,
                ["bodyBytes"] = bodyBytes,
                ["runEntries"] = entries,
                ["failureCounts"] = failures.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["tailFlagCounts"] = flags.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private static Dictionary<string, object?> BuildType17Summary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var bodies = 0U; var exact = 0U; var fenced = 0U; var failed = 0U;
            var exactBytes = 0U; var bodyBytes = 0U; var run = 0U; var entries = 0U;
            var failures = new Dictionary<string, uint>(StringComparer.Ordinal);
            var reasons = new Dictionary<string, uint>(StringComparer.Ordinal);
            var byType = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var structure in structures)
            {
                var census = structure.Type17;
                bodies = checked(bodies + census.Bodies);
                exact = checked(exact + census.Exact);
                fenced = checked(fenced + census.Fenced);
                failed = checked(failed + census.Failed);
                exactBytes = checked(exactBytes + census.ExactBytes);
                bodyBytes = checked(bodyBytes + census.BodyBytes);
                run = checked(run + census.RunElements);
                entries = checked(entries + census.GroupIEntries);
                foreach (var pair in census.FailureCounts)
                {
                    failures.TryGetValue(pair.Key, out var existing);
                    failures[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.FenceReasons)
                {
                    reasons.TryGetValue(pair.Key, out var existing);
                    reasons[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.BodiesByType)
                {
                    byType.TryGetValue(pair.Key, out var existing);
                    byType[pair.Key] = checked(existing + pair.Value);
                }
            }
            return new Dictionary<string, object?>
            {
                ["bodies"] = bodies,
                ["exact"] = exact,
                ["fenced"] = fenced,
                ["fenceReasons"] = reasons.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["bodiesByType"] = byType.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["failed"] = failed,
                ["exactBytes"] = exactBytes,
                ["bodyBytes"] = bodyBytes,
                ["runElements"] = run,
                ["groupIEntries"] = entries,
                ["failureCounts"] = failures.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private static Dictionary<string, object?> BuildType08HeadSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var bodies = 0U; var resolved = 0U; var nulls = 0U; var unresolved = 0U; var tooShort = 0U;
            foreach (var structure in structures)
            {
                var census = structure.Type08Head;
                bodies = checked(bodies + census.Bodies);
                resolved = checked(resolved + census.Resolved);
                nulls = checked(nulls + census.Null);
                unresolved = checked(unresolved + census.Unresolved);
                tooShort = checked(tooShort + census.TooShort);
            }
            return new Dictionary<string, object?>
            {
                ["bodies"] = bodies,
                ["resolved"] = resolved,
                ["null"] = nulls,
                ["unresolved"] = unresolved,
                ["tooShort"] = tooShort,
            };
        }

        private static Dictionary<string, object?> BuildType08TailSummary(
            IEnumerable<EndfieldBnkStructure> structures,
            Func<EndfieldBnkStructure, EndfieldHircType08TailCensus>? select = null)
        {
            select ??= x => x.Type08Tail;
            var bodies = 0U; var notWalkable = 0U; var framed = 0U; var tails = 0U;
            var noZero = 0U; var noCount = 0U; var ambiguous = 0U; var unique = 0U;
            var records = 0U; var headBytes = 0U;
            var observedWidth = 0U; var otherWidth = 0U;
            var counts = new Dictionary<string, uint>(StringComparer.Ordinal);
            var codes = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var structure in structures)
            {
                var census = select(structure);
                bodies = checked(bodies + census.Bodies);
                notWalkable = checked(notWalkable + census.NotWalkable);
                framed = checked(framed + census.FramedByTheReader);
                tails = checked(tails + census.Tails);
                noZero = checked(noZero + census.NoZeroWordAtTheEnd);
                noCount = checked(noCount + census.NoCountBeforeTheRecords);
                ambiguous = checked(ambiguous + census.CountIsAmbiguous);
                unique = checked(unique + census.TailsWithAUniqueCount);
                records = checked(records + census.Records);
                headBytes = checked(headBytes + census.UnexplainedHeadBytes);
                observedWidth = checked(observedWidth + census.HeadsOfTheObservedWidth);
                otherWidth = checked(otherWidth + census.HeadIsNotTheObservedWidth);
                foreach (var pair in census.RecordCountCounts)
                {
                    counts.TryGetValue(pair.Key, out var existing);
                    counts[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.ThirdFieldCounts)
                {
                    codes.TryGetValue(pair.Key, out var existing);
                    codes[pair.Key] = checked(existing + pair.Value);
                }
            }
            return new Dictionary<string, object?>
            {
                ["bodies"] = bodies,
                ["notWalkable"] = notWalkable,
                ["framedByTheReader"] = framed,
                ["tails"] = tails,
                ["noZeroWordAtTheEnd"] = noZero,
                ["noCountBeforeTheRecords"] = noCount,
                ["countIsAmbiguous"] = ambiguous,
                ["tailsWithAUniqueCount"] = unique,
                ["records"] = records,
                ["unexplainedHeadBytes"] = headBytes,
                ["headsOfTheObservedWidth"] = observedWidth,
                ["headIsNotTheObservedWidth"] = otherWidth,
                ["recordCountCounts"] = counts.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["thirdFieldCounts"] = codes.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private static Dictionary<string, object?> BuildType11SourceSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var bodies = 0U; var withRecords = 0U; var records = 0U;
            var outOfRange = 0U; var tooShort = 0U; var endsWith = 0U;
            var terms = new Dictionary<string, uint>(StringComparer.Ordinal);
            var plugins = new Dictionary<string, uint>(StringComparer.Ordinal);
            var streams = new Dictionary<string, uint>(StringComparer.Ordinal);
            var counts = new Dictionary<string, uint>(StringComparer.Ordinal);
            var tailCounts = new Dictionary<string, uint>(StringComparer.Ordinal);
            var leadWords = new Dictionary<string, uint>(StringComparer.Ordinal);
            var withTail = 0U; var noTail = 0U; var tailOutOfRange = 0U;
            var inspected = 0U; var noRecords = 0U; var recordsFit = 0U;
            var countUnusable = 0U; var recordsPastEnd = 0U; var curveRecords = 0U;
            var interps = new Dictionary<string, uint>(StringComparer.Ordinal);
            var tailDeclared = 0U; var tailEchoed = 0U; var tailMatches = 0U;
            var tailExceeds = 0U; var firstNames = 0U; var firstShort = 0U;
            foreach (var structure in structures)
            {
                var census = structure.Type11Sources;
                withTail = checked(withTail + census.BodiesWithATail);
                inspected = checked(inspected + census.EntriesInspected);
                noRecords = checked(noRecords + census.EntriesWithNoRecords);
                recordsFit = checked(recordsFit + census.EntriesWhoseRecordsFit);
                countUnusable = checked(countUnusable + census.EntriesWhoseCountIsNotUsable);
                recordsPastEnd = checked(recordsPastEnd + census.EntriesWhoseRecordsRunPastTheEnd);
                curveRecords = checked(curveRecords + census.CurveRecords);
                foreach (var pair in census.InterpolationCounts)
                {
                    interps.TryGetValue(pair.Key, out var existing);
                    interps[pair.Key] = checked(existing + pair.Value);
                }
                noTail = checked(noTail + census.NoTailAfterTheRun);
                tailOutOfRange = checked(tailOutOfRange + census.TailCountOutOfRange);
                tailDeclared = checked(tailDeclared + census.TailEntriesDeclared);
                tailEchoed = checked(tailEchoed + census.TailEntriesEchoed);
                tailMatches = checked(tailMatches + census.TailEchoesMatchTheCount);
                tailExceeds = checked(tailExceeds + census.TailEchoesExceedTheCount);
                firstNames = checked(firstNames + census.FirstTailEntryNamesADeclaredSource);
                firstShort = checked(firstShort + census.FirstTailEntryTooShort);
                foreach (var pair in census.TailEntryCountCounts)
                {
                    tailCounts.TryGetValue(pair.Key, out var existing);
                    tailCounts[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.FirstTailEntryLeadingWordCounts)
                {
                    leadWords.TryGetValue(pair.Key, out var existing);
                    leadWords[pair.Key] = checked(existing + pair.Value);
                }
                bodies = checked(bodies + census.Bodies);
                withRecords = checked(withRecords + census.BodiesWithRecords);
                records = checked(records + census.Records);
                outOfRange = checked(outOfRange + census.RecordsOutOfRange);
                tooShort = checked(tooShort + census.TooShort);
                endsWith = checked(endsWith + census.EndsWithTerminator);
                foreach (var pair in census.TerminatorCounts)
                {
                    terms.TryGetValue(pair.Key, out var existing);
                    terms[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.PluginIdCounts)
                {
                    plugins.TryGetValue(pair.Key, out var existing);
                    plugins[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.StreamTypeCounts)
                {
                    streams.TryGetValue(pair.Key, out var existing);
                    streams[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in census.RecordCountCounts)
                {
                    counts.TryGetValue(pair.Key, out var existing);
                    counts[pair.Key] = checked(existing + pair.Value);
                }
            }
            return new Dictionary<string, object?>
            {
                ["bodies"] = bodies,
                ["bodiesWithRecords"] = withRecords,
                ["records"] = records,
                ["recordsOutOfRange"] = outOfRange,
                ["tooShort"] = tooShort,
                ["endsWithTerminator"] = endsWith,
                ["terminatorCounts"] = terms.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["pluginIdCounts"] = plugins.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["streamTypeCounts"] = streams.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["recordCountCounts"] = counts.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["bodiesWithATail"] = withTail,
                ["noTailAfterTheRun"] = noTail,
                ["tailCountOutOfRange"] = tailOutOfRange,
                ["tailEntriesDeclared"] = tailDeclared,
                ["tailEntriesEchoed"] = tailEchoed,
                ["tailEchoesMatchTheCount"] = tailMatches,
                ["tailEchoesExceedTheCount"] = tailExceeds,
                ["firstTailEntryNamesADeclaredSource"] = firstNames,
                ["firstTailEntryTooShort"] = firstShort,
                ["entriesInspected"] = inspected,
                ["entriesWithNoRecords"] = noRecords,
                ["entriesWhoseRecordsFit"] = recordsFit,
                ["entriesWhoseCountIsNotUsable"] = countUnusable,
                ["entriesWhoseRecordsRunPastTheEnd"] = recordsPastEnd,
                ["curveRecords"] = curveRecords,
                ["interpolationCounts"] = interps.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["tailEntryCountCounts"] = tailCounts.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["firstTailEntryLeadingWordCounts"] = leadWords.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private static Dictionary<string, object?> BuildMusicHeadSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var bodies = 0U; var resolved = 0U; var unresolved = 0U; var zero = 0U;
            var tailTested = 0U; var tailNamed = 0U; var tailShort = 0U;
            var tailNamedBy = new Dictionary<string, uint>(StringComparer.Ordinal);
            var tailTestedBy = new Dictionary<string, uint>(StringComparer.Ordinal);
            var unknown = 0U; var tooShort = 0U; var unknownHead = 0U;
            var byType = new Dictionary<string, uint>(StringComparer.Ordinal);
            var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
            var discriminants = new Dictionary<string, uint>(StringComparer.Ordinal);
            var headShapes = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var structure in structures)
            {
                var head = structure.MusicHeadReferences;
                bodies = checked(bodies + head.Bodies);
                tailTested = checked(tailTested + head.TailWordsTested);
                tailNamed = checked(tailNamed + head.TailWordsNamed);
                tailShort = checked(tailShort + head.BodiesTooShortForTailWords);
                foreach (var pair in head.TailWordNamedByOffset)
                {
                    tailNamedBy.TryGetValue(pair.Key, out var a1);
                    tailNamedBy[pair.Key] = checked(a1 + pair.Value);
                }
                foreach (var pair in head.TailWordTestedByOffset)
                {
                    tailTestedBy.TryGetValue(pair.Key, out var a2);
                    tailTestedBy[pair.Key] = checked(a2 + pair.Value);
                }
                resolved = checked(resolved + head.Resolved);
                unresolved = checked(unresolved + head.Unresolved);
                zero = checked(zero + head.Zero);
                unknown = checked(unknown + head.UnknownDiscriminant);
                tooShort = checked(tooShort + head.TooShort);
                unknownHead = checked(unknownHead + head.UnknownHeadShape);
                foreach (var pair in head.BodiesByType)
                {
                    byType.TryGetValue(pair.Key, out var existing);
                    byType[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in head.OffsetCounts)
                {
                    offsets.TryGetValue(pair.Key, out var existing);
                    offsets[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in head.DiscriminantCounts)
                {
                    discriminants.TryGetValue(pair.Key, out var existing);
                    discriminants[pair.Key] = checked(existing + pair.Value);
                }
                foreach (var pair in head.HeadShapeCounts)
                {
                    headShapes.TryGetValue(pair.Key, out var existing);
                    headShapes[pair.Key] = checked(existing + pair.Value);
                }
            }
            return new Dictionary<string, object?>
            {
                ["bodies"] = bodies,
                ["resolved"] = resolved,
                ["unresolved"] = unresolved,
                ["zero"] = zero,
                ["unknownDiscriminant"] = unknown,
                ["unknownHeadShape"] = unknownHead,
                ["tooShort"] = tooShort,
                ["bodiesByType"] = byType.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["tailWordsTested"] = tailTested,
                ["tailWordsNamed"] = tailNamed,
                ["bodiesTooShortForTailWords"] = tailShort,
                ["tailWordNamedByOffset"] = tailNamedBy.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["tailWordTestedByOffset"] = tailTestedBy.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["offsetCounts"] = offsets.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["discriminantCounts"] = discriminants.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                ["headShapeCounts"] = headShapes.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private static Dictionary<string, object?> BuildBodyFrameSummary(
            IEnumerable<EndfieldBnkStructure> structures,
            Func<EndfieldBnkStructure, EndfieldHircBodyCensus> select)
        {
            var rows = structures.Select(select).ToArray();
            var examples = rows
                .SelectMany(row => row.FailureExamples)
                .Take(16)
                .Select(example => new Dictionary<string, object?>
                {
                    ["bankId"] = example.BankId,
                    ["ordinal"] = example.Ordinal,
                    ["objectId"] = example.ObjectId,
                    ["status"] = example.Status,
                    ["category"] = example.Category,
                    ["cursorOffset"] = example.CursorOffset,
                    ["expectedBytes"] = example.ExpectedBytes,
                    ["actualBytes"] = example.ActualBytes,
                })
                .ToArray();

            static Dictionary<string, long> Merge(
                IEnumerable<KeyValuePair<string, uint>> pairs) => pairs
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(pair => (long)pair.Value),
                        StringComparer.Ordinal);

            // The largest a single body declared, which does not sum across banks.
            static Dictionary<string, long> MergeMax(
                IEnumerable<KeyValuePair<string, uint>> pairs) => pairs
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Max(pair => (long)pair.Value),
                        StringComparer.Ordinal);

            return new Dictionary<string, object?>
            {
                ["count"] = rows.Sum(row => (long)row.FrameCount),
                ["exact"] = rows.Sum(row => (long)row.ExactCount),
                ["unsupported"] = rows.Sum(row => (long)row.UnsupportedCount),
                ["failed"] = rows.Sum(row => (long)row.FailedCount),
                ["ambiguous"] = 0,
                ["bodyBytes"] = rows.Sum(row => (long)row.BodyBytes),
                ["exactCursorBytes"] = rows.Sum(row => (long)row.ExactCursorBytes),
                ["nonExactBodyBytes"] = rows.Sum(row => (long)row.NonExactBytes),
                ["minExactBodyBytes"] = rows.Where(row => row.ExactCount > 0)
                    .Select(row => row.MinExactBytes).DefaultIfEmpty(0u).Min(),
                ["maxExactBodyBytes"] = rows.Select(row => row.MaxExactBytes)
                    .DefaultIfEmpty(0u).Max(),
                ["groupCounts"] = Merge(rows.SelectMany(row => row.GroupCounts)),
                ["groupBodies"] = Merge(rows.SelectMany(row => row.GroupBodies)),
                ["groupMaxInOneBody"] = MergeMax(rows.SelectMany(row => row.GroupMaxInOneBody)),
                ["selectorCounts"] = Merge(rows.SelectMany(row => row.SelectorCounts)),
                ["failureCategories"] = Merge(rows.SelectMany(row => row.FailureCounts)),
                ["unsupportedCategories"] = Merge(rows.SelectMany(row => row.UnsupportedCategories)),
                ["nonExactExamples"] = examples,
            };
        }


        private static Dictionary<string, long> MergeCensus(
            IEnumerable<KeyValuePair<string, uint>> pairs) => pairs
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(pair => (long)pair.Value),
                    StringComparer.Ordinal);

        private static Dictionary<string, object?> BuildType4U32VectorFrameSummary(
            IEnumerable<EndfieldBnkStructure> structures)
        {
            var rows = structures.ToArray();
            var examples = rows
                .SelectMany(row => row.Type4U32VectorFailureExamples)
                .Take(16)
                .Select(example => new Dictionary<string, object?>
                {
                    ["bankId"] = example.BankId,
                    ["ordinal"] = example.Ordinal,
                    ["objectId"] = example.ObjectId,
                    ["status"] = example.Status,
                    ["category"] = example.Category,
                    ["expectedBytes"] = example.ExpectedBytes,
                    ["actualBytes"] = example.ActualBytes,
                    ["opaqueTailBytes"] = example.OpaqueTailBytes,
                })
                .ToArray();
            return new Dictionary<string, object?>
            {
                ["count"] = rows.Sum(row => (long)row.Type4U32VectorFrameCount),
                ["exact"] = rows.Sum(row => (long)row.Type4U32VectorExactCount),
                ["unsupported"] = rows.Sum(row => (long)row.Type4U32VectorUnsupportedCount),
                ["failed"] = rows.Sum(row => (long)row.Type4U32VectorFailedCount),
                ["ambiguous"] = 0,
                ["bodyBytes"] = rows.Sum(row => (long)row.Type4U32VectorBodyBytes),
                ["candidatePrefixBytes"] = rows.Sum(row => (long)row.Type4U32VectorPrefixBytes),
                ["unsupportedCandidatePrefixBytes"] = rows.Sum(row => (long)row.Type4U32VectorUnsupportedPrefixBytes),
                ["exactCursorBytes"] = rows.Sum(row => (long)row.Type4U32VectorExactCursorBytes),
                ["opaqueTailBytes"] = rows.Sum(row => (long)row.Type4U32VectorOpaqueTailBytes),
                ["failedBodyBytes"] = rows.Sum(row => (long)row.Type4U32VectorFailedBodyBytes),
                ["candidateEntryCount"] = rows.Sum(row => (long)row.Type4U32VectorEntryCount),
                ["exactEntryCount"] = rows.Sum(row => (long)row.Type4U32VectorExactEntryCount),
                ["failureCategories"] = rows
                    .SelectMany(row => row.Type4U32VectorFailureCounts)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(pair => (long)pair.Value),
                        StringComparer.Ordinal),
                ["unsupportedCategories"] = rows
                    .SelectMany(row => row.Type4U32VectorUnsupportedCategories)
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(pair => (long)pair.Value),
                        StringComparer.Ordinal),
                ["nonExactExamples"] = examples,
            };
        }

        private sealed class AudioAuditOptions
        {
            public string StreamingAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string Output { get; set; } = "./akpk_audit.json";
            public List<EndfieldVfsBlockType> BlockTypes { get; } = new();
            public bool HircOnly { get; set; }
            public string NamedHashFile { get; set; }
        }

        private static AudioAuditOptions ParseAuditOptions(string[] args)
        {
            var options = new AudioAuditOptions();
            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
                var value = token.Contains('=') ? token[(token.IndexOf('=') + 1)..] : null;
                if (value is not null)
                {
                    token = token[..token.IndexOf('=')];
                }
                string Next()
                {
                    if (value is not null) return value;
                    if (++i >= args.Length) throw new ArgumentException($"{token} requires a value");
                    return args[i];
                }
                switch (token)
                {
                    case "-s":
                    case "--streaming-assets": options.StreamingAssets = Next(); break;
                    case "--fallback-assets": options.FallbackAssets = Next(); break;
                    case "--hirc-only": options.HircOnly = true; break;
                    case "--named-hash-file": options.NamedHashFile = Next(); break;
                    case "-o":
                    case "--output": options.Output = Next(); break;
                    case "-b":
                    case "--block":
                    {
                        var block = ParseAuditBlock(Next());
                        options.BlockTypes.Add(block);
                        break;
                    }
                    default: throw new ArgumentException($"unexpected argument: {token}");
                }
            }
            if (string.IsNullOrEmpty(options.StreamingAssets)) throw new ArgumentException("--streaming-assets is required");
            if (options.BlockTypes.Count == 0)
            {
                options.BlockTypes.AddRange(new[]
                {
                    EndfieldVfsBlockType.Audio,
                    EndfieldVfsBlockType.InitialAudio,
                    EndfieldVfsBlockType.AuditAudio,
                    EndfieldVfsBlockType.HotfixAudio,
                    EndfieldVfsBlockType.AudioChinese,
                    EndfieldVfsBlockType.AudioEnglish,
                    EndfieldVfsBlockType.AudioJapanese,
                    EndfieldVfsBlockType.AudioKorean,
                });
            }
            return options;
        }

        private static EndfieldVfsBlockType ParseAuditBlock(string value) => value.ToLowerInvariant() switch
        {
            "audio" => EndfieldVfsBlockType.Audio,
            "initial-audio" or "initialaudio" => EndfieldVfsBlockType.InitialAudio,
            "audit-audio" or "auditaudio" => EndfieldVfsBlockType.AuditAudio,
            "hotfix-audio" or "hotfixaudio" => EndfieldVfsBlockType.HotfixAudio,
            "audio-chinese" or "audiochinese" or "chinese" => EndfieldVfsBlockType.AudioChinese,
            "audio-english" or "audioenglish" or "english" => EndfieldVfsBlockType.AudioEnglish,
            "audio-japanese" or "audiojapanese" or "japanese" => EndfieldVfsBlockType.AudioJapanese,
            "audio-korean" or "audiokorean" or "korean" => EndfieldVfsBlockType.AudioKorean,
            _ => throw new ArgumentException($"unsupported or excluded audio block: {value}"),
        };

        private static bool IsAudioBlock(EndfieldVfsBlockType blockType) => blockType is
            EndfieldVfsBlockType.InitialAudio or
            EndfieldVfsBlockType.AuditAudio or
            EndfieldVfsBlockType.Audio or
            EndfieldVfsBlockType.HotfixAudio or
            EndfieldVfsBlockType.AudioChinese or
            EndfieldVfsBlockType.AudioEnglish or
            EndfieldVfsBlockType.AudioJapanese or
            EndfieldVfsBlockType.AudioKorean;

        private static JObject LoadAudioDialogLayer(EndfieldVfsLoader loader)
        {
            var blockInfo = loader.LoadBlockInfo(EndfieldVfsBlockType.Table);
            foreach (var chunk in blockInfo.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    byte[] data;
                    try
                    {
                        data = loader.ExtractFileToBytes(EndfieldVfsBlockType.Table, chunk, file);
                    }
                    catch (EndfieldVfsChunkNotFoundException)
                    {
                        // Persistent Table metadata can retain audit chunks
                        // supplied by the primary VFS.  A fallback-only loader
                        // cannot read those shared chunks; continue to the
                        // chunks that are actually present in Persistent.
                        continue;
                    }
                    try
                    {
                        var parsed = EndfieldSparkBuffer.ParseBytes(data);
                        if (parsed.Name == "AudioDialog" && parsed.Data is JObject obj)
                        {
                            return obj;
                        }
                    }
                    catch
                    {
                        // fluffy-dumper ignores non-SparkBuffer table rows while looking for AudioDialog.
                    }
                }
            }

            throw new EndfieldVfsException("AudioDialog.bytes not found in Table block");
        }

        private static bool IsChunkMissing(
            EndfieldVfsLoader loader,
            EndfieldVfsBlockType blockType,
            EndfieldVfsChunkInfo chunk)
        {
            try
            {
                loader.ResolveChunkPath(blockType, chunk);
                return false;
            }
            catch (EndfieldVfsChunkNotFoundException)
            {
                return true;
            }
        }

        private static List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> ExtractPckFiles(EndfieldVfsBlockMainInfo blockInfo)
        {
            var files = new List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)>();
            foreach (var chunk in blockInfo.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    if (file.FileName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add((chunk, file));
                    }
                }
            }
            return files;
        }

        private static List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> ExtractPckFiles(
            EndfieldVfsLoader loader,
            EndfieldVfsBlockType blockType) =>
            ExtractPckFiles(loader.LoadBlockInfo(blockType));

        private static void WriteAudioFile(byte[] wemData, string outputPath, AudioOutputFormat format, EndfieldVgmstreamConverter converter)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (format == AudioOutputFormat.Wem)
            {
                File.WriteAllBytes(outputPath, wemData);
            }
            else if (format == AudioOutputFormat.Flac)
            {
                converter.ConvertBytesToFlac(wemData, outputPath);
            }
            else
            {
                converter.ConvertBytes(wemData, outputPath);
            }
        }

        // Maps a Wwise PCK file name to its source-bank folder, matching the Python
        // indexer (build_audio.unmapped_bank_for_pck_name).
        private static string UnmappedBankFolder(string pckName)
        {
            var name = Path.GetFileName(pckName ?? string.Empty).ToLowerInvariant();
            if (name.Contains("external_source", StringComparison.Ordinal))
            {
                return "external";
            }
            if (name.StartsWith("init", StringComparison.Ordinal))
            {
                return "initial";
            }
            if (name.StartsWith("audit", StringComparison.Ordinal))
            {
                return "audit";
            }
            if (name.StartsWith("hotfix", StringComparison.Ordinal))
            {
                return "hotfix";
            }
            return "main";
        }

        private static bool HasMagic(byte[] data, string magic)
        {
            if (data.Length < magic.Length)
            {
                return false;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (data[i] != (byte)magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static string MagicPreview(byte[] data) =>
            data.Length == 0
                ? "<empty>"
                : Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 8)));

        private static AudioOptions ParseOptions(string[] args)
        {
            var options = new AudioOptions
            {
                Output = "./output",
                LanguageMode = "all",
                Format = AudioOutputFormat.Flac,
                BlockMode = AudioBlockMode.All,
                Jobs = Math.Min(8, Math.Max(1, Environment.ProcessorCount)),
            };

            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
                string value = null;
                var equalsIndex = token.IndexOf('=');
                if (equalsIndex > 0)
                {
                    value = token[(equalsIndex + 1)..];
                    token = token[..equalsIndex];
                }

                switch (token)
                {
                    case "-s":
                    case "--streaming-assets":
                        options.StreamingAssets = value ?? NextValue(args, ref i, token);
                        break;
                    case "--fallback-assets":
                        options.FallbackAssets = value ?? NextValue(args, ref i, token);
                        break;
                    case "-o":
                    case "--output":
                        options.Output = value ?? NextValue(args, ref i, token);
                        break;
                    case "--shared-output":
                        options.SharedOutput = value ?? NextValue(args, ref i, token);
                        break;
                    case "-l":
                    case "--language":
                        options.LanguageMode = value ?? NextValue(args, ref i, token);
                        break;
                    case "-f":
                    case "--format":
                        var rawFormat = value ?? NextValue(args, ref i, token);
                        options.Format = rawFormat.ToLowerInvariant() switch
                        {
                            "flac" => AudioOutputFormat.Flac,
                            "wem" => AudioOutputFormat.Wem,
                            "wav" => AudioOutputFormat.Wav,
                            _ => throw new ArgumentException($"unknown format: {rawFormat}"),
                        };
                        break;
                    case "-b":
                    case "--block":
                        options.BlockMode = ParseBlockMode(value ?? NextValue(args, ref i, token));
                        break;
                    case "-j":
                    case "--jobs":
                        options.Jobs = int.Parse(value ?? NextValue(args, ref i, token));
                        break;
                    default:
                        throw new ArgumentException($"unexpected argument: {token}");
                }
            }

            if (string.IsNullOrEmpty(options.StreamingAssets))
            {
                throw new ArgumentException("--streaming-assets is required");
            }
            if (options.Jobs <= 0)
            {
                throw new ArgumentException("--jobs must be greater than zero");
            }

            return options;
        }

        private static string NextValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value");
            }
            index++;
            return args[index];
        }

        private static AudioBlockMode ParseBlockMode(string value) => value.ToLowerInvariant() switch
        {
            "all" => AudioBlockMode.All,
            "voice" => AudioBlockMode.Voice,
            "audio" => AudioBlockMode.Audio,
            "initial-audio" => AudioBlockMode.InitialAudio,
            "initialaudio" => AudioBlockMode.InitialAudio,
            "audit-audio" => AudioBlockMode.AuditAudio,
            "auditaudio" => AudioBlockMode.AuditAudio,
            "hotfix-audio" => AudioBlockMode.HotfixAudio,
            "hotfixaudio" => AudioBlockMode.HotfixAudio,
            _ => throw new ArgumentException($"unknown audio block: {value}"),
        };

        private sealed class AudioOptions
        {
            public string StreamingAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string Output { get; set; }
            public string SharedOutput { get; set; }
            public string LanguageMode { get; set; }
            public AudioOutputFormat Format { get; set; }
            public AudioBlockMode BlockMode { get; set; }
            public int Jobs { get; set; }

            public IReadOnlyList<EndfieldAudioLanguage> Languages
            {
                get
                {
                    if (string.Equals(LanguageMode, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        return EndfieldAudioLanguages.All;
                    }

                    if (EndfieldAudioLanguages.TryParse(LanguageMode, out var language))
                    {
                        return new[] { language };
                    }

                    throw new ArgumentException($"unknown language: {LanguageMode}");
                }
            }

            public IReadOnlyList<EndfieldVfsBlockType> BlockTypes(EndfieldAudioLanguage language) => BlockMode switch
            {
                AudioBlockMode.All => new[]
                {
                    EndfieldVfsBlockType.Audio,
                    EndfieldVfsBlockType.InitialAudio,
                    EndfieldVfsBlockType.AuditAudio,
                    VoiceBlock(language),
                },
                AudioBlockMode.Voice => new[] { VoiceBlock(language) },
                AudioBlockMode.Audio => new[] { EndfieldVfsBlockType.Audio },
                AudioBlockMode.InitialAudio => new[] { EndfieldVfsBlockType.InitialAudio },
                AudioBlockMode.AuditAudio => new[] { EndfieldVfsBlockType.AuditAudio },
                AudioBlockMode.HotfixAudio => new[] { EndfieldVfsBlockType.HotfixAudio },
                _ => Array.Empty<EndfieldVfsBlockType>(),
            };

            public string OutputForBlock(EndfieldVfsBlockType blockType) =>
                !string.IsNullOrEmpty(SharedOutput) && IsSharedBlock(blockType)
                    ? SharedOutput
                    : Output;

            private static bool IsSharedBlock(EndfieldVfsBlockType blockType) => blockType is
                EndfieldVfsBlockType.Audio or
                EndfieldVfsBlockType.InitialAudio or
                EndfieldVfsBlockType.AuditAudio or
                EndfieldVfsBlockType.HotfixAudio;

            private static EndfieldVfsBlockType VoiceBlock(EndfieldAudioLanguage language) => language switch
            {
                EndfieldAudioLanguage.Chinese => EndfieldVfsBlockType.AudioChinese,
                EndfieldAudioLanguage.English => EndfieldVfsBlockType.AudioEnglish,
                EndfieldAudioLanguage.Japanese => EndfieldVfsBlockType.AudioJapanese,
                EndfieldAudioLanguage.Korean => EndfieldVfsBlockType.AudioKorean,
                _ => EndfieldVfsBlockType.Audio,
            };
        }

        public enum AudioOutputFormat
        {
            Flac,
            Wem,
            Wav,
        }

        private enum AudioBlockMode
        {
            All,
            Voice,
            Audio,
            InitialAudio,
            AuditAudio,
            HotfixAudio,
        }
    }

    internal static class AudioOutputFormatExtensions
    {
        public static string Extension(this EndfieldAudioCli.AudioOutputFormat format) =>
            format switch
            {
                EndfieldAudioCli.AudioOutputFormat.Flac => "flac",
                EndfieldAudioCli.AudioOutputFormat.Wav => "wav",
                _ => "wem",
            };
    }
}
