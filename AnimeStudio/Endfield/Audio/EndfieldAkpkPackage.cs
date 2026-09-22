using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio.Endfield
{
    public sealed class EndfieldAkpkPackage
    {
        private readonly byte[] data;

        private EndfieldAkpkPackage(byte[] data)
        {
            this.data = data;
        }

        public List<EndfieldWemEntry> Entries { get; } = new();
        public Dictionary<uint, string> Languages { get; } = new();
        public uint HeaderSize { get; private set; }
        public uint Version { get; private set; }
        public uint LanguageSectorSize { get; private set; }
        public uint BanksSectorSize { get; private set; }
        public uint SoundsSectorSize { get; private set; }
        public uint ExternalsSectorSize { get; private set; }
        public bool EncryptedHeader { get; private set; }
        public int BankCount { get; private set; }
        public int SoundCount { get; private set; }
        public int ExternalCount { get; private set; }
        public List<EndfieldBnkStructure> BnkStructures { get; } = new();
        public EndfieldHircMediaJoinCensus MediaJoin { get; } = new();
        public EndfieldHircSourceRecordCensus SourceRecords { get; } = new();
        public EndfieldHircType03TargetCensus Type03Targets { get; } = new();
        public EndfieldHircType08TailWordCensus Type08TailWords { get; } = new();
        public EndfieldHircType08TailWordCensus Type12TailWords { get; } = new();
        public EndfieldHircMusicReferenceCensus MusicReferences { get; } = new();
        public EndfieldHircType0AHeadCensus Type0AHead { get; } = new();
        public EndfieldHircType0AEndAnchorCensus Type0AEndAnchor { get; } = new();
        public EndfieldHircType0ACountedArrayCensus Type0ACountedArray { get; } = new();
        public EndfieldHircMusicReachCensus MusicReach { get; } = new();
        public EndfieldHircStmgWordCensus StmgWords { get; } = new();
        public EndfieldStmgCensus Stmg { get; } = new();
        public EndfieldInitCensus Init { get; } = new();
        public EndfieldEnvsCensus Envs { get; } = new();
        public EndfieldHircSharedConstantCensus SharedConstants { get; } = new();
        public EndfieldHircHierarchyCensus Hierarchy { get; } = new();
        public EndfieldHircMusicMutualityCensus MusicMutuality { get; } = new();
        public EndfieldHircType0CHierarchyCensus Type0CHierarchy { get; } = new();
        public EndfieldHircType0AElementCensus Type0AElements { get; } = new();
        // Identities the caller wants walked. Empty by default, so the walk costs
        // nothing unless a consumer supplies them.
        public static HashSet<uint> NamedIdentityHashes { get; set; } = new();

        public static EndfieldAkpkPackage Parse(byte[] input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }
            if (input.Length < 16)
            {
                throw new InvalidDataException("invalid AKPK magic");
            }

            var data = (byte[])input.Clone();
            var packageEncryptedHeader = false;
            if (HasMagic(data, ":)xD"))
            {
                packageEncryptedHeader = true;
                var headerSize = BitConverter.ToUInt32(data, 4);
                if (headerSize < 16 || headerSize > data.Length)
                {
                    throw new InvalidDataException("invalid AKPK header size");
                }

                EndfieldAudioCrypto.DecryptVfs(data, 12, checked((int)headerSize - 4), headerSize, 0);
                data[0] = (byte)'A';
                data[1] = (byte)'K';
                data[2] = (byte)'P';
                data[3] = (byte)'K';
                BitConverter.GetBytes(1U).CopyTo(data, 8);
            }

            if (!HasMagic(data, "AKPK"))
            {
                throw new InvalidDataException("invalid AKPK magic");
            }

            var package = new EndfieldAkpkPackage(data);
            package.EncryptedHeader = packageEncryptedHeader;
            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);

            stream.Position = 4;
            var headerSizeValue = ReadUInt32(reader, "header size");
            var version = ReadUInt32(reader, "version");
            if (version != 1)
            {
                throw new InvalidDataException($"unsupported AKPK version: {version}");
            }
            var languagesSectorSize = ReadUInt32(reader, "languages sector size");
            var banksSectorSize = ReadUInt32(reader, "banks sector size");
            var soundsSectorSize = ReadUInt32(reader, "sounds sector size");
            var externalsSectorSize = 0U;
            var hasExternals = (ulong)languagesSectorSize + banksSectorSize + soundsSectorSize + 0x10UL < headerSizeValue;
            if (hasExternals)
            {
                externalsSectorSize = ReadUInt32(reader, "externals sector size");
            }

            package.HeaderSize = headerSizeValue;
            package.Version = version;
            package.LanguageSectorSize = languagesSectorSize;
            package.BanksSectorSize = banksSectorSize;
            package.SoundsSectorSize = soundsSectorSize;
            package.ExternalsSectorSize = externalsSectorSize;

            var languageStart = checked((int)stream.Position);
            package.ParseLanguages(reader, languageStart, languagesSectorSize);
            var banksStart = checked(languageStart + checked((int)languagesSectorSize));
            package.BankCount = package.ParseSector(reader, banksStart, banksSectorSize, isSounds: false, isExternals: false);
            var soundsStart = checked(banksStart + checked((int)banksSectorSize));
            package.SoundCount = package.ParseSector(reader, soundsStart, soundsSectorSize, isSounds: true, isExternals: false);
            var externalsStart = checked(soundsStart + checked((int)soundsSectorSize));
            package.ExternalCount = package.ParseSector(reader, externalsStart, externalsSectorSize, isSounds: true, isExternals: true);
            package.JoinType2SourcesToMedia();
            package.CountNamedBanksAndMedia();
            package.ClassifyType03Targets();
            package.ClassifyType08TailWords();
            package.ClassifyMusicReferences();
            package.ClassifyType0AHeadPredictions();
            package.ClassifyType0AEndAnchor();
            package.ClassifyType0ACountedArray();
            package.WalkMusicFromActions();
            package.FrameStmgSections();
            package.FrameInitAndPlat();
            package.ResolveStmgWords();
            package.ClassifySharedFrameConstants();
            package.ClassifySharedHierarchy();
            package.ClassifyMusicMutuality();
            package.ClassifyType0CHierarchy();
            package.WalkNamedReachAcrossPackage();
            return package;
        }


        // Runs once every sector is parsed, because it needs both the HIRC bodies and
        // the media table. Counts are over distinct source ids per plug-in id: the same
        // id declared twice is one media question, not two.
        /// <summary>
        /// The 14-byte source record numeric types 0x02 and 0x0B share.
        /// </summary>
        /// <remarks>
        /// 0x02 carries exactly one, at body offset 0. 0x0B carries a counted array of
        /// them after its leading flag and count. That they are the same record is
        /// shown twice over, not assumed from a matching width.
        ///
        /// The word at +0 is a plugin id from a closed set. 0x0B uses two values and
        /// 0x02 opens with one of those same two in 140,121 of its 142,815 bodies.
        ///
        /// The word at +5 names a declared media id in **every** one of 0x0B's 4,447
        /// records, and in 0 of them at any other offset in the record. That contrast
        /// is what makes +5 the id field; a bare hit rate would not.
        ///
        /// Between them the two types account for 61,325 of the 61,333 media ids the
        /// packages declare. Adding 0x0B closed 1,276 that no source record had named.
        /// </remarks>
        private static void CollectSourceRecord(
            EndfieldBnkStructure structure, byte objectType, ReadOnlySpan<byte> record)
        {
            if (!structure.SourceRecordsByType.TryGetValue(objectType, out var rows))
            {
                rows = new List<byte[]>();
                structure.SourceRecordsByType[objectType] = rows;
            }
            rows.Add(record.ToArray());
        }


        private void CensusSourceRecords()
        {
            foreach (var structure in BnkStructures)
            {
                foreach (var pair in structure.SourceRecordsByType)
                {
                    var typeKey = $"type{pair.Key:X2}";
                    foreach (var record in pair.Value)
                    {
                        SourceRecords.Records = checked(SourceRecords.Records + 1);
                        HircBump(SourceRecords.RecordsByType, typeKey, 1);
                        if (record.Length < SourceRecordBytes)
                        {
                            SourceRecords.RecordsTooShort =
                                checked(SourceRecords.RecordsTooShort + 1);
                            continue;
                        }
                        HircBump(
                            SourceRecords.PluginIdsByType,
                            $"{typeKey}_plugin_{BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0, 4)):X8}",
                            1);
                        // The id field and its two neighbours, published as distinct
                        // value sets rather than joined here.
                        //
                        // The join CANNOT be done in this method: a source id names media
                        // declared by a DIFFERENT package, so a package-local join scores
                        // 12 of 147,262 and says nothing. The pooling happens in the
                        // Python audit, which already unions every package's media.
                        // *A join that must cross a file boundary cannot be gated inside
                        // one file.*
                        Collect(SourceRecords.IdValuesByType, typeKey,
                                BinaryPrimitives.ReadUInt32LittleEndian(
                                    record.AsSpan(SourceRecordIdOffset, 4)));
                        Collect(SourceRecords.IdValuesBeforeByType, typeKey,
                                BinaryPrimitives.ReadUInt32LittleEndian(
                                    record.AsSpan(SourceRecordIdOffset - 1, 4)));
                        Collect(SourceRecords.IdValuesAfterByType, typeKey,
                                BinaryPrimitives.ReadUInt32LittleEndian(
                                    record.AsSpan(SourceRecordIdOffset + 1, 4)));
                    }
                }
            }
            SourceRecords.MediaIdsDeclared = checked((uint)MediaJoin.MediaIds.Count);
        }


        private static void Collect(
            Dictionary<string, SortedSet<uint>> map, string key, uint value)
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new SortedSet<uint>();
                map[key] = set;
            }
            set.Add(value);
        }


        private void JoinType2SourcesToMedia()
        {
            foreach (var entry in Entries)
            {
                if (entry.Id <= uint.MaxValue)
                {
                    MediaJoin.MediaIds.Add(checked((uint)entry.Id));
                }
            }
            MediaJoin.MediaEntries = checked((uint)MediaJoin.MediaIds.Count);
            CensusSourceRecords();
            foreach (var structure in BnkStructures)
            {
                foreach (var pair in structure.Type2SourcesByPlugin)
                {
                    var key = $"plugin_{pair.Key:X8}";
                    if (!MediaJoin.SourceIdsByPlugin.TryGetValue(key, out var ids))
                    {
                        ids = new SortedSet<uint>();
                        MediaJoin.SourceIdsByPlugin[key] = ids;
                    }
                    foreach (var sourceId in pair.Value)
                    {
                        ids.Add(sourceId);
                    }
                }
            }
        }


        // Bank ids and media ids are separate id populations from HIRC objects. The
        // caller's hash set is tested against them here so the same coincidence
        // arithmetic can judge whether either is named.
        private void CountNamedBanksAndMedia()
        {
            var hashes = NamedIdentityHashes;
            if (hashes == null || hashes.Count == 0)
            {
                return;
            }
            var banks = new HashSet<uint>();
            foreach (var structure in BnkStructures)
            {
                banks.Add(checked((uint)(structure.BankId & 0xFFFFFFFF)));
            }
            foreach (var structure in BnkStructures)
            {
                structure.NamedReachCensus.BanksSeen = 0;
                structure.NamedReachCensus.BanksMatched = 0;
                structure.NamedReachCensus.MediaSeen = 0;
                structure.NamedReachCensus.MediaMatched = 0;
            }
            var first = BnkStructures.Count > 0 ? BnkStructures[0].NamedReachCensus : null;
            if (first == null)
            {
                return;
            }
            foreach (var bank in banks)
            {
                first.BanksSeen = checked(first.BanksSeen + 1);
                if (hashes.Contains(bank))
                {
                    first.BanksMatched = checked(first.BanksMatched + 1);
                }
            }
            foreach (var media in MediaJoin.MediaIds)
            {
                first.MediaSeen = checked(first.MediaSeen + 1);
                if (hashes.Contains(media))
                {
                    first.MediaMatched = checked(first.MediaMatched + 1);
                }
            }
        }


        // Runs once every bank in the package is parsed, because "another bank" is
        // only answerable then. A target outside the package is reported as such
        // rather than as unresolved: this reader cannot see the other packages.
        /// <summary>
        /// Decide whether the words in numeric type 0x08's tail head are references.
        /// </summary>
        /// <remarks>
        /// Two words are classified, not one. The second exists only as a control: if
        /// the first word's hits were an artefact of reading a 32-bit value at an
        /// arbitrary offset, the second would hit at the same rate. Object ids are
        /// sparse against the 32-bit range -- a package declares on the order of a
        /// thousand -- so chance hits are expected far below one across the whole
        /// corpus, and any hit at all is informative.
        ///
        /// Targets are also counted by the numeric type they land on, because a
        /// reference that always names the same type says more than one that scatters.
        /// </remarks>
        private void ClassifyType08TailWords()
        {
            ClassifyTailWords(Type08TailWords, x => x.Type08TailWords);
            ClassifyTailWords(Type12TailWords, x => x.Type12TailWords);
        }

        private void ClassifyTailWords(
            EndfieldHircType08TailWordCensus census,
            Func<EndfieldBnkStructure, List<(uint First, uint Second)>> select)
        {
            var perBank = new Dictionary<ulong, HashSet<uint>>();
            var everything = new HashSet<uint>();
            var typeOf = new Dictionary<uint, byte>();
            foreach (var structure in BnkStructures)
            {
                perBank[structure.BankId] = structure.DeclaredObjectIds;
                everything.UnionWith(structure.DeclaredObjectIds);
                foreach (var pair in structure.WalkObjectTypes)
                {
                    typeOf[pair.Key] = pair.Value;
                }
            }
            census.PackagePopulation = checked((uint)everything.Count);
            foreach (var structure in BnkStructures)
            {
                var own = perBank[structure.BankId];
                foreach (var (first, second) in select(structure))
                {
                    census.Heads = checked(census.Heads + 1);
                    if (own.Contains(first))
                    {
                        census.FirstWordSameBank =
                            checked(census.FirstWordSameBank + 1);
                    }
                    else if (everything.Contains(first))
                    {
                        census.FirstWordOtherBankInPackage =
                            checked(census.FirstWordOtherBankInPackage + 1);
                    }
                    else
                    {
                        census.FirstWordOutsidePackage =
                            checked(census.FirstWordOutsidePackage + 1);
                    }
                    if (everything.Contains(first) && typeOf.TryGetValue(first, out var targetType))
                    {
                        HircBump(census.FirstWordTargetTypeCounts, $"type{targetType:X2}", 1);
                    }
                    if (everything.Contains(second))
                    {
                        census.SecondWordResolves =
                            checked(census.SecondWordResolves + 1);
                        if (typeOf.TryGetValue(second, out var controlType))
                        {
                            HircBump(
                                census.SecondWordTargetTypeCounts, $"type{controlType:X2}", 1);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Decide which words in a music body name objects the package ships.
        /// </summary>
        /// <remarks>
        /// The music types resist framing, so their references cannot be read from a
        /// known offset. What can be done is to offer every word and let the id space
        /// decide: a package declares on the order of a thousand objects against a
        /// 32-bit range, so chance resolutions across the whole corpus are expected in
        /// the low single digits. Anything above that is real, and the target types are
        /// counted so the relationships, not just the counts, are visible.
        ///
        /// Bodies are counted by how many references they carry, because "every body
        /// carries at least one" is a much stronger statement than a total.
        /// </remarks>
        private void ClassifyMusicReferences()
        {
            var everything = new HashSet<uint>();
            var typeOf = new Dictionary<uint, byte>();
            foreach (var structure in BnkStructures)
            {
                everything.UnionWith(structure.DeclaredObjectIds);
                foreach (var pair in structure.WalkObjectTypes)
                {
                    typeOf[pair.Key] = pair.Value;
                }
            }
            MusicReferences.PackagePopulation = checked((uint)everything.Count);
            var reached = new Dictionary<string, Dictionary<uint, uint>>(StringComparer.Ordinal);
            var population = new Dictionary<byte, HashSet<uint>>();
            foreach (var pair in typeOf)
            {
                if (!population.TryGetValue(pair.Value, out var set))
                {
                    set = new HashSet<uint>();
                    population[pair.Value] = set;
                }
                set.Add(pair.Key);
            }
            foreach (var structure in BnkStructures)
            {
                foreach (var (objectType, words, distances) in structure.MusicBodyWords)
                {
                    MusicReferences.Bodies = checked(MusicReferences.Bodies + 1);
                    MusicReferences.WordsOffered = checked(MusicReferences.WordsOffered + (uint)words.Length);
                    var hits = 0U;
                    for (var w = 0; w < words.Length; w++)
                    {
                        var word = words[w];
                        if (!everything.Contains(word))
                        {
                            continue;
                        }
                        hits = checked(hits + 1);
                        if (typeOf.TryGetValue(word, out var targetType))
                        {
                            var edge = $"type{objectType:X2}_to_type{targetType:X2}";
                            HircBump(MusicReferences.EdgeCounts, edge, 1);
                            HircBump(
                                MusicReferences.EdgeDistanceFromEnd,
                                $"{edge}_at{distances[w]}",
                                1);
                            if (!reached.TryGetValue(edge, out var seen))
                            {
                                seen = new Dictionary<uint, uint>();
                                reached[edge] = seen;
                            }
                            seen.TryGetValue(word, out var times);
                            seen[word] = checked(times + 1);
                        }
                    }
                    MusicReferences.References = checked(MusicReferences.References + hits);
                    if (hits == 0)
                    {
                        MusicReferences.BodiesWithNoReference =
                            checked(MusicReferences.BodiesWithNoReference + 1);
                    }
                    HircBump(MusicReferences.ReferencesPerBody, $"refs_{Math.Min(hits, 8)}", 1);
                }
            }
            foreach (var pair in reached)
            {
                var twice = 0U;
                foreach (var target in pair.Value)
                {
                    if (target.Value > 1)
                    {
                        twice = checked(twice + 1);
                    }
                }
                HircBump(MusicReferences.DistinctTargets, pair.Key, (uint)pair.Value.Count);
                HircBump(MusicReferences.TargetsReachedTwice, pair.Key, twice);
                var targetType = Convert.ToByte(pair.Key.Substring(pair.Key.Length - 2), 16);
                var declared = population.TryGetValue(targetType, out var set) ? (uint)set.Count : 0U;
                HircBump(MusicReferences.TargetPopulation, pair.Key, declared);
            }
        }


        /// <summary>
        /// Test numeric type 0x0A's head-length rule against its controls.
        /// </summary>
        /// <remarks>
        /// The rule says the head is a fixed part plus a counted run of five-byte
        /// elements, so the reference that follows sits at a position the count
        /// decides. It is worth nothing unless the count is what puts it there, which
        /// is what the controls measure: a fixed offset that ignores the count, and the
        /// two neighbouring positions.
        /// </remarks>
        private void ClassifyType0AHeadPredictions()
        {
            var everything = new HashSet<uint>();
            var typeOf = new Dictionary<uint, byte>();
            foreach (var structure in BnkStructures)
            {
                everything.UnionWith(structure.DeclaredObjectIds);
                foreach (var pair in structure.WalkObjectTypes)
                {
                    typeOf[pair.Key] = pair.Value;
                }
            }
            foreach (var structure in BnkStructures)
            {
                foreach (var (predicted, fixedWord, plus, minus, discriminant, headWord,
                              leadBad, padBad, values, tailBytes, tailFloat, neighbourFloat,
                              fraction, fractionControl, decibel, decibelControl, headWordFive)
                         in structure.Type0AHeadPredictions)
                {
                    Type0AHead.Bodies = checked(Type0AHead.Bodies + 1);
                    if (discriminant)
                    {
                        Type0AHead.BodiesWhereTheRuleApplies =
                            checked(Type0AHead.BodiesWhereTheRuleApplies + 1);
                    }
                    void Score(string label, uint word)
                    {
                        if (everything.Contains(word)
                            && typeOf.TryGetValue(word, out var targetType)
                            && targetType == 11)
                        {
                            HircBump(Type0AHead.NamesTheSourceType, label, 1);
                            if (discriminant)
                            {
                                HircBump(Type0AHead.NamesTheSourceTypeWhereTheRuleApplies, label, 1);
                            }
                        }
                    }
                    // The elements only mean anything where the rule is confirmed: a
                    // body whose reference is not where the rule says has no element
                    // boundary to speak of, so counting its bytes as elements would be
                    // reading arbitrary offsets.
                    if (discriminant
                        && everything.Contains(predicted)
                        && typeOf.TryGetValue(predicted, out var confirmed)
                        && confirmed == 11)
                    {
                        Type0AElements.Total = checked(Type0AElements.Total + (uint)values.Length);
                        Type0AElements.LeadingByteNotZero =
                            checked(Type0AElements.LeadingByteNotZero + leadBad);
                        Type0AElements.PadNotZero = checked(Type0AElements.PadNotZero + padBad);
                        foreach (var value in values)
                        {
                            HircBump(Type0AElements.ValueCounts, $"value_{value}", 1);
                        }
                    }
                    // The reference is an optional four-byte field: bodies that carry it
                    // have exactly four more bytes after the head than bodies that do
                    // not. Counting the tail length by outcome is what shows that.
                    HircBump(
                        Type0AHead.TailBytesByOutcome,
                        (everything.Contains(predicted)
                            && typeOf.TryGetValue(predicted, out var placed)
                            && placed == 11 ? "withReference_" : "withoutReference_") + tailBytes,
                        1);
                    if (discriminant
                        && everything.Contains(predicted)
                        && typeOf.TryGetValue(predicted, out var forFloat)
                        && forFloat == 11
                        && float.IsFinite(tailFloat))
                    {
                        Type0AHead.TailFloats = checked(Type0AHead.TailFloats + 1);
                        if (tailFloat >= Type0ATailFloatFloor && tailFloat <= Type0ATailFloatCeiling)
                        {
                            Type0AHead.TailFloatsInBand = checked(Type0AHead.TailFloatsInBand + 1);
                        }
                        if (tailFloat == MathF.Round(tailFloat))
                        {
                            Type0AHead.TailFloatsWhole = checked(Type0AHead.TailFloatsWhole + 1);
                        }
                        if (float.IsFinite(decibel)
                            && decibel >= Type0AHeadDecibelFloor
                            && decibel <= Type0AHeadDecibelCeiling)
                        {
                            Type0AHead.DecibelsInRange = checked(Type0AHead.DecibelsInRange + 1);
                            if (decibel == MathF.Round(decibel) && decibel != 0f)
                            {
                                Type0AHead.DecibelsWhole = checked(Type0AHead.DecibelsWhole + 1);
                            }
                        }
                        // The control must be judged on the same two properties, not
                        // just the range: a denormal near zero is trivially in range,
                        // so range alone accepts anything and proves nothing.
                        if (float.IsFinite(decibelControl)
                            && decibelControl >= Type0AHeadDecibelFloor
                            && decibelControl <= Type0AHeadDecibelCeiling
                            && decibelControl == MathF.Round(decibelControl)
                            && decibelControl != 0f)
                        {
                            Type0AHead.DecibelControlsInRange =
                                checked(Type0AHead.DecibelControlsInRange + 1);
                        }
                        Type0AHead.DecibelBodies = checked(Type0AHead.DecibelBodies + 1);
                        if (headWordFive != 0)
                        {
                            Type0AHead.WordFiveNonZero = checked(Type0AHead.WordFiveNonZero + 1);
                            HircBump(Type0AHead.WordFiveValues, $"word_{headWordFive:X8}", 1);
                            if (everything.Contains(headWordFive))
                            {
                                Type0AHead.WordFiveInPackage =
                                    checked(Type0AHead.WordFiveInPackage + 1);
                            }
                        }
                        if (fraction != 0)
                        {
                            Type0AHead.FractionCandidates =
                                checked(Type0AHead.FractionCandidates + 1);
                            if (IsSmallFraction(fraction))
                            {
                                Type0AHead.FractionsWithASmallDenominator =
                                    checked(Type0AHead.FractionsWithASmallDenominator + 1);
                            }
                        }
                        if (fractionControl != 0)
                        {
                            Type0AHead.FractionControls =
                                checked(Type0AHead.FractionControls + 1);
                            if (IsSmallFraction(fractionControl))
                            {
                                Type0AHead.FractionControlsWithASmallDenominator =
                                    checked(Type0AHead.FractionControlsWithASmallDenominator + 1);
                            }
                        }
                        if (float.IsFinite(neighbourFloat))
                        {
                            Type0AHead.NeighbourFloats = checked(Type0AHead.NeighbourFloats + 1);
                            if (neighbourFloat == MathF.Round(neighbourFloat))
                            {
                                Type0AHead.NeighbourFloatsWhole =
                                    checked(Type0AHead.NeighbourFloatsWhole + 1);
                            }
                        }
                    }
                    Score("predicted", predicted);
                    Score("fixedOffset", fixedWord);
                    Score("predictedPlusFour", plus);
                    Score("predictedMinusFour", minus);
                    if (discriminant)
                    {
                        HircBump(
                            Type0AHead.HeadWordTargets,
                            everything.Contains(headWord) && typeOf.TryGetValue(headWord, out var headType)
                                ? $"type{headType:X2}"
                                : "nothing",
                            1);
                    }
                }
            }
        }


        /// <summary>
        /// Score each constant in the shared framer against every rival value.
        /// </summary>
        /// <remarks>
        /// A constant that merely lets the bodies close is not established, and three
        /// of these four are not: several entry widths close every body that carries
        /// an entry run, three element widths close every body that carries a middle
        /// run, and every trailer length "closes" because a remainder that does not
        /// match simply routes the body to the tail block. What separates them is the
        /// zero trailer -- landing exactly on five bytes that are all zero.
        ///
        /// Two things make this a fair test rather than a flattering one.
        ///
        /// Each constant is scored only over the bodies that **exercise** it: the
        /// element width over bodies whose middle run is nonempty, the entry width
        /// over bodies whose entry run is nonempty. Scoring a width over bodies that
        /// declare a count of zero buries the margin under hundreds of bodies to which
        /// every candidate value is identical -- the same dilution that made a
        /// flat-tile corpus look like evidence for the terrain codec.
        ///
        /// And the per-candidate scores are published rather than reduced here, so the
        /// corpus gate can sum them across packages before comparing. A per-package
        /// maximum of the best rival is not the corpus-wide best rival.
        /// </remarks>
        private void ClassifySharedFrameConstants()
        {
            var bodies = new List<byte[]>();
            foreach (var structure in BnkStructures)
            {
                bodies.AddRange(structure.SharedBodies);
            }
            SharedConstants.Bodies = checked((uint)bodies.Count);
            foreach (var (name, chosen, first, last) in SharedFrameConstants)
            {
                HircBump(SharedConstants.Chosen, name, checked((uint)chosen));
                var exercising = new List<byte[]>();
                foreach (var body in bodies)
                {
                    if (ExercisesSharedConstant(body, name))
                    {
                        exercising.Add(body);
                    }
                }
                HircBump(SharedConstants.BodiesExercising, name, checked((uint)exercising.Count));
                for (var candidate = first; candidate <= last; candidate++)
                {
                    var zeroTrailer = 0U;
                    var closes = 0U;
                    foreach (var body in exercising)
                    {
                        var outcome = ProbeSharedFrame(
                            body,
                            name == "middleBlockBytes" ? candidate : SharedMiddleBlockBytes,
                            name == "middleRunElementBytes" ? candidate : SharedMiddleRunElementBytes,
                            name == "entryBytes" ? candidate : Type08EntryBytes,
                            name == "trailerBytes" ? candidate : Type08TrailerBytes);
                        if (outcome != SharedFrameProbe.NoClose)
                        {
                            closes = checked(closes + 1);
                        }
                        if (outcome == SharedFrameProbe.ClosesOnZeroTrailer)
                        {
                            zeroTrailer = checked(zeroTrailer + 1);
                        }
                    }
                    HircBump(SharedConstants.ZeroTrailerByCandidate, $"{name}_{candidate}", zeroTrailer);
                    HircBump(SharedConstants.ClosesByCandidate, $"{name}_{candidate}", closes);
                }
            }
        }

        /// <summary>
        /// Whether a body's own counts make a constant matter to how it frames.
        /// </summary>
        /// <remarks>
        /// A body that declares a middle run of zero frames identically under every
        /// element width, so counting it towards a width's score measures nothing but
        /// how many such bodies the corpus holds.
        /// </remarks>
        private static bool ExercisesSharedConstant(ReadOnlySpan<byte> body, string name)
        {
            if (name is "middleBlockBytes" or "trailerBytes")
            {
                return ProbeSharedFrame(
                    body,
                    SharedMiddleBlockBytes,
                    SharedMiddleRunElementBytes,
                    Type08EntryBytes,
                    Type08TrailerBytes) != SharedFrameProbe.NoClose;
            }
            if (!TryReadSharedCounts(body, out var middleRun, out var entryCount))
            {
                return false;
            }
            return name == "middleRunElementBytes" ? middleRun > 0 : entryCount > 0;
        }

        /// <summary>
        /// Read the two counts the shared body declares, under the chosen constants.
        /// </summary>
        private static bool TryReadSharedCounts(
            ReadOnlySpan<byte> body,
            out uint middleRun,
            out byte entryCount)
        {
            middleRun = 0;
            entryCount = 0;
            if (body.Length < 5)
            {
                return false;
            }
            var cursor = 4;
            if (BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)) == 0)
            {
                if (body.Length < 9)
                {
                    return false;
                }
                cursor = 8;
            }
            var propertyCount = body[cursor];
            cursor = checked(cursor + 1);
            if (propertyCount > (body.Length - cursor) / 5)
            {
                return false;
            }
            cursor = checked(cursor + propertyCount * 5);
            if (body.Length - cursor < 2)
            {
                return false;
            }
            var secondWidth = body[cursor + 1] switch
            {
                Type08SecondListShortKey => Type08SecondListShortBytes,
                Type08SecondListLongKey => Type08SecondListLongBytes,
                SharedSecondListKeyA => SharedSecondListKeyABytes,
                _ => -1,
            };
            if (secondWidth < 0)
            {
                return false;
            }
            cursor = checked(cursor + 2 + secondWidth + SharedMiddleBlockBytes);
            if (body.Length - cursor < 4)
            {
                return false;
            }
            middleRun = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            cursor = checked(cursor + 4);
            if (middleRun > SharedMiddleRunMaximum)
            {
                return false;
            }
            var middleSpan = checked((long)middleRun * SharedMiddleRunElementBytes);
            if (middleSpan > body.Length - cursor)
            {
                return false;
            }
            cursor = checked(cursor + (int)middleSpan);
            if (cursor >= body.Length)
            {
                return false;
            }
            entryCount = body[cursor];
            return true;
        }


        /// <summary>
        /// Walk the parent relation numeric types 0x08 and 0x12 declare, per bank.
        /// </summary>
        /// <remarks>
        /// This is the first structure in this format above the level of a byte
        /// layout, so it is measured rather than assumed. Each object's leading word
        /// names another object; following it gives a relation, and the questions
        /// worth asking of a relation are whether it is acyclic, how many roots each
        /// bank has, and whether the two types sit in different places in it.
        ///
        /// Built per bank on purpose. Object ids repeat across banks, and a graph
        /// built on the deduplicated ids merges trees that are not connected -- doing
        /// that made the corpus look like three trees over 278 objects instead of 121
        /// trees over 412.
        ///
        /// Nothing here says what the relation means. It says it is a forest, that
        /// almost every bank contributes exactly one tree, and that numeric type 0x12
        /// is a leaf in all but two of its 251 objects.
        /// </remarks>
        private void ClassifySharedHierarchy()
        {
            foreach (var structure in BnkStructures)
            {
                var objects = structure.SharedParents;
                if (objects.Count == 0)
                {
                    continue;
                }
                Hierarchy.Banks = checked(Hierarchy.Banks + 1);
                var children = new Dictionary<uint, uint>();
                foreach (var pair in objects)
                {
                    if (pair.Value.Parent != 0 && objects.ContainsKey(pair.Value.Parent))
                    {
                        children.TryGetValue(pair.Value.Parent, out var seen);
                        children[pair.Value.Parent] = checked(seen + 1);
                    }
                }
                var roots = 0U;
                foreach (var pair in objects)
                {
                    var (type, parent) = pair.Value;
                    Hierarchy.Objects = checked(Hierarchy.Objects + 1);
                    var inBank = parent != 0 && objects.ContainsKey(parent);
                    if (!inBank)
                    {
                        roots = checked(roots + 1);
                        // Two very different things, and conflating them cost a wrong
                        // gate: an object with NO parent is a root of the relation,
                        // while an object naming a parent that is simply not in this
                        // bank is a root only of this bank's fragment of it.
                        if (parent == 0)
                        {
                            Hierarchy.RootsWithNoParent = checked(Hierarchy.RootsWithNoParent + 1);
                            HircBump(Hierarchy.RootTypes, $"type{type:X2}", 1);
                        }
                        else
                        {
                            Hierarchy.RootsNamingOutsideTheBank =
                                checked(Hierarchy.RootsNamingOutsideTheBank + 1);
                            HircBump(Hierarchy.OutsideBankTypes, $"type{type:X2}", 1);
                        }
                    }
                    HircBump(
                        children.ContainsKey(pair.Key) ? Hierarchy.InternalTypes : Hierarchy.LeafTypes,
                        $"type{type:X2}",
                        1);

                    // Depth, and the cycle check that makes the depth meaningful.
                    var seenIds = new HashSet<uint>();
                    var cursor = pair.Key;
                    var depth = 0;
                    while (true)
                    {
                        var next = objects[cursor].Parent;
                        if (next == 0 || !objects.ContainsKey(next))
                        {
                            break;
                        }
                        if (!seenIds.Add(next))
                        {
                            Hierarchy.Cycles = checked(Hierarchy.Cycles + 1);
                            depth = -1;
                            break;
                        }
                        cursor = next;
                        depth = checked(depth + 1);
                    }
                    if (depth >= 0)
                    {
                        HircBump(Hierarchy.Depths, $"depth_{Math.Min(depth, 12)}", 1);
                    }
                }
                HircBump(Hierarchy.RootsPerBank, $"roots_{Math.Min(roots, 8)}", 1);
                // How many children each parent has. This is what separates this
                // relation from the main reference graph, which is the same shape
                // pointing the other way: there every target has exactly one referrer,
                // here a parent is named by many children at once.
                foreach (var pair in children)
                {
                    HircBump(Hierarchy.ChildrenPerParent, $"children_{Math.Min(pair.Value, 16)}", 1);
                    Hierarchy.ParentsWithSeveralChildren = pair.Value > 1
                        ? checked(Hierarchy.ParentsWithSeveralChildren + 1)
                        : Hierarchy.ParentsWithSeveralChildren;
                }
            }
        }


        /// <summary>
        /// Resolve the music types' references inside their own bank, and ask whether
        /// the relation is symmetric.
        /// </summary>
        /// <remarks>
        /// Two things this measures that the package-wide census cannot.
        ///
        /// **Scope.** Resolving a music body's words against the whole package counts
        /// 24,515 references; resolving them inside the bank the object lives in counts
        /// 12,372. Ids repeat across banks, so the wider scope credits an object with
        /// references to namesakes in banks it has nothing to do with.
        ///
        /// **Direction.** 74% of the same-bank edges whose *both* ends were scanned
        /// have their reverse present as well. That makes this relation unlike either
        /// of the other two in the format: the main reference graph is functional, with
        /// every target named exactly once, and the 0x08 parent relation has many
        /// children naming one parent. A mutual edge is neither, and cannot be read as
        /// parenthood in either direction.
        ///
        /// It is also not a clique. Neighbours of a node are linked to each other only
        /// 0.1% of the time, which rules out the reading that these bodies simply share
        /// a list of sibling ids.
        /// </remarks>
        private void ClassifyMusicMutuality()
        {
            foreach (var structure in BnkStructures)
            {
                if (structure.MusicSources.Count == 0)
                {
                    continue;
                }
                var here = new Dictionary<uint, byte>();
                foreach (var pair in structure.WalkObjectTypes)
                {
                    here[pair.Key] = pair.Value;
                }
                var scanned = new HashSet<uint>();
                foreach (var source in structure.MusicSources)
                {
                    scanned.Add(source.Id);
                }
                var edges = new HashSet<(uint From, uint To)>();
                foreach (var (id, _, words) in structure.MusicSources)
                {
                    foreach (var word in words)
                    {
                        if (word != id && here.ContainsKey(word))
                        {
                            edges.Add((id, word));
                        }
                    }
                }
                foreach (var edge in edges)
                {
                    MusicMutuality.SameBankEdges = checked(MusicMutuality.SameBankEdges + 1);
                    if (!scanned.Contains(edge.To))
                    {
                        // Only a scanned body can carry the reverse edge, so an edge
                        // into an unscanned object cannot be asked the question and is
                        // kept out of the rate rather than counted as one-way.
                        MusicMutuality.EdgesIntoUnscannedObjects =
                            checked(MusicMutuality.EdgesIntoUnscannedObjects + 1);
                        continue;
                    }
                    MusicMutuality.EdgesBetweenScannedObjects =
                        checked(MusicMutuality.EdgesBetweenScannedObjects + 1);
                    if (edges.Contains((edge.To, edge.From)))
                    {
                        MusicMutuality.MutualEdges = checked(MusicMutuality.MutualEdges + 1);
                        HircBump(
                            MusicMutuality.MutualEdgeKinds,
                            $"type{here[edge.From]:X2}_with_type{here[edge.To]:X2}",
                            1);
                    }
                }
            }
        }


        /// <summary>
        /// Score numeric type 0x0A's end anchor against its neighbouring distances,
        /// and say how much of the type the two rules together account for.
        /// </summary>
        /// <remarks>
        /// The combined accounting is the point. The head rule places 3,875 of the
        /// 4,158 bodies; the anchor places 25 more that it could not reach; 255 carry
        /// no reference at all, which the tail length explains as an absent optional
        /// field. That leaves **three** bodies with a reference neither rule finds.
        ///
        /// The anchor's own evidence is the control, not the hit count. A fixed
        /// distance from the end will land on *something* in every body; what makes
        /// -69 a rule is that the ten distances around it land on an object id in no
        /// body at all.
        /// </remarks>
        private void ClassifyType0AEndAnchor()
        {
            var typeOf = new Dictionary<uint, byte>();
            foreach (var structure in BnkStructures)
            {
                foreach (var pair in structure.WalkObjectTypes)
                {
                    typeOf[pair.Key] = pair.Value;
                }
            }
            foreach (var structure in BnkStructures)
            {
                foreach (var words in structure.Type0AEndAnchors)
                {
                    Type0AEndAnchor.Bodies = checked(Type0AEndAnchor.Bodies + 1);
                    for (var i = 0; i < words.Length; i++)
                    {
                        var distance = Type0AEndAnchorDistance
                            + (i - Type0AEndAnchorControlSpan);
                        if (words[i] == 0
                            || !typeOf.TryGetValue(words[i], out var target)
                            || target != 11)
                        {
                            continue;
                        }
                        if (Array.IndexOf(Type0AEndAnchorDistances, distance) >= 0)
                        {
                            Type0AEndAnchor.AnchorNamesTheTargetType =
                                checked(Type0AEndAnchor.AnchorNamesTheTargetType + 1);
                            HircBump(Type0AEndAnchor.AnchorHits, $"minus_{distance}", 1);
                        }
                        else
                        {
                            Type0AEndAnchor.ControlsNameTheTargetType =
                                checked(Type0AEndAnchor.ControlsNameTheTargetType + 1);
                            HircBump(Type0AEndAnchor.ControlHits, $"minus_{distance}", 1);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Walk the music types' parent relation, read at a fixed front offset.
        /// </summary>
        /// <remarks>
        /// The same shape as the relation numeric types 0x08 and 0x12 declare, in an
        /// unrelated family of types and at a different offset: a forest, no cycles,
        /// depth running to 7, and many children naming one parent rather than one
        /// referrer per target. That the pattern recurs is the finding; what it means
        /// is still not claimed.
        ///
        /// Found by asking which **front** offsets this type's references use. The
        /// census measures distance from the end, and from the end 0x0C looks entirely
        /// unlocated -- 3,757 distinct distances for 9,634 references. From the front
        /// it is 3,193 distinct offsets, equally scattered, **except** that one offset
        /// carries 716 of them.
        /// </remarks>
        private void ClassifyType0CHierarchy()
        {
            foreach (var structure in BnkStructures)
            {
                var objects = structure.Type0CParents;
                if (objects.Count == 0)
                {
                    continue;
                }
                Type0CHierarchy.Banks = checked(Type0CHierarchy.Banks + 1);
                var children = new Dictionary<uint, uint>();
                foreach (var pair in objects)
                {
                    if (pair.Value.Parent != 0 && objects.ContainsKey(pair.Value.Parent))
                    {
                        children.TryGetValue(pair.Value.Parent, out var seen);
                        children[pair.Value.Parent] = checked(seen + 1);
                    }
                }
                foreach (var pair in objects)
                {
                    Type0CHierarchy.Objects = checked(Type0CHierarchy.Objects + 1);
                    if (pair.Value.Parent != 0 && objects.ContainsKey(pair.Value.Parent))
                    {
                        Type0CHierarchy.ObjectsNamingAParent =
                            checked(Type0CHierarchy.ObjectsNamingAParent + 1);
                        HircBump(
                            Type0CHierarchy.EdgeTypes,
                            $"type{pair.Value.Type:X2}_to_type{objects[pair.Value.Parent].Type:X2}",
                            1);
                    }
                    else if (pair.Value.Parent == 0)
                    {
                        Type0CHierarchy.RootsWithNoParent =
                            checked(Type0CHierarchy.RootsWithNoParent + 1);
                    }
                    else
                    {
                        Type0CHierarchy.ParentsOutsideTheBank =
                            checked(Type0CHierarchy.ParentsOutsideTheBank + 1);
                    }
                    var seenIds = new HashSet<uint>();
                    var cursor = pair.Key;
                    var depth = 0;
                    while (true)
                    {
                        if (!objects.TryGetValue(cursor, out var next)
                            || next.Parent == 0
                            || !objects.ContainsKey(next.Parent))
                        {
                            break;
                        }
                        if (!seenIds.Add(next.Parent))
                        {
                            Type0CHierarchy.Cycles = checked(Type0CHierarchy.Cycles + 1);
                            depth = -1;
                            break;
                        }
                        cursor = next.Parent;
                        depth = checked(depth + 1);
                    }
                    if (depth >= 0)
                    {
                        HircBump(Type0CHierarchy.Depths, $"depth_{Math.Min(depth, 12)}", 1);
                    }
                }
                foreach (var pair in children)
                {
                    HircBump(
                        Type0CHierarchy.ChildrenPerParent,
                        $"children_{Math.Min(pair.Value, 16)}",
                        1);
                    if (pair.Value > 1)
                    {
                        Type0CHierarchy.ParentsWithSeveralChildren =
                            checked(Type0CHierarchy.ParentsWithSeveralChildren + 1);
                    }
                }
            }
        }


        /// <summary>
        /// Numeric type 0x0A's reference to 0x0B is a counted array, not a lone word.
        /// </summary>
        /// <remarks>
        /// The word immediately before the first reference is a count, and it equals
        /// the number of references that follow it four bytes apart, in **every** one
        /// of the 3,903 bodies that carry a reference at all. Counts are 1 in 3,565
        /// bodies, 2 in 271, 3 in 58 and 4 in 9.
        ///
        /// This supersedes both earlier readings rather than competing with them. The
        /// three-branch head rule and the two end anchors were each locating the first
        /// element of this array; where they disagreed with each other, or reached
        /// nothing, the array was simply longer or shorter than one. With it, numeric
        /// type 0x0A has **no unexplained bodies**: 3,903 counted arrays plus 255
        /// carrying no reference is its whole population of 4,158.
        ///
        /// The array is found here by locating a reference and stepping back four
        /// bytes, which is a reading rather than a forward frame. What makes it
        /// evidence is that the count agrees with the run length every time -- a wrong
        /// step-back would give a number unrelated to how many references follow.
        /// </remarks>
        private void ClassifyType0ACountedArray()
        {
            var targets = new HashSet<uint>();
            foreach (var structure in BnkStructures)
            {
                foreach (var pair in structure.WalkObjectTypes)
                {
                    if (pair.Value == 11)
                    {
                        targets.Add(pair.Key);
                    }
                }
            }
            foreach (var structure in BnkStructures)
            {
                foreach (var (owner, body) in structure.Type0ABodies)
                {
                    Type0ACountedArray.Bodies = checked(Type0ACountedArray.Bodies + 1);
                    var first = -1;
                    for (var at = 0; at + 4 <= body.Length; at++)
                    {
                        if (targets.Contains(BinaryPrimitives.ReadUInt32LittleEndian(
                                body.AsSpan(at, 4))))
                        {
                            first = at;
                            break;
                        }
                    }
                    if (first < 0)
                    {
                        Type0ACountedArray.BodiesWithNoReference =
                            checked(Type0ACountedArray.BodiesWithNoReference + 1);
                        continue;
                    }
                    if (first < 4)
                    {
                        Type0ACountedArray.NoRoomForACount =
                            checked(Type0ACountedArray.NoRoomForACount + 1);
                        continue;
                    }
                    var run = 1;
                    while (first + 4 * run + 4 <= body.Length
                        && targets.Contains(BinaryPrimitives.ReadUInt32LittleEndian(
                            body.AsSpan(first + 4 * run, 4))))
                    {
                        run = checked(run + 1);
                    }
                    var declared = BinaryPrimitives.ReadUInt32LittleEndian(
                        body.AsSpan(first - 4, 4));
                    Type0ACountedArray.Checkable = checked(Type0ACountedArray.Checkable + 1);
                    if (declared == (uint)run)
                    {
                        if (!structure.MusicEdges.TryGetValue(owner, out var owned))
                        {
                            owned = new List<uint>();
                            structure.MusicEdges[owner] = owned;
                        }
                        for (var step = 0; step < run; step++)
                        {
                            owned.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                                body.AsSpan(first + 4 * step, 4)));
                        }
                        Type0ACountedArray.CountMatchesTheRun =
                            checked(Type0ACountedArray.CountMatchesTheRun + 1);
                        HircBump(Type0ACountedArray.RunLengths, $"references_{Math.Min(run, 8)}", 1);
                    }
                    else
                    {
                        Type0ACountedArray.CountDoesNotMatch =
                            checked(Type0ACountedArray.CountDoesNotMatch + 1);
                    }
                }
            }
        }


        /// <summary>
        /// Census numeric type 0x0C's counted reference array.
        /// </summary>
        /// <remarks>
        /// The count sits at `32 + 5 * body[14]` and is followed by that many 32-bit
        /// object ids. Over the type's 742 bodies, 721 have an array in which **every**
        /// entry resolves to an object in the same bank, with lengths spread from 1 to
        /// 9 and beyond rather than piling on one value.
        ///
        /// Two separate discriminations, because the base and the step are settled by
        /// different evidence. The base is settled by resolution: at 32 every array
        /// resolves and at 30, 31, 33, 34, 36 and 40 essentially none does. The step
        /// is settled by coverage: a step of 5 finds an array in 704 bodies where 0, 4,
        /// 6 and 8 find one in 564, and the 140 extra are exactly the bodies whose
        /// selector byte is nonzero. A step that did not match the data would not
        /// reach more bodies, it would reach the same ones and fail on them.
        ///
        /// The array is not everything this type references -- most bodies carry more
        /// ids after it -- so this locates one field rather than framing the type.
        /// </remarks>
        private static void CensusType0CArray(
            EndfieldHircType0CArrayCensus census,
            ReadOnlySpan<byte> body,
            Dictionary<uint, byte> bankObjectTypes,
            Dictionary<uint, List<uint>> musicEdges,
            uint owner)
        {
            census.Bodies = checked(census.Bodies + 1);
            if (body.Length <= Type0CArraySelectorOffset)
            {
                census.TooShort = checked(census.TooShort + 1);
                return;
            }
            var selector = body[Type0CArraySelectorOffset];
            if (selector > Type0CArrayMaximumSelector)
            {
                census.SelectorOutOfRange = checked(census.SelectorOutOfRange + 1);
                return;
            }
            HircBump(census.SelectorValues, $"selector_{selector}", 1);
            var at = checked(Type0CArrayBaseOffset + Type0CArrayStepBytes * selector);
            if (at + 4 > body.Length)
            {
                census.CountPastTheEnd = checked(census.CountPastTheEnd + 1);
                return;
            }
            var count = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(at, 4));
            if (count == 0 || count > Type0CArrayMaximumLength)
            {
                census.CountOutOfRange = checked(census.CountOutOfRange + 1);
                return;
            }
            var start = checked(at + 4);
            if (checked(start + 4 * (int)count) > body.Length)
            {
                census.ArrayPastTheEnd = checked(census.ArrayPastTheEnd + 1);
                return;
            }
            census.ArraysTested = checked(census.ArraysTested + 1);
            var resolved = true;
            for (var i = 0; i < count; i++)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(
                    body.Slice(checked(start + 4 * i), 4));
                if (!bankObjectTypes.TryGetValue(id, out var target))
                {
                    resolved = false;
                    break;
                }
                HircBump(census.TargetTypes, $"type{target:X2}", 1);
                if (!musicEdges.TryGetValue(owner, out var owned))
                {
                    owned = new List<uint>();
                    musicEdges[owner] = owned;
                }
                owned.Add(id);
            }
            if (resolved)
            {
                census.ArraysFullyResolving = checked(census.ArraysFullyResolving + 1);
                HircBump(census.ArrayLengths, $"entries_{Math.Min(count, 9)}", 1);
            }

            // The region after the array, and the flag that lengthens it.
            if (resolved)
            {
                var regionStart = checked(start + 4 * (int)count);
                var flagAt = checked(regionStart + Type0CRegionFlagOffset);
                if (flagAt < body.Length)
                {
                    var flag = body[flagAt];
                    HircBump(census.RegionFlags, $"flag_{Math.Min((int)flag, 9)}", 1);
                    var next = -1;
                    for (var o = regionStart; o + 4 <= body.Length; o++)
                    {
                        if (bankObjectTypes.ContainsKey(BinaryPrimitives.ReadUInt32LittleEndian(
                                body.Slice(o, 4))))
                        {
                            next = o;
                            break;
                        }
                    }
                    if (next >= 0)
                    {
                        if (flag <= Type0CRegionMaximumFlag)
                        {
                            census.RegionGapsTested = checked(census.RegionGapsTested + 1);
                            var want = checked(Type0CRegionBaseGap + Type0CRegionBlockBytes * flag);
                            if (next - regionStart == want)
                            {
                                census.RegionGapsPredicted =
                                    checked(census.RegionGapsPredicted + 1);
                            }
                        }
                        else
                        {
                            census.RegionFlagAboveTheObservedRange =
                                checked(census.RegionFlagAboveTheObservedRange + 1);
                        }
                    }
                }
            }

            // Every rival base, scored the same way, so the offset is discriminated
            // rather than asserted.
            foreach (var rival in Type0CArrayRivalBases)
            {
                var ra = checked(rival + Type0CArrayStepBytes * selector);
                if (ra + 4 > body.Length)
                {
                    continue;
                }
                // Counted as attempted here, before the plausibility checks, so that a
                // rival failing to produce even a usable count registers as a failure
                // rather than as "not tested". Every rival base fails at exactly this
                // point, which is a stronger result than one that fails later.
                census.RivalArraysTested = checked(census.RivalArraysTested + 1);
                var rc = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(ra, 4));
                if (rc == 0 || rc > Type0CArrayMaximumLength
                    || checked(ra + 4 + 4 * (int)rc) > body.Length)
                {
                    continue;
                }
                var ok = true;
                for (var i = 0; i < rc; i++)
                {
                    if (!bankObjectTypes.ContainsKey(BinaryPrimitives.ReadUInt32LittleEndian(
                            body.Slice(checked(ra + 4 + 4 * i), 4))))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                {
                    census.RivalArraysFullyResolving =
                        checked(census.RivalArraysFullyResolving + 1);
                }
            }
        }


        /// <summary>
        /// Walk the music family downward from the only edges that enter it from outside.
        /// </summary>
        /// <remarks>
        /// The music types are their own component. Across the whole corpus the main
        /// reference graph's 230,247 edges carry no music type at either end, and the
        /// parent field's 199,445 carry none either. The only edges from outside are
        /// action target words: of 23,455 that resolve to an object, **8** land on
        /// numeric type 0x0C and none at all on 0x0A, 0x0B or 0x0D.
        ///
        /// So this walk starts at those 8 and follows only downward music edges that
        /// are already gated -- numeric type 0x0C's counted array at `32 + 5 * body[14]`
        /// and numeric type 0x0A's counted array of 0x0B references. It reports what
        /// fraction of the family, and of the media the family owns, those 8 reach.
        ///
        /// What it does not establish: that firing those actions plays anything, or
        /// that the music family has no other entry point. It establishes that no
        /// OTHER entry point exists among the relations this reader has resolved,
        /// which is a smaller claim and the only one the bytes support.
        /// </remarks>
        private void WalkMusicFromActions()
        {
            var musicTypes = new HashSet<byte> { 0x0A, 0x0B, 0x0C, 0x0D };
            foreach (var structure in BnkStructures)
            {
                var types = structure.WalkObjectTypes;
                foreach (var pair in types)
                {
                    if (musicTypes.Contains(pair.Value))
                    {
                        MusicReach.MusicObjects = checked(MusicReach.MusicObjects + 1);
                    }
                }
                // The walk's own edges, censused so that "reached nothing" can be told
                // apart from "had nothing to follow".
                var incoming = new HashSet<uint>();
                foreach (var edge in structure.MusicEdges)
                {
                    MusicReach.EdgeSources = checked(MusicReach.EdgeSources + 1);
                    MusicReach.Edges = checked(MusicReach.Edges + (uint)edge.Value.Count);
                    foreach (var child in edge.Value)
                    {
                        incoming.Add(child);
                    }
                }
                foreach (var pair in types)
                {
                    if (musicTypes.Contains(pair.Value) && !incoming.Contains(pair.Key))
                    {
                        MusicReach.RootsWithNoIncomingEdge =
                            checked(MusicReach.RootsWithNoIncomingEdge + 1);
                        HircBump(MusicReach.RootTypes, $"type{pair.Value:X2}", 1);
                    }
                }
                var entries = new List<uint>();
                foreach (var (action, target) in structure.Type03Targets)
                {
                    if (target != 0 && types.TryGetValue(target, out var targetType)
                        && musicTypes.Contains(targetType))
                    {
                        MusicReach.EntryEdges = checked(MusicReach.EntryEdges + 1);
                        HircBump(
                            MusicReach.EntryKinds,
                            $"action_{action:X2}_to_type{targetType:X2}",
                            1);
                        entries.Add(target);
                        if (structure.MusicEdges.ContainsKey(target))
                        {
                            MusicReach.EntriesWithOutgoingEdges =
                                checked(MusicReach.EntriesWithOutgoingEdges + 1);
                        }
                        if (!incoming.Contains(target))
                        {
                            MusicReach.EntriesThatAreRoots =
                                checked(MusicReach.EntriesThatAreRoots + 1);
                        }
                    }
                }
                if (entries.Count == 0)
                {
                    continue;
                }
                MusicReach.BanksWithAnEntry = checked(MusicReach.BanksWithAnEntry + 1);
                var seen = new HashSet<uint>();
                var queue = new Queue<uint>(entries);
                foreach (var entry in entries)
                {
                    seen.Add(entry);
                }
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (types.TryGetValue(current, out var currentType))
                    {
                        HircBump(MusicReach.ReachedTypes, $"type{currentType:X2}", 1);
                    }
                    if (structure.WalkSourceIds.TryGetValue(current, out var owned))
                    {
                        foreach (var sourceId in owned)
                        {
                            MusicReach.ReachedSourceIds.Add(sourceId);
                        }
                    }
                    if (!structure.MusicEdges.TryGetValue(current, out var next))
                    {
                        continue;
                    }
                    foreach (var child in next)
                    {
                        if (seen.Add(child))
                        {
                            queue.Enqueue(child);
                        }
                    }
                }
                MusicReach.ReachedObjects =
                    checked(MusicReach.ReachedObjects + (uint)seen.Count);
            }
            MusicReach.ReachedSourceIdCount =
                checked((uint)MusicReach.ReachedSourceIds.Count);
        }


        /// <summary>
        /// Does the STMG section name HIRC objects, and of which types?
        /// </summary>
        /// <remarks>
        /// STMG appears exactly once in the whole corpus, 10,118 bytes, and nothing
        /// reads it. It is the only section left that could carry the edges the object
        /// graph does not have.
        ///
        /// The test slides a 32-bit window over every byte offset and asks which
        /// values are HIRC object ids. That is deliberately indiscriminate: the point
        /// is to find out whether STMG references objects at all before deciding what
        /// its records are. The chance rate is computable rather than a matter of
        /// taste -- 10,115 draws against 323,034 objects over a 32-bit space is 0.76
        /// expected matches -- so a handful of hits is already a result, and hits
        /// landing at a fixed stride are decisive.
        ///
        /// Unaligned offsets are included on purpose. The source id inside the
        /// 14-byte source record sits at +5, which is not four-byte aligned, and
        /// testing only aligned words is exactly how that field stayed unidentified.
        /// </remarks>
        // STMG's leading block. One instance in the whole corpus, so closure is not
        // available as evidence and none is claimed from it.
        internal const int StmgHeaderBytes = 14;
        internal const int StmgCountOffset = 10;
        internal const int StmgRecordBytes = 12;
        internal const int StmgRecordValueOffset = 4;
        internal const uint StmgMaximumRecords = 4096;
        // The stride is discriminated by two checks that do not depend on the section
        // closing. At 12 the 309 record ids are ALL distinct; every other stride from
        // 8 to 20 collapses them to between 107 and 220. And at 12 the word directly
        // after the run is 15 -- a small count that frames a further block -- where
        // every other stride lands on zero or on an arbitrary large value.
        // INIT is a plugin name table, and it is self-describing: a u32 count, then
        // that many entries of `u16 company, u16 plugin, NUL-terminated ASCII name`.
        // It closes byte-exactly at 347 bytes over 22 entries, which is the whole
        // check -- a wrong field order would not land on the section end.
        //
        // What makes it worth parsing is the join -- which is NOT done here. INIT lives
        // in init_banks.pck and the 147,262 source records live in default_banks.pck,
        // so a package-local join scores 1. This is the third field in this format whose
        // two sides sit in different packages, after the media ids and the source ids.
        // *When a table and its users are in separate files, the join belongs in the
        // pass that already unions the files.*
        //
        // The plugin id word at the front of
        // the 14-byte source record decomposes as `(plugin << 16) | company`, and every
        // company-2 value the corpus carries is named here: 0x00640002 AkSineTone,
        // 0x00650002 AkSilenceGenerator, 0x00940002 AkSynthOne, 0x01990002 AkMotion.
        // The company-1 values are not, and that is not a failure: INIT lists plugin
        // DLLs, and company 1 is the built-in codec set.
        internal const int InitEntryHeadBytes = 4;
        internal const uint InitMaximumEntries = 1024;
        internal const int InitMaximumNameBytes = 256;
        // ENVS is a run of curves, and its point is the SAME 12-byte record numeric
        // type 0x0B carries: `f32 x, f32 y, u32 interpolation`.
        //
        // Three shapes consume the section exactly with every curve non-empty; the
        // content separates them. At head 4 with the count at +2 and a 12-byte point,
        // all 16 interpolation codes are 0 to 9, x is non-decreasing in all 6 curves,
        // and all 16 floats are bounded. The nearest rival -- head 12, count at +2,
        // 18-byte record -- puts 3 of 10 codes in range and orders 1 of 3 curves.
        internal const int EnvsCurveHeadBytes = 4;
        internal const int EnvsCurveCountOffset = 2;
        internal const int EnvsPointBytes = 12;
        internal const uint EnvsMaximumInterpolation = 9;
        internal const int EnvsMaximumCurves = 256;

        // PLAT is a single NUL-terminated platform name.
        internal const int PlatMaximumBytes = 256;

        // STMG's middle block: a counted run of variable-length entries.
        //
        // One STMG in the corpus, but FIFTEEN entries inside this block, and they must
        // consume it exactly. Searching every shape of the form "head bytes with a
        // u32 count inside, then n records, then a tail" over head 4..40, count offset
        // 0..head-4, record 1..32 and tail 0..16 -- about 745,000 candidates -- exactly
        // ONE lands on the block's end in exactly 15 entries.
        //
        // The content then agrees independently: all 15 entry ids are distinct, the
        // head's bytes at +8 and +10..+12 are zero in every entry, and the record's
        // byte at +8 is 9 in all 45 records with +9..+11 zero.
        internal const int StmgEntryHeadBytes = 13;
        internal const int StmgEntryCountOffset = 9;
        internal const int StmgEntryRecordBytes = 12;
        internal const uint StmgMaximumEntryRecords = 64;
        internal const int StmgEntryRecordMarkerOffset = 8;
        internal const byte StmgEntryRecordMarker = 9;

        // STMG's trailing run, framed BACKWARD from the section end.
        //
        // It has to be backward. The block between the two runs is variable-length and
        // is not framed, so the trailing run's start cannot be computed forward. What
        // makes the backward walk evidence rather than a guess is that it ends on a
        // count: step back in 21-byte records while the record shape holds, then read
        // the word in front of the run and require it to equal the number of records
        // stepped over. A walk that overshoots into the unframed block lands on a word
        // that does not match, and is refused.
        internal const int StmgTailRecordBytes = 21;
        internal const int StmgTailZeroRunOffset = 9;
        internal const int StmgTailZeroRunBytes = 3;
        internal const int StmgTrailingBytes = 4;
        internal const uint StmgMaximumTailRecords = 4096;
        internal static readonly int[] StmgTailRivalStrides =
            { 14, 15, 16, 17, 18, 19, 20, 22, 23, 24 };
        internal static readonly int[] StmgRivalStrides =
            { 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20 };

        /// <summary>
        /// Frame the STMG section's header and its first record run.
        /// </summary>
        /// <remarks>
        /// STMG appears exactly once across the corpus at 10,118 bytes. Only the
        /// leading block is framed: a 14-byte header whose last word is a count, then
        /// that many 12-byte records of `u32 id, u16 value, 6 bytes`. The 309 records
        /// carry 309 distinct ids and a value that is 1,000 in 306 of them, 500 in one
        /// and 0 in one.
        ///
        /// The remaining 6,392 bytes begin with another count and are NOT framed. They
        /// are reported as unframed rather than guessed at, because with one instance
        /// there is nothing to check a guess against.
        /// </remarks>
        private void FrameStmgSections()
        {
            foreach (var structure in BnkStructures)
            {
                foreach (var section in structure.Sections)
                {
                    if (section.Body is null || section.Tag != "STMG")
                    {
                        continue;
                    }
                    Stmg.Sections = checked(Stmg.Sections + 1);
                    var body = section.Body;
                    Stmg.SectionBytes = checked(Stmg.SectionBytes + (uint)body.Length);
                    if (body.Length < StmgHeaderBytes)
                    {
                        Stmg.SectionsTooShort = checked(Stmg.SectionsTooShort + 1);
                        continue;
                    }
                    var declared = BinaryPrimitives.ReadUInt32LittleEndian(
                        body.AsSpan(StmgCountOffset, 4));
                    if (declared == 0 || declared > StmgMaximumRecords)
                    {
                        Stmg.CountOutOfRange = checked(Stmg.CountOutOfRange + 1);
                        continue;
                    }
                    var span = checked((int)declared * StmgRecordBytes);
                    if (checked(StmgHeaderBytes + span) > body.Length)
                    {
                        Stmg.RunPastTheEnd = checked(Stmg.RunPastTheEnd + 1);
                        continue;
                    }
                    Stmg.SectionsFramed = checked(Stmg.SectionsFramed + 1);
                    Stmg.DeclaredRecords = checked(Stmg.DeclaredRecords + declared);
                    var ids = new HashSet<uint>();
                    for (var record = 0U; record < declared; record++)
                    {
                        var at = checked(StmgHeaderBytes + (int)record * StmgRecordBytes);
                        ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at, 4)));
                        HircBump(
                            Stmg.RecordValues,
                            $"value_{BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + StmgRecordValueOffset, 2))}",
                            1);
                    }
                    Stmg.DistinctRecordIds = checked(Stmg.DistinctRecordIds + (uint)ids.Count);
                    // Every rival stride scored the same way, so the width is
                    // discriminated rather than asserted from a tidy-looking dump.
                    foreach (var stride in StmgRivalStrides)
                    {
                        var rivalSpan = checked((int)declared * stride);
                        if (checked(StmgHeaderBytes + rivalSpan) + 4 > body.Length)
                        {
                            continue;
                        }
                        Stmg.RivalStridesTested = checked(Stmg.RivalStridesTested + 1);
                        var rivalIds = new HashSet<uint>();
                        for (var record = 0U; record < declared; record++)
                        {
                            rivalIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                                body.AsSpan(checked(StmgHeaderBytes + (int)record * stride), 4)));
                        }
                        if (rivalIds.Count == declared)
                        {
                            Stmg.RivalStridesWithDistinctIds =
                                checked(Stmg.RivalStridesWithDistinctIds + 1);
                        }
                    }
                    var after = checked(StmgHeaderBytes + span);
                    if (after + 4 <= body.Length)
                    {
                        var next = BinaryPrimitives.ReadUInt32LittleEndian(
                            body.AsSpan(after, 4));
                        if (next > 0 && next <= StmgMaximumRecords)
                        {
                            Stmg.RunsFollowedByAPlausibleCount =
                                checked(Stmg.RunsFollowedByAPlausibleCount + 1);
                        }
                    }
                    var tailStart = FrameStmgTail(body, after);
                    var middleFramed = tailStart >= 0
                        && FrameStmgEntries(body, after, tailStart - 4);
                    var middleBytes = tailStart < 0 ? 0 : tailStart - 4 - after;
                    Stmg.BytesFramed = checked(
                        Stmg.BytesFramed
                        + (uint)after
                        + (uint)(tailStart < 0 ? 0 : body.Length - tailStart + 4)
                        + (uint)(middleFramed ? middleBytes : 0));
                    Stmg.BytesUnframed = checked(
                        Stmg.BytesUnframed
                        + (uint)(tailStart < 0 ? body.Length - after : 0)
                        + (uint)(tailStart >= 0 && !middleFramed ? middleBytes : 0));
                }
            }
        }


        /// <summary>
        /// Frame STMG's trailing record run, backward from the section end.
        /// </summary>
        /// <remarks>
        /// 269 records of 21 bytes: `u32 id, f32, u8 selector, 3 zero bytes, f32, f32,
        /// u8`. All 269 ids are distinct and all 269 carry the three zero bytes, while
        /// every rival stride from 14 to 24 gives between 108 and 133 distinct ids and
        /// between 126 and 162 records with the zero run. The three floats are finite
        /// and bounded, taking values like 0, -96, 1, 0.1, 10, 50 and 16000.
        ///
        /// Returns the offset the run starts at, or -1 if it could not be framed.
        /// </remarks>
        private int FrameStmgTail(byte[] body, int notBefore)
        {
            if (body.Length < StmgTrailingBytes)
            {
                return -1;
            }
            for (var at = body.Length - StmgTrailingBytes; at < body.Length; at++)
            {
                if (body[at] != 0)
                {
                    Stmg.TailTrailingBytesNotZero =
                        checked(Stmg.TailTrailingBytesNotZero + 1);
                    return -1;
                }
            }
            var cursor = body.Length - StmgTrailingBytes;
            var records = 0U;
            while (checked(cursor - StmgTailRecordBytes) >= notBefore
                && records < StmgMaximumTailRecords)
            {
                var candidate = cursor - StmgTailRecordBytes;
                var zeroed = true;
                for (var at = 0; at < StmgTailZeroRunBytes; at++)
                {
                    if (body[candidate + StmgTailZeroRunOffset + at] != 0)
                    {
                        zeroed = false;
                        break;
                    }
                }
                if (!zeroed)
                {
                    break;
                }
                cursor = candidate;
                records = checked(records + 1);
            }
            if (records == 0 || cursor - 4 < notBefore)
            {
                Stmg.TailRunNotFound = checked(Stmg.TailRunNotFound + 1);
                return -1;
            }
            // The zero-run walk BOUNDS the run; it does not locate it. Here it reaches
            // 270 records where the true count is 269, because the three bytes it looks
            // at happen to be zero one record early. So the boundary is settled by the
            // count instead: the only length whose preceding word equals it.
            //
            // That is a real discrimination rather than a fit. Over every length from 1
            // to 480 exactly ONE satisfies it, so the run is located to the byte and an
            // ambiguous section is fenced rather than resolved by preference.
            var matches = 0U;
            var chosen = 0U;
            for (var candidate = 1U; candidate <= records; candidate++)
            {
                var at = checked(body.Length - StmgTrailingBytes
                                 - (int)candidate * StmgTailRecordBytes - 4);
                if (at < notBefore)
                {
                    break;
                }
                if (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at, 4)) == candidate)
                {
                    matches = checked(matches + 1);
                    chosen = candidate;
                }
            }
            Stmg.TailRunsCheckable = checked(Stmg.TailRunsCheckable + 1);
            if (matches != 1)
            {
                Stmg.TailCountDoesNotMatchTheRun =
                    checked(Stmg.TailCountDoesNotMatchTheRun + 1);
                return -1;
            }
            records = chosen;
            cursor = checked(body.Length - StmgTrailingBytes
                             - (int)records * StmgTailRecordBytes);
            Stmg.TailRunsFramed = checked(Stmg.TailRunsFramed + 1);
            Stmg.TailRecords = checked(Stmg.TailRecords + records);
            var ids = new HashSet<uint>();
            for (var record = 0U; record < records; record++)
            {
                var at = checked(cursor + (int)record * StmgTailRecordBytes);
                ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at, 4)));
                HircBump(Stmg.TailSelectors, $"selector_{body[at + 8]}", 1);
                for (var field = 0; field < 3; field++)
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(
                        body.AsSpan(at + (field == 0 ? 4 : 8 + field * 4), 4));
                    Stmg.TailFloatsTested = checked(Stmg.TailFloatsTested + 1);
                    if (!float.IsNaN(value) && !float.IsInfinity(value)
                        && Math.Abs(value) < 1e7f)
                    {
                        Stmg.TailFloatsBounded = checked(Stmg.TailFloatsBounded + 1);
                    }
                }
            }
            Stmg.TailDistinctIds = checked(Stmg.TailDistinctIds + (uint)ids.Count);
            // Every rival stride scored the same two ways, over the same span.
            foreach (var stride in StmgTailRivalStrides)
            {
                if (checked(cursor + (int)records * stride) > body.Length)
                {
                    continue;
                }
                Stmg.TailRivalStridesTested = checked(Stmg.TailRivalStridesTested + 1);
                var rivalIds = new HashSet<uint>();
                var rivalZeroed = 0U;
                for (var record = 0U; record < records; record++)
                {
                    var at = checked(cursor + (int)record * stride);
                    rivalIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                        body.AsSpan(at, 4)));
                    if (body.AsSpan(at + StmgTailZeroRunOffset, StmgTailZeroRunBytes)
                            .IndexOfAnyExcept((byte)0) < 0)
                    {
                        rivalZeroed = checked(rivalZeroed + 1);
                    }
                }
                if (rivalIds.Count == records)
                {
                    Stmg.TailRivalStridesWithDistinctIds =
                        checked(Stmg.TailRivalStridesWithDistinctIds + 1);
                }
                if (rivalZeroed == records)
                {
                    Stmg.TailRivalStridesWithTheZeroRun =
                        checked(Stmg.TailRivalStridesWithTheZeroRun + 1);
                }
            }
            return cursor;
        }


        /// <summary>
        /// Frame the counted run of variable-length entries between STMG's two arrays.
        /// </summary>
        /// <remarks>
        /// `u32 count`, then that many entries of `u32, u32, u8 zero, u32 count` followed
        /// by that many 12-byte records. With the leading and trailing runs already
        /// located this block is bounded on both sides, so the frame has to consume it
        /// byte-exactly or be refused -- which is the strongest check available for a
        /// section that appears once.
        /// </remarks>
        private bool FrameStmgEntries(byte[] body, int start, int end)
        {
            Stmg.EntryBlocks = checked(Stmg.EntryBlocks + 1);
            Stmg.EntryBlockBytes = checked(Stmg.EntryBlockBytes + (uint)(end - start));
            if (end - start < 4)
            {
                Stmg.EntryBlocksTooShort = checked(Stmg.EntryBlocksTooShort + 1);
                return false;
            }
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(start, 4));
            if (declared == 0 || declared > StmgMaximumRecords)
            {
                Stmg.EntryCountOutOfRange = checked(Stmg.EntryCountOutOfRange + 1);
                return false;
            }
            var cursor = start + 4;
            var ids = new HashSet<uint>();
            var records = 0U;
            var markers = 0U;
            for (var entry = 0U; entry < declared; entry++)
            {
                if (checked(cursor + StmgEntryHeadBytes) > end)
                {
                    Stmg.EntryHeadPastTheEnd = checked(Stmg.EntryHeadPastTheEnd + 1);
                    return false;
                }
                ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(cursor, 4)));
                var owned = BinaryPrimitives.ReadUInt32LittleEndian(
                    body.AsSpan(cursor + StmgEntryCountOffset, 4));
                if (owned > StmgMaximumEntryRecords)
                {
                    Stmg.EntryRecordCountOutOfRange =
                        checked(Stmg.EntryRecordCountOutOfRange + 1);
                    return false;
                }
                cursor = checked(cursor + StmgEntryHeadBytes);
                var span = checked((int)owned * StmgEntryRecordBytes);
                if (checked(cursor + span) > end)
                {
                    Stmg.EntryRecordsPastTheEnd =
                        checked(Stmg.EntryRecordsPastTheEnd + 1);
                    return false;
                }
                for (var record = 0U; record < owned; record++)
                {
                    if (body[cursor + (int)record * StmgEntryRecordBytes
                             + StmgEntryRecordMarkerOffset] == StmgEntryRecordMarker)
                    {
                        markers = checked(markers + 1);
                    }
                }
                cursor = checked(cursor + span);
                records = checked(records + owned);
                HircBump(Stmg.EntryRecordCounts, $"records_{owned}", 1);
            }
            if (cursor != end)
            {
                // Bounded on both sides, so anything short of byte-exact is a failure
                // and is fenced rather than reported as a partial frame.
                Stmg.EntryBlocksNotClosing = checked(Stmg.EntryBlocksNotClosing + 1);
                return false;
            }
            Stmg.EntryBlocksFramed = checked(Stmg.EntryBlocksFramed + 1);
            Stmg.Entries = checked(Stmg.Entries + declared);
            Stmg.DistinctEntryIds = checked(Stmg.DistinctEntryIds + (uint)ids.Count);
            Stmg.EntryRecords = checked(Stmg.EntryRecords + records);
            Stmg.EntryRecordsCarryingTheMarker =
                checked(Stmg.EntryRecordsCarryingTheMarker + markers);
            return true;
        }


        /// <summary>
        /// Frame the ENVS section: a run of curves over the shared 12-byte point.
        /// </summary>
        /// <remarks>
        /// Each curve is `u8, u8, u8 count, u8` then that many points of `f32 x, f32 y,
        /// u32 interpolation` -- the same record numeric type 0x0B's element runs carry.
        /// The section has no count of its own; the run ends when the bytes do, so
        /// byte-exact closure is what makes the walk a frame rather than a scan.
        /// </remarks>
        private void FrameEnvsSection(byte[] body)
        {
            Envs.Sections = checked(Envs.Sections + 1);
            Envs.SectionBytes = checked(Envs.SectionBytes + (uint)body.Length);
            var cursor = 0;
            var curves = 0U;
            var points = 0U;
            var inRange = 0U;
            var bounded = 0U;
            var ordered = 0U;
            while (cursor < body.Length)
            {
                if (checked(cursor + EnvsCurveHeadBytes) > body.Length
                    || curves >= EnvsMaximumCurves)
                {
                    Envs.SectionsNotClosing = checked(Envs.SectionsNotClosing + 1);
                    return;
                }
                var count = body[cursor + EnvsCurveCountOffset];
                cursor = checked(cursor + EnvsCurveHeadBytes);
                var span = checked(count * EnvsPointBytes);
                if (checked(cursor + span) > body.Length)
                {
                    Envs.SectionsNotClosing = checked(Envs.SectionsNotClosing + 1);
                    return;
                }
                curves = checked(curves + 1);
                var previous = float.NegativeInfinity;
                var rising = true;
                for (var point = 0; point < count; point++)
                {
                    var at = checked(cursor + point * EnvsPointBytes);
                    var x = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(at, 4));
                    var y = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(at + 4, 4));
                    var code = BinaryPrimitives.ReadUInt32LittleEndian(
                        body.AsSpan(at + 8, 4));
                    points = checked(points + 1);
                    if (code <= EnvsMaximumInterpolation)
                    {
                        inRange = checked(inRange + 1);
                        HircBump(Envs.InterpolationCodes, $"code_{code}", 1);
                    }
                    if (!float.IsNaN(x) && !float.IsInfinity(x)
                        && !float.IsNaN(y) && !float.IsInfinity(y)
                        && Math.Abs(x) < 1e7f && Math.Abs(y) < 1e7f)
                    {
                        bounded = checked(bounded + 1);
                    }
                    if (x < previous)
                    {
                        rising = false;
                    }
                    previous = x;
                }
                if (rising)
                {
                    ordered = checked(ordered + 1);
                }
                cursor = checked(cursor + span);
            }
            if (curves == 0 || points == 0)
            {
                Envs.SectionsNotClosing = checked(Envs.SectionsNotClosing + 1);
                return;
            }
            Envs.SectionsFramed = checked(Envs.SectionsFramed + 1);
            Envs.Curves = checked(Envs.Curves + curves);
            Envs.Points = checked(Envs.Points + points);
            Envs.CodesInRange = checked(Envs.CodesInRange + inRange);
            Envs.FloatsBounded = checked(Envs.FloatsBounded + bounded);
            Envs.CurvesWithRisingX = checked(Envs.CurvesWithRisingX + ordered);
        }


        /// <summary>
        /// Frame the INIT plugin name table and the PLAT platform string.
        /// </summary>
        private void FrameInitAndPlat()
        {
            foreach (var structure in BnkStructures)
            {
                foreach (var section in structure.Sections)
                {
                    if (section.Body is null)
                    {
                        continue;
                    }
                    if (section.Tag == "PLAT")
                    {
                        Init.PlatSections = checked(Init.PlatSections + 1);
                        var terminator = Array.IndexOf(section.Body, (byte)0);
                        if (terminator < 0 || terminator != section.Body.Length - 1
                            || section.Body.Length > PlatMaximumBytes)
                        {
                            Init.PlatSectionsNotClosing =
                                checked(Init.PlatSectionsNotClosing + 1);
                            continue;
                        }
                        Init.PlatSectionsFramed = checked(Init.PlatSectionsFramed + 1);
                        HircBump(
                            Init.PlatformNames,
                            Encoding.ASCII.GetString(section.Body, 0, terminator),
                            1);
                        continue;
                    }
                    if (section.Tag == "ENVS")
                    {
                        FrameEnvsSection(section.Body);
                        continue;
                    }
                    if (section.Tag != "INIT")
                    {
                        continue;
                    }
                    Init.Sections = checked(Init.Sections + 1);
                    var body = section.Body;
                    if (body.Length < 4)
                    {
                        Init.SectionsTooShort = checked(Init.SectionsTooShort + 1);
                        continue;
                    }
                    var declared = BinaryPrimitives.ReadUInt32LittleEndian(
                        body.AsSpan(0, 4));
                    if (declared == 0 || declared > InitMaximumEntries)
                    {
                        Init.CountOutOfRange = checked(Init.CountOutOfRange + 1);
                        continue;
                    }
                    var cursor = 4;
                    var names = new Dictionary<uint, string>();
                    var failed = false;
                    for (var entry = 0U; entry < declared; entry++)
                    {
                        if (checked(cursor + InitEntryHeadBytes) > body.Length)
                        {
                            failed = true;
                            break;
                        }
                        var company = BinaryPrimitives.ReadUInt16LittleEndian(
                            body.AsSpan(cursor, 2));
                        var plugin = BinaryPrimitives.ReadUInt16LittleEndian(
                            body.AsSpan(cursor + 2, 2));
                        cursor = checked(cursor + InitEntryHeadBytes);
                        var terminator = Array.IndexOf(body, (byte)0, cursor);
                        if (terminator < 0
                            || terminator - cursor > InitMaximumNameBytes)
                        {
                            failed = true;
                            break;
                        }
                        names[((uint)plugin << 16) | company] =
                            Encoding.ASCII.GetString(body, cursor, terminator - cursor);
                        cursor = terminator + 1;
                    }
                    if (failed || cursor != body.Length)
                    {
                        // Self-describing and bounded, so anything short of byte-exact
                        // is a failure rather than a partial frame.
                        Init.SectionsNotClosing = checked(Init.SectionsNotClosing + 1);
                        continue;
                    }
                    Init.SectionsFramed = checked(Init.SectionsFramed + 1);
                    Init.Entries = checked(Init.Entries + declared);
                    Init.DistinctPluginIds = checked(Init.DistinctPluginIds + (uint)names.Count);
                    foreach (var pair in names)
                    {
                        Init.PluginNames[pair.Key] = pair.Value;
                    }
                }
            }
        }


        private void ResolveStmgWords()
        {
            var typeOf = new Dictionary<uint, byte>();
            foreach (var structure in BnkStructures)
            {
                foreach (var pair in structure.WalkObjectTypes)
                {
                    typeOf[pair.Key] = pair.Value;
                }
            }
            StmgWords.HircObjects = checked((uint)typeOf.Count);
            foreach (var structure in BnkStructures)
            {
                foreach (var section in structure.Sections)
                {
                    if (section.Body is null)
                    {
                        continue;
                    }
                    // Every section nothing parses, not only STMG. Leaving the others
                    // out would answer the question for one of four candidates.
                    StmgWords.Sections = checked(StmgWords.Sections + 1);
                    StmgWords.SectionBytes =
                        checked(StmgWords.SectionBytes + (uint)section.Body.Length);
                    var body = section.Body;
                    for (var offset = 0; offset + 4 <= body.Length; offset++)
                    {
                        StmgWords.WordsTested = checked(StmgWords.WordsTested + 1);
                        HircBump(StmgWords.WordsTestedByTag, section.Tag, 1);
                        var value = BinaryPrimitives.ReadUInt32LittleEndian(
                            body.AsSpan(offset, 4));
                        if (!typeOf.TryGetValue(value, out var targetType))
                        {
                            continue;
                        }
                        StmgWords.WordsNamingAnObject =
                            checked(StmgWords.WordsNamingAnObject + 1);
                        HircBump(
                            StmgWords.TypesNamed,
                            $"{section.Tag}_type{targetType:X2}",
                            1);
                        HircBump(StmgWords.OffsetsModTwelve, $"mod12_{offset % 12}", 1);
                    }
                }
            }
        }


        private void ClassifyType03Targets()
        {
            var perBank = new Dictionary<ulong, HashSet<uint>>();
            var everything = new HashSet<uint>();
            // Which TYPE an action addresses, not just which bank it lives in. The
            // locality census cannot answer whether the music types are addressable
            // from an action at all, and that is a different question.
            var typeOf = new Dictionary<uint, byte>();
            foreach (var structure in BnkStructures)
            {
                perBank[structure.BankId] = structure.DeclaredObjectIds;
                everything.UnionWith(structure.DeclaredObjectIds);
                foreach (var pair in structure.WalkObjectTypes)
                {
                    typeOf[pair.Key] = pair.Value;
                }
            }
            foreach (var structure in BnkStructures)
            {
                var own = perBank[structure.BankId];
                foreach (var (action, target) in structure.Type03Targets)
                {
                    Type03Targets.Objects = checked(Type03Targets.Objects + 1);
                    var key = $"action_{action:X2}";
                    if (target == 0)
                    {
                        Type03Targets.Zero = checked(Type03Targets.Zero + 1);
                    }
                    else if (own.Contains(target))
                    {
                        Type03Targets.SameBank = checked(Type03Targets.SameBank + 1);
                        HircBump(Type03Targets.SameBankByActionByte, key, 1);
                        if (typeOf.TryGetValue(target, out var sameType))
                        {
                            HircBump(Type03Targets.TargetTypes, $"type{sameType:X2}", 1);
                        }
                    }
                    else if (everything.Contains(target))
                    {
                        Type03Targets.OtherBankInPackage =
                            checked(Type03Targets.OtherBankInPackage + 1);
                        HircBump(Type03Targets.OtherBankByActionByte, key, 1);
                        if (typeOf.TryGetValue(target, out var otherType))
                        {
                            HircBump(Type03Targets.TargetTypes, $"type{otherType:X2}", 1);
                        }
                    }
                    else
                    {
                        Type03Targets.OutsidePackage = checked(Type03Targets.OutsidePackage + 1);
                        HircBump(Type03Targets.OutsideByActionByte, key, 1);
                    }
                }
            }
        }


        // The walk runs over every bank in the package at once. Doing it per bank
        // abandoned any edge that pointed at a sibling bank, which the type 0x03
        // classification showed is a real and common relation rather than a dead end.
        // Edges leaving the *package* are still counted and not followed: this reader
        // cannot see the other packages.
        private void WalkNamedReachAcrossPackage()
        {
            var hashes = NamedIdentityHashes;
            if (hashes == null || hashes.Count == 0 || BnkStructures.Count == 0)
            {
                return;
            }
            var types = new Dictionary<uint, byte>();
            var edges = new Dictionary<uint, List<uint>>();
            var sources = new Dictionary<uint, List<uint>>();
            foreach (var structure in BnkStructures)
            {
                foreach (var pair in structure.WalkObjectTypes)
                {
                    types[pair.Key] = pair.Value;
                }
                foreach (var pair in structure.WalkEdges)
                {
                    edges[pair.Key] = pair.Value;
                }
                foreach (var pair in structure.WalkSourceIds)
                {
                    sources[pair.Key] = pair.Value;
                }
            }
            foreach (var structure in BnkStructures)
            {
                var census = structure.NamedReachCensus;
                census.MatchedObjects = 0;
                census.MatchedNamedType = 0;
                census.ReachingASource = 0;
                census.ReachingNoSource = 0;
                census.ReachedSourceIds = 0;
                census.ReachedObjectTypes.Clear();
                census.WalkEdgesLeavingThePackage = 0;
                census.MatchesByObjectType.Clear();
                census.ReachedSourceIdsByIdentity.Clear();
                census.ReachedSourceIdListByIdentity.Clear();
            }
            WalkHircNamedReach(
                BnkStructures[0].NamedReachCensus, hashes, types, edges, sources);
        }

        private static uint ReadUInt32(BinaryReader reader, string field)
        {
            if (reader.BaseStream.Length - reader.BaseStream.Position < 4)
            {
                throw new InvalidDataException($"truncated AKPK {field}");
            }
            return reader.ReadUInt32();
        }

        public byte[] GetWemData(EndfieldWemEntry entry)
        {
            if (entry.Offset > int.MaxValue || entry.Size > int.MaxValue || entry.Offset + entry.Size > (ulong)data.Length)
            {
                throw new EndfieldVfsException("invalid WEM entry range");
            }

            var result = new byte[entry.Size];
            Array.Copy(data, (long)entry.Offset, result, 0, (long)entry.Size);
            if (entry.ContainerSeed.HasValue)
            {
                if (entry.ContainerDataOffset > uint.MaxValue)
                {
                    throw new EndfieldVfsException($"invalid AKPK bank media offset: {entry.ContainerDataOffset}");
                }
                EndfieldAudioCrypto.DecryptVfs(
                    result,
                    0,
                    result.Length,
                    entry.ContainerSeed.Value,
                    (uint)entry.ContainerDataOffset);
            }
            // Embedded Wwise plug-in media uses a PLUG envelope rather than a
            // RIFF/RIFX WEM. It is already framed after bank-ID decryption;
            // applying the media-ID stream cipher would corrupt the envelope.
            if (result.Length >= 4
                && !HasMagic(result, "RIFF")
                && !HasMagic(result, "RIFX")
                && !HasMagic(result, "PLUG"))
            {
                EndfieldAudioCrypto.DecryptWem(result, (uint)entry.Id);
            }
            return result;
        }

        private static bool HasMagic(byte[] buffer, string magic)
        {
            if (buffer.Length < magic.Length)
            {
                return false;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (buffer[i] != (byte)magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private void ParseLanguages(BinaryReader reader, int sectorStart, uint sectorSize)
        {
            var sectorEnd = checked((long)sectorStart + sectorSize);
            if (sectorSize < 4 || sectorEnd > data.Length)
            {
                throw new InvalidDataException($"AKPK languages sector out of range: start={sectorStart}, size={sectorSize}, data={data.Length}");
            }

            reader.BaseStream.Position = sectorStart;
            var langCount = ReadUInt32(reader, "language count");
            if (langCount > (sectorSize - 4) / 8)
            {
                throw new InvalidDataException($"AKPK language count exceeds sector: count={langCount}, sector={sectorSize}");
            }

            var stringOffset = (uint)sectorStart;
            for (var i = 0; i < langCount; i++)
            {
                var langOffset = ReadUInt32(reader, "language offset");
                var langId = ReadUInt32(reader, "language id");
                if (langOffset >= sectorSize)
                {
                    throw new InvalidDataException($"AKPK language offset out of range: offset={langOffset}, sector={sectorSize}");
                }
                var current = reader.BaseStream.Position;
                reader.BaseStream.Position = checked(stringOffset + langOffset);

                var testBytes = ReadBytesWithin(reader, 2, sectorEnd, "language string probe");
                reader.BaseStream.Position = checked(stringOffset + langOffset);
                string langName;
                if (testBytes.Length == 2 && (testBytes[0] == 0 || testBytes[1] == 0))
                {
                    var available = checked((int)Math.Min(32, sectorEnd - reader.BaseStream.Position));
                    var bytes = ReadBytesWithin(reader, available, sectorEnd, "UTF-16 language string");
                    var chars = new List<ushort>();
                    var terminated = false;
                    for (var j = 0; j + 1 < bytes.Length; j += 2)
                    {
                        var value = BitConverter.ToUInt16(bytes, j);
                        if (value == 0)
                        {
                            terminated = true;
                            break;
                        }
                        chars.Add(value);
                    }
                    if (!terminated)
                    {
                        throw new InvalidDataException($"unterminated UTF-16 AKPK language string at offset={langOffset}");
                    }
                    langName = Encoding.Unicode.GetString(ToBytes(chars));
                }
                else
                {
                    var available = checked((int)Math.Min(16, sectorEnd - reader.BaseStream.Position));
                    var bytes = ReadBytesWithin(reader, available, sectorEnd, "UTF-8 language string");
                    var terminator = Array.IndexOf(bytes, (byte)0);
                    if (terminator < 0)
                    {
                        throw new InvalidDataException($"unterminated UTF-8 AKPK language string at offset={langOffset}");
                    }
                    langName = Encoding.UTF8.GetString(bytes, 0, terminator);
                }

                Languages[langId] = langName;
                reader.BaseStream.Position = current;
            }

            reader.BaseStream.Position = sectorEnd;
        }

        private int ParseSector(BinaryReader reader, int sectorStart, uint sectorSize, bool isSounds, bool isExternals)
        {
            if (sectorSize == 0)
            {
                return 0;
            }

            var sectorEnd = checked((long)sectorStart + sectorSize);
            if (sectorSize < 4 || sectorEnd > data.Length)
            {
                throw new InvalidDataException($"AKPK sector out of range: start={sectorStart}, size={sectorSize}, data={data.Length}");
            }
            reader.BaseStream.Position = sectorStart;
            var fileCount = ReadUInt32(reader, "sector file count");
            if (fileCount == 0)
            {
                if (sectorSize != 4)
                {
                    throw new InvalidDataException($"AKPK empty sector has unexpected size: {sectorSize}");
                }
                return 0;
            }

            if ((sectorSize - 4) % fileCount != 0)
            {
                throw new InvalidDataException($"AKPK sector size is not divisible by file count: size={sectorSize}, count={fileCount}");
            }
            var entrySize = (sectorSize - 4) / fileCount;
            var altMode = entrySize == 0x18;
            if (entrySize != 20 && entrySize != 24)
            {
                throw new InvalidDataException($"unsupported AKPK sector entry size: {entrySize}");
            }

            for (var i = 0; i < fileCount; i++)
            {
                var fileIdLow = (ulong)ReadUInt32(reader, "file id");
                ulong? fileIdHigh = null;
                if (altMode && isExternals)
                {
                    fileIdHigh = ReadUInt32(reader, "external file id high");
                }

                var blockSize = ReadUInt32(reader, "block size");
                ulong size;
                if (altMode && isExternals)
                {
                    size = ReadUInt32(reader, "external size");
                }
                else if (altMode)
                {
                    if (reader.BaseStream.Length - reader.BaseStream.Position < 8)
                    {
                        throw new InvalidDataException("truncated AKPK 64-bit size");
                    }
                    size = reader.ReadUInt64();
                }
                else
                {
                    size = ReadUInt32(reader, "size");
                }

                var offset = (ulong)ReadUInt32(reader, "offset");
                var langId = ReadUInt32(reader, "language id");
                if (blockSize != 0)
                {
                    offset = checked(offset * blockSize);
                }

                if (size > (ulong)data.Length || offset > (ulong)data.Length - size)
                {
                    throw new InvalidDataException($"AKPK entry range out of bounds: offset={offset}, size={size}, data={data.Length}");
                }

                Languages.TryGetValue(langId, out var language);
                var finalId = fileIdHigh.HasValue ? (fileIdHigh.Value << 32) | fileIdLow : fileIdLow;
                if (!isSounds)
                {
                    foreach (var (wemId, wemOffset, wemSize) in ParseBnk(fileIdLow, offset, size))
                    {
                        Entries.Add(new EndfieldWemEntry
                        {
                            Id = wemId,
                            Offset = offset + wemOffset,
                            Size = wemSize,
                            Language = language,
                            ContainerSeed = checked((uint)fileIdLow),
                            ContainerDataOffset = wemOffset,
                        });
                    }
                }
                else
                {
                    Entries.Add(new EndfieldWemEntry
                    {
                        Id = finalId,
                        Offset = offset,
                        Size = size,
                        Language = language,
                    });
                }
            }
            if (reader.BaseStream.Position != sectorEnd)
            {
                throw new InvalidDataException($"AKPK sector cursor mismatch: cursor={reader.BaseStream.Position}, end={sectorEnd}");
            }
            return checked((int)fileCount);
        }

        private IEnumerable<(ulong id, ulong offset, ulong size)> ParseBnk(ulong bankId, ulong offset, ulong size)
        {
            if (offset > int.MaxValue || size > int.MaxValue || size < 8)
            {
                throw new InvalidDataException($"invalid AKPK bank range: id={bankId}, offset={offset}, size={size}");
            }

            var start = (int)offset;
            var end = checked(start + (int)size);
            var payload = new byte[(int)size];
            Array.Copy(data, start, payload, 0, payload.Length);
            EndfieldAudioCrypto.DecryptVfs(payload, 0, payload.Length, checked((uint)bankId), 0);
            if (!HasMagicAt(payload, 0, "BKHD"))
            {
                throw new InvalidDataException($"AKPK bank payload missing BKHD: id={bankId}, offset={offset}, size={size}");
            }

            var pos = 0;
            var didx = new List<(uint id, uint offset, uint size)>();
            var dataBodyOffset = -1;
            var dataBodySize = 0U;
            var first = true;
            var structure = new EndfieldBnkStructure
            {
                BankId = bankId,
                ByteLength = checked((int)size),
            };
            while (pos < payload.Length)
            {
                if (payload.Length - pos < 8)
                {
                    throw new InvalidDataException($"truncated AKPK BNK section: id={bankId}, offset={pos}");
                }
                var tag = Encoding.ASCII.GetString(payload, pos, 4);
                var sectionSize = BitConverter.ToUInt32(payload, pos + 4);
                var bodyStart = checked(pos + 8);
                var bodyEnd = checked(bodyStart + checked((int)sectionSize));
                if (bodyEnd > payload.Length || !IsAsciiSectionTag(tag))
                {
                    throw new InvalidDataException($"invalid AKPK BNK section: id={bankId}, tag={tag}, offset={pos}, size={sectionSize}");
                }
                if (first && tag != "BKHD")
                {
                    throw new InvalidDataException($"AKPK BNK must start with BKHD: id={bankId}");
                }
                first = false;
                structure.Sections.Add(new EndfieldBnkSection
                {
                    Tag = tag,
                    Offset = pos,
                    DeclaredSize = sectionSize,
                    // BKHD, HIRC, DIDX and DATA are parsed in place. The rest are kept
                    // whole: STMG, INIT, ENVS and PLAT appear once each in the whole
                    // corpus and nothing reads them yet.
                    Body = tag is "BKHD" or "HIRC" or "DIDX" or "DATA"
                        ? null
                        : payload.AsSpan(bodyStart, checked((int)sectionSize)).ToArray(),
                });
                if (tag == "BKHD")
                {
                    if (sectionSize < 4)
                    {
                        throw new InvalidDataException(
                            $"AKPK BKHD version field truncated: id={bankId}, offset={bodyStart}, expected=4, actual={sectionSize}");
                    }
                    structure.Version = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(bodyStart, 4));
                }
                if (tag == "DIDX")
                {
                    if (sectionSize % 12 != 0)
                    {
                        throw new InvalidDataException($"AKPK DIDX size is not divisible by 12: id={bankId}, size={sectionSize}");
                    }
                    for (var p = bodyStart; p < bodyEnd; p += 12)
                    {
                        didx.Add((BitConverter.ToUInt32(payload, p), BitConverter.ToUInt32(payload, p + 4), BitConverter.ToUInt32(payload, p + 8)));
                    }
                }
                else if (tag == "DATA")
                {
                    dataBodyOffset = bodyStart;
                    dataBodySize = sectionSize;
                }
                else if (tag == "HIRC")
                {
                    ParseHirc(payload, bodyStart, checked((int)sectionSize), bankId, structure, NamedIdentityHashes);
                }
                pos = bodyEnd;
            }

            BnkStructures.Add(structure);

            if (didx.Count > 0 && dataBodyOffset < 0)
            {
                throw new InvalidDataException($"AKPK DIDX has no DATA section: id={bankId}");
            }
            foreach (var (id, wemOffset, wemSize) in didx)
            {
                if (dataBodyOffset < 0 || wemOffset > dataBodySize || wemSize > dataBodySize - wemOffset)
                {
                    throw new InvalidDataException($"AKPK DIDX media range out of DATA: bank={bankId}, media={id}, offset={wemOffset}, size={wemSize}, data={dataBodySize}");
                }
                yield return (id, checked((ulong)dataBodyOffset + wemOffset), wemSize);
            }
        }

        private static void ParseHirc(
            byte[] payload,
            int bodyStart,
            int bodyLength,
            ulong bankId,
            EndfieldBnkStructure structure,
            HashSet<uint> namedIdentityHashes)
        {
            if (bodyLength < 4)
            {
                throw new InvalidDataException($"AKPK HIRC object-count field truncated: id={bankId}, offset={bodyStart}");
            }

            var bodyEnd = checked(bodyStart + bodyLength);
            var objectCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(bodyStart, 4));
            var maximumObjectCount = checked((bodyLength - 4) / 9);
            if (objectCount > maximumObjectCount)
            {
                throw new InvalidDataException(
                    $"AKPK HIRC object count exceeds HIRC: id={bankId}, count={objectCount}, maximum={maximumObjectCount}, size={bodyLength}");
            }
            var cursor = checked(bodyStart + 4);
            structure.HircObjectCount = checked(structure.HircObjectCount + objectCount);

            // First pass: the bank's object identities, so the anonymous reference
            // vectors framed below can be resolved against them. This walks headers
            // only and repeats the same bounds the framing pass enforces.
            var bankObjectTypes = new Dictionary<uint, byte>();
            var duplicateObjectIds = new HashSet<uint>();
            var scan = cursor;
            for (var ordinal = 0U; ordinal < objectCount; ordinal++)
            {
                if (bodyEnd - scan < 9)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object header truncated: id={bankId}, ordinal={ordinal}, offset={scan}, expected=9, actual={bodyEnd - scan}");
                }
                var scanType = payload[scan];
                var scanSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(scan + 1), 4));
                var scanId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(scan + 5), 4));
                if (scanSize < 4)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object size too small: id={bankId}, ordinal={ordinal}, object={scanId}, type={scanType}, size={scanSize}, expectedMin=4");
                }
                var scanEnd = checked((long)scan + 5L + scanSize);
                if (scanEnd > bodyEnd)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object range out of HIRC: id={bankId}, ordinal={ordinal}, object={scanId}, type={scanType}, offset={scan}, size={scanSize}, hircEnd={bodyEnd}");
                }
                // Cross-type duplicate ids are retained numerically elsewhere; a duplicate
                // here would make resolution ambiguous, so record it rather than collapse it.
                if (!bankObjectTypes.TryAdd(scanId, scanType))
                {
                    structure.ReferenceCensus.DuplicateObjectIds =
                        checked(structure.ReferenceCensus.DuplicateObjectIds + 1);
                    duplicateObjectIds.Add(scanId);
                }
                scan = checked((int)scanEnd);
            }

            var referrerCounts = new Dictionary<uint, uint>();
            var referrerOf = new Dictionary<uint, uint>();
            var bankEdges = new Dictionary<uint, List<uint>>();
            // One object can own several sources, which only became true when numeric
            // type 0x0B joined this map: 0x02 carries exactly one record and 0x0B a
            // counted array of the same record.
            var bankSourceIds = new Dictionary<uint, List<uint>>();
            for (var ordinal = 0U; ordinal < objectCount; ordinal++)
            {
                if (bodyEnd - cursor < 9)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object header truncated: id={bankId}, ordinal={ordinal}, offset={cursor}, expected=9, actual={bodyEnd - cursor}");
                }

                var objectType = payload[cursor];
                var objectSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(cursor + 1), 4));
                var objectId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(cursor + 5), 4));
                if (objectSize < 4)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object size too small: id={bankId}, ordinal={ordinal}, object={objectId}, type={objectType}, size={objectSize}, expectedMin=4");
                }
                var objectEnd = checked((long)cursor + 5L + objectSize);
                if (objectEnd > bodyEnd)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object range out of HIRC: id={bankId}, ordinal={ordinal}, object={objectId}, type={objectType}, offset={cursor}, size={objectSize}, hircEnd={bodyEnd}");
                }
                structure.HircObjectTypeCounts.TryGetValue(objectType, out var typeCount);
                structure.HircObjectTypeCounts[objectType] = checked(typeCount + 1);
                if (!structure.HircObjectTypeStats.TryGetValue(objectType, out var stats))
                {
                    stats = new EndfieldBnkObjectTypeStats
                    {
                        MinDeclaredLength = objectSize,
                        MaxDeclaredLength = objectSize,
                    };
                    structure.HircObjectTypeStats[objectType] = stats;
                }
                stats.DeclaredLengthBytes = checked(stats.DeclaredLengthBytes + objectSize);
                stats.MinDeclaredLength = Math.Min(stats.MinDeclaredLength, objectSize);
                stats.MaxDeclaredLength = Math.Max(stats.MaxDeclaredLength, objectSize);
                stats.Count = checked(stats.Count + 1);
                if (objectType == 3)
                {
                    var actionBodyStart = checked(cursor + 9);
                    var actionBodyLength = checked((int)objectSize - 4);
                    RecordType3ActionFrame(
                        payload.AsSpan(actionBodyStart, actionBodyLength),
                        structure.Version,
                        bankId,
                        ordinal,
                        objectId,
                        structure);
                }
                if (objectType == 2)
                {
                    ParseType2SourcePrefix(
                        payload,
                        checked(cursor + 9),
                        checked((int)objectSize - 4),
                        bankId,
                        ordinal,
                        objectId,
                        structure);
                }
                if (objectType is 2 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22)
                {
                    var bodySpan = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    // Both switches are exhaustive on purpose: widening the guard above
                    // without adding arms here must fail loudly rather than silently
                    // frame a new type with another type's framer and census.
                    var census = objectType switch
                    {
                        2 => structure.Type2Body,
                        5 => structure.Type5Body,
                        6 => structure.Type6Body,
                        7 => structure.Type7Body,
                        9 => structure.Type9Body,
                        10 => structure.Type0ABody,
                        12 => structure.Type0CBody,
                        13 => structure.Type0DBody,
                        8 => structure.Type08Body,
                        11 => structure.Type0BBody,
                        18 => structure.Type12Body,
                        14 => structure.Type14Body,
                        15 => structure.Type0FBody,
                        16 => structure.Type10Body,
                        17 => structure.Type11Body,
                        19 => structure.Type13Body,
                        20 => structure.Type14ModBody,
                        21 => structure.Type15Body,
                        22 => structure.Type22Body,
                        _ => throw new InvalidDataException(
                            $"AKPK HIRC body census is not defined for type {objectType}"),
                    };
                    var frame = objectType switch
                    {
                        2 => FrameType2Body(bodySpan, structure.Version),
                        5 => FrameType5Body(bodySpan, structure.Version),
                        6 => FrameType6Body(bodySpan, structure.Version),
                        7 => FrameType7Body(bodySpan, structure.Version),
                        9 => FrameType9Body(bodySpan, structure.Version),
                        10 => FrameType0ABody(bodySpan, structure.Version),
                        12 => FrameType0CBody(bodySpan, structure.Version),
                        13 => FrameType0DBody(bodySpan, structure.Version),
                        8 => FrameSharedBody(bodySpan, structure.Version),
                        11 => FrameType0BBody(bodySpan, structure.Version),
                        18 => FrameSharedBody(bodySpan, structure.Version),
                        14 => FrameType14Body(bodySpan, structure.Version),
                        15 => FrameType0FBody(bodySpan, structure.Version),
                        16 => FrameFxBody(bodySpan, structure.Version, withDeviceSlots: false),
                        17 => FrameFxBody(bodySpan, structure.Version, withDeviceSlots: false),
                        19 => FrameModulatorBody(bodySpan, structure.Version),
                        20 => FrameModulatorBody(bodySpan, structure.Version),
                        21 => FrameFxBody(bodySpan, structure.Version, withDeviceSlots: true),
                        22 => FrameModulatorBody(bodySpan, structure.Version),
                        _ => throw new InvalidDataException(
                            $"AKPK HIRC body framer is not defined for type {objectType}"),
                    };
                    if (objectType is 8 or 18)
                    {
                        structure.SharedBodies.Add(bodySpan.ToArray());
                        // The leading word is this object's parent. Kept per bank,
                        // because object ids repeat across banks and a graph built on
                        // the deduplicated ids is a different graph.
                        structure.SharedParents[objectId] = (
                            objectType,
                            bodySpan.Length >= 4
                                ? BinaryPrimitives.ReadUInt32LittleEndian(bodySpan.Slice(0, 4))
                                : 0u);
                    }
                    RecordHircBodyFrame(census, frame, bodySpan, bankId, ordinal, objectId);
                    if (frame.Status == "exact" && frame.References is { Count: > 0 })
                    {
                        bankEdges[objectId] = new List<uint>(frame.References);
                    }
                    // Numeric types 0x02 and 0x0B carry the SAME 14-byte source record.
                    // 0x02 holds exactly one, at body offset 0; 0x0B holds a counted
                    // array of them after its leading flag and count.
                    //
                    // The record is shared, not merely similar. Its word at +0 is a
                    // plugin id drawn from a closed set: 0x0B uses 0x00040001 (3,506
                    // records) and 0x00140001 (941), and 140,121 of 0x02's 142,815
                    // bodies open with one of exactly those two. And its word at +5
                    // names a declared media id in 4,447 of 4,447 of 0x0B's records --
                    // against 0 at every other offset in the record, which is what
                    // makes +5 the id field rather than a hit rate.
                    if (objectType == 2 && bodySpan.Length >= SourceRecordBytes)
                    {
                        bankSourceIds[objectId] = new List<uint>
                        {
                            BinaryPrimitives.ReadUInt32LittleEndian(
                                bodySpan.Slice(SourceRecordIdOffset, 4)),
                        };
                        CollectSourceRecord(structure, 2, bodySpan.Slice(0, SourceRecordBytes));
                    }
                    else if (objectType == 11 && bodySpan.Length >= 5)
                    {
                        var declared = BinaryPrimitives.ReadUInt32LittleEndian(
                            bodySpan.Slice(1, 4));
                        if (declared > 0 && declared <= SourceRecordMaximumCount
                            && checked(5 + (int)declared * SourceRecordBytes) <= bodySpan.Length)
                        {
                            var owned = new List<uint>();
                            for (var record = 0U; record < declared; record++)
                            {
                                var at = checked(5 + (int)record * SourceRecordBytes);
                                owned.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                                    bodySpan.Slice(
                                        checked(at + SourceRecordIdOffset), 4)));
                                CollectSourceRecord(
                                    structure, 11, bodySpan.Slice(at, SourceRecordBytes));
                            }
                            bankSourceIds[objectId] = owned;
                        }
                    }
                    ResolveHircReferences(
                        structure.ReferenceCensus,
                        frame,
                        objectType,
                        objectId,
                        bankObjectTypes,
                        duplicateObjectIds,
                        referrerCounts,
                        referrerOf);
                }
                if (objectType == 18)
                {
                    // Numeric type 0x12's fenced bodies carry the same tail numeric
                    // type 0x08's do, so they are censused by the same code rather than
                    // by a second reading of the same bytes.
                    var body12 = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    CensusType08Tail(
                        structure.Type12Tail, structure.Type12TailWords, body12, requireSignature: false);
                }
                // Numeric type 0x08's leading word: null, or one same-bank object.
                if (objectType == 8)
                {
                    var head08 = structure.Type08Head;
                    head08.Bodies = checked(head08.Bodies + 1);
                    var head08Body = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    CensusType08Tail(structure.Type08Tail, structure.Type08TailWords, head08Body);
                    if (head08Body.Length < 4)
                    {
                        head08.TooShort = checked(head08.TooShort + 1);
                    }
                    else
                    {
                        var word = BinaryPrimitives.ReadUInt32LittleEndian(head08Body.Slice(0, 4));
                        if (word == 0)
                        {
                            head08.Null = checked(head08.Null + 1);
                        }
                        else if (bankObjectTypes.ContainsKey(word))
                        {
                            head08.Resolved = checked(head08.Resolved + 1);
                            bankEdges[objectId] = new List<uint> { word };
                        }
                        else
                        {
                            head08.Unresolved = checked(head08.Unresolved + 1);
                        }
                    }
                }
                // Numeric types 0x0A and 0x0D carry one 32-bit word near the head that
                // names an object in the same bank. Byte 2 selects where it sits: zero
                // puts it at offset 9, nonzero at offset 5. The offset is computed from
                // that byte, never searched for, and an unobserved discriminant is
                // counted as unknown rather than guessed either way.
                foreach (var (parentType, parentOffset) in ParentFieldOffsets)
                {
                    if (parentType != objectType)
                    {
                        continue;
                    }
                    var pb = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    if (pb.Length >= parentOffset + 4)
                    {
                        var named = BinaryPrimitives.ReadUInt32LittleEndian(
                            pb.Slice(parentOffset, 4));
                        if (named != 0)
                        {
                            structure.ParentFields[objectId] = named;
                        }
                    }
                    break;
                }
                if (objectType is 10 or 12 or 13)
                {
                    var head = structure.MusicHeadReferences;
                    head.Bodies = checked(head.Bodies + 1);
                    if (objectType == 10)
                    {
                        structure.Type0ABodies.Add((
                            objectId,
                            payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)).ToArray()));
                        CollectType0AEndAnchor(
                            structure.Type0AEndAnchors,
                            payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)));
                        CollectType0AHeadPrediction(
                            structure.Type0AHeadPredictions,
                            payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)));
                    }
                    if (objectType is 10 or 12 or 13)
                    {
                        // Every music type names another object at a fixed offset of 9,
                        // and the targets chain: 0x0A -> 0x0D -> 0x0C -> 0x0C. Kept per
                        // bank, because object ids repeat across banks and a relation
                        // resolved corpus-wide is a different relation.
                        var parentBody = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                        if (objectType == 12)
                        {
                            CensusType0CArray(
                                structure.Type0CArray, parentBody, bankObjectTypes,
                                structure.MusicEdges, objectId);
                        }
                        structure.Type0CParents[objectId] = (
                            objectType,
                            parentBody.Length >= Type0CParentOffset + 4
                                ? BinaryPrimitives.ReadUInt32LittleEndian(
                                    parentBody.Slice(Type0CParentOffset, 4))
                                : 0u);
                    }
                    if (objectType is 10 or 12 or 13)
                    {
                        var sourceBodySpan = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                        var distinct = new HashSet<uint>();
                        for (var at = 0; at + 4 <= sourceBodySpan.Length; at++)
                        {
                            var word = BinaryPrimitives.ReadUInt32LittleEndian(sourceBodySpan.Slice(at, 4));
                            if (word != 0)
                            {
                                distinct.Add(word);
                            }
                        }
                        var flat = new uint[distinct.Count];
                        distinct.CopyTo(flat);
                        structure.MusicSources.Add((objectId, objectType, flat));
                    }
                    CollectMusicBodyWords(
                        structure.MusicBodyWords,
                        objectType,
                        payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)));
                    CensusMusicTailWords(
                        head,
                        payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)),
                        namedIdentityHashes);
                    HircBump(head.BodiesByType, $"type{objectType:X2}", 1);
                    var musicBody = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    if (musicBody.Length < 3)
                    {
                        head.TooShort = checked(head.TooShort + 1);
                    }
                    else if (musicBody[0] != 0)
                    {
                        HircBump(head.HeadShapeCounts, $"head_{musicBody[0]:X2}", 1);
                        head.UnknownHeadShape = checked(head.UnknownHeadShape + 1);
                    }
                    else
                    {
                        HircBump(head.HeadShapeCounts, $"head_{musicBody[0]:X2}", 1);
                        var discriminant = musicBody[2];
                        HircBump(head.DiscriminantCounts, $"byte2_{discriminant:X2}", 1);
                        if (discriminant > 2)
                        {
                            head.UnknownDiscriminant = checked(head.UnknownDiscriminant + 1);
                        }
                        else
                        {
                            var offset = discriminant == 0 ? 9 : 5;
                            if (musicBody.Length < offset + 4)
                            {
                                head.TooShort = checked(head.TooShort + 1);
                            }
                            else
                            {
                                HircBump(head.OffsetCounts, $"offset_{offset}", 1);
                                var target = BinaryPrimitives.ReadUInt32LittleEndian(
                                    musicBody.Slice(offset, 4));
                                if (target == 0)
                                {
                                    head.Zero = checked(head.Zero + 1);
                                }
                                else if (bankObjectTypes.ContainsKey(target))
                                {
                                    head.Resolved = checked(head.Resolved + 1);
                                    bankEdges[objectId] = new List<uint> { target };
                                }
                                else
                                {
                                    head.Unresolved = checked(head.Unresolved + 1);
                                }
                            }
                        }
                    }
                }
                // Type 0x04 is framed by its own candidate-vector reader, so its edges
                // have to be collected here rather than from the shared body result.
                if (objectType == 4 && objectSize >= 5)
                {
                    var vectorStart = checked(cursor + 9);
                    var vectorLength = checked((int)objectSize - 4);
                    var entryCount = payload[vectorStart];
                    if (checked(1 + entryCount * 4) <= vectorLength)
                    {
                        var targets = new List<uint>(entryCount);
                        for (var entry = 0; entry < entryCount; entry++)
                        {
                            targets.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                                payload.AsSpan(checked(vectorStart + 1 + entry * 4), 4)));
                        }
                        bankEdges[objectId] = targets;
                    }
                }
                structure.DeclaredObjectIds.Add(objectId);
                if (objectType == 3 && objectSize >= 10)
                {
                    structure.Type03Targets.Add((
                        payload[checked(cursor + 9)],
                        BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(cursor + 11), 4))));
                    var target = BinaryPrimitives.ReadUInt32LittleEndian(
                        payload.AsSpan(checked(cursor + 11), 4));
                    bankEdges[objectId] = new List<uint> { target };
                }
                if (objectType == 4)
                {
                    RecordType4U32VectorFrame(
                        payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)),
                        bankId,
                        ordinal,
                        objectId,
                        structure,
                        bankObjectTypes,
                        duplicateObjectIds,
                        referrerCounts,
                        referrerOf);
                }
                cursor = checked((int)objectEnd);
            }

            foreach (var pair in bankObjectTypes)
            {
                var typeKey = $"type{pair.Value:X2}";
                structure.ReferenceCensus.ObjectCountsByType.TryGetValue(typeKey, out var typeCount);
                structure.ReferenceCensus.ObjectCountsByType[typeKey] = checked(typeCount + 1);
            }
            structure.ReferenceCensus.DistinctDuplicateObjectIds = checked(
                structure.ReferenceCensus.DistinctDuplicateObjectIds + (uint)duplicateObjectIds.Count);
            MeasureHircReferenceShape(structure.ReferenceCensus, referrerOf);
            foreach (var pair in bankObjectTypes)
            {
                structure.WalkObjectTypes[pair.Key] = pair.Value;
            }
            // The strongest cross-check this format allows. Every type with a parent
            // field names one object there; the reference graph, read independently
            // from entirely different bytes, has each parent naming its children. If
            // the two are inverse, both readings are right about the same relation --
            // and if they ever disagree, one of them has drifted.
            foreach (var pair in structure.ParentFields)
            {
                if (!bankObjectTypes.ContainsKey(pair.Value))
                {
                    structure.ParentField.NamesSomethingOutsideTheBank =
                        checked(structure.ParentField.NamesSomethingOutsideTheBank + 1);
                    continue;
                }
                if (!bankEdges.TryGetValue(pair.Value, out var siblings))
                {
                    structure.ParentField.ParentDeclaresNoChildren =
                        checked(structure.ParentField.ParentDeclaresNoChildren + 1);
                    continue;
                }
                structure.ParentField.Checkable = checked(structure.ParentField.Checkable + 1);
                if (siblings.Contains(pair.Key))
                {
                    structure.ParentField.ParentNamesTheChildBack =
                        checked(structure.ParentField.ParentNamesTheChildBack + 1);
                }
                else
                {
                    structure.ParentField.ParentDoesNotNameTheChild =
                        checked(structure.ParentField.ParentDoesNotNameTheChild + 1);
                    HircBump(
                        structure.ParentField.DisagreementTypes,
                        $"type{bankObjectTypes[pair.Key]:X2}_under_type{bankObjectTypes[pair.Value]:X2}",
                        1);
                }
                HircBump(
                    structure.ParentField.EdgeTypes,
                    $"type{bankObjectTypes[pair.Key]:X2}_to_type{bankObjectTypes[pair.Value]:X2}",
                    1);
            }
            foreach (var pair in bankEdges)
            {
                structure.WalkEdges[pair.Key] = pair.Value;
            }
            foreach (var pair in bankSourceIds)
            {
                structure.WalkSourceIds[pair.Key] = pair.Value;
            }

            if (cursor != bodyEnd)
            {
                throw new InvalidDataException(
                    $"AKPK HIRC cursor mismatch: id={bankId}, cursor={cursor}, end={bodyEnd}, trailing={bodyEnd - cursor}");
            }
        }

        private static void RecordType3ActionFrame(
            ReadOnlySpan<byte> body,
            uint? bankVersion,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            var result = FrameType3ActionBody(body, bankVersion);
            structure.Type3ActionFrameCount = checked(structure.Type3ActionFrameCount + 1);
            structure.Type3ActionBodyBytes = checked(structure.Type3ActionBodyBytes + (uint)body.Length);
            if (result.OperationCode is ushort operationCode)
            {
                structure.Type3ActionOperationCounts.TryGetValue(operationCode, out var operationCount);
                structure.Type3ActionOperationCounts[operationCode] = checked(operationCount + 1);
            }

            if (result.Status == "exact")
            {
                structure.Type3ActionExactCount = checked(structure.Type3ActionExactCount + 1);
                structure.Type3ActionExactCursorBytes = checked(
                    structure.Type3ActionExactCursorBytes + (uint)result.CursorOffset);
                return;
            }
            if (result.Status == "unsupported")
            {
                structure.Type3ActionUnsupportedCount = checked(
                    structure.Type3ActionUnsupportedCount + 1);
            }
            else
            {
                structure.Type3ActionFailedCount = checked(structure.Type3ActionFailedCount + 1);
            }

            var failureCategory = string.IsNullOrEmpty(result.FailureCategory)
                ? result.Status
                : result.FailureCategory;
            structure.Type3ActionFailureCounts.TryGetValue(failureCategory, out var failureCount);
            structure.Type3ActionFailureCounts[failureCategory] = checked(failureCount + 1);
            if (structure.Type3ActionFailureExamples.Count < 8)
            {
                structure.Type3ActionFailureExamples.Add(new EndfieldHircActionFrameFailure
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    OperationCode = result.OperationCode,
                    Status = result.Status,
                    FailureCategory = failureCategory,
                    CursorOffset = result.CursorOffset,
                    ExpectedBytes = result.ExpectedBytes,
                    ActualBytes = result.ActualBytes,
                });
            }
        }

        private static EndfieldHircActionFrameResult FrameType3ActionBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return new EndfieldHircActionFrameResult
                {
                    Status = "unsupported",
                    FailureCategory = "unsupported_bank_version",
                };
            }
            if (body.Length < 2)
            {
                return ActionFrameFailure("truncated_action_type", null, 0, 2, body.Length);
            }

            var actionType = BinaryPrimitives.ReadUInt16LittleEndian(body);
            var operationCode = (ushort)(actionType & 0xFF00);
            if (body.Length < 7)
            {
                return ActionFrameFailure("truncated_action_header", operationCode, 0, 7, body.Length);
            }

            var cursor = 7;
            if (!TryReadByteCount(body, ref cursor, "truncated_scalar_property_count", operationCode, out var scalarCount, out var failure)
                || !TrySkip(body, ref cursor, scalarCount, "truncated_scalar_property_ids", operationCode, out failure)
                || !TrySkip(body, ref cursor, scalarCount * 4, "truncated_scalar_property_values", operationCode, out failure))
            {
                return failure!;
            }
            if (!TryReadByteCount(body, ref cursor, "truncated_range_property_count", operationCode, out var rangeCount, out failure)
                || !TrySkip(body, ref cursor, rangeCount, "truncated_range_property_ids", operationCode, out failure)
                || !TrySkip(body, ref cursor, rangeCount * 8, "truncated_range_property_values", operationCode, out failure))
            {
                return failure!;
            }

            if (operationCode == 0x0400)
            {
                if (!TrySkip(body, ref cursor, 9, "truncated_play_tail", operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x2100)
            {
                // The v150 PlayEvent body ends after the common header and property bundles.
            }
            else if (operationCode == 0x1200 || operationCode == 0x1900)
            {
                if (!TrySkip(body, ref cursor, 8, "truncated_pair_tail", operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x1300 || operationCode == 0x1400)
            {
                if (!TrySkip(body, ref cursor, 15, "truncated_game_parameter_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x6100)
            {
                if (!TrySkip(body, ref cursor, 8, "truncated_rtpc_tail", operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x0100 or 0x0200 or 0x0300)
            {
                if (!TrySkip(body, ref cursor, 2, "truncated_action_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x0600 or 0x0700)
            {
                if (!TrySkip(body, ref cursor, 1, "truncated_fade_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x0800 or 0x0900 or 0x0A00 or 0x0B00 or 0x0C00
                or 0x0D00 or 0x0E00 or 0x0F00 or 0x2000 or 0x3000)
            {
                if (!TrySkip(body, ref cursor, 14, "truncated_value_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x3100 or 0x3200)
            {
                if (!TrySkip(body, ref cursor, 7, "truncated_effect_slot_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x3300 or 0x3400 or 0x3500 or 0x3600 or 0x3700)
            {
                if (!TrySkip(body, ref cursor, 2, "truncated_effect_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x1E00)
            {
                if (!TrySkip(body, ref cursor, 14, "truncated_seek_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x2200)
            {
                if (!TrySkip(body, ref cursor, 1, "truncated_fade_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x1000 or 0x1100 or 0x1A00 or 0x1B00 or 0x1F00)
            {
                // These v150 operation variants have no serialized tail after the common bundles.
            }
            else
            {
                return new EndfieldHircActionFrameResult
                {
                    Status = "unsupported",
                    OperationCode = operationCode,
                    FailureCategory = "unsupported_operation",
                    CursorOffset = cursor,
                };
            }

            if (cursor != body.Length)
            {
                return ActionFrameFailure(
                    "unexpected_trailing_bytes",
                    operationCode,
                    cursor,
                    0,
                    body.Length - cursor);
            }
            return new EndfieldHircActionFrameResult
            {
                Status = "exact",
                OperationCode = operationCode,
                CursorOffset = cursor,
            };
        }

        private static EndfieldHircActionFrameResult ActionFrameFailure(
            string category,
            ushort? operationCode,
            int offset,
            long expectedBytes,
            long actualBytes) => new()
        {
            Status = "failed",
            OperationCode = operationCode,
            FailureCategory = category,
            CursorOffset = offset,
            ExpectedBytes = expectedBytes,
            ActualBytes = actualBytes,
        };

        private static bool TryReadByteCount(
            ReadOnlySpan<byte> body,
            ref int cursor,
            string category,
            ushort operationCode,
            out int count,
            out EndfieldHircActionFrameResult failure)
        {
            if (cursor >= body.Length)
            {
                count = 0;
                failure = ActionFrameFailure(category, operationCode, cursor, 1, 0);
                return false;
            }
            count = body[cursor++];
            failure = null!;
            return true;
        }

        private static bool TrySkip(
            ReadOnlySpan<byte> body,
            ref int cursor,
            int count,
            string category,
            ushort operationCode,
            out EndfieldHircActionFrameResult failure)
        {
            var available = body.Length - cursor;
            if (count < 0 || count > available)
            {
                failure = ActionFrameFailure(category, operationCode, cursor, count, available);
                return false;
            }
            cursor += count;
            failure = null!;
            return true;
        }

        private static bool TrySkipExceptionRows(
            ReadOnlySpan<byte> body,
            ref int cursor,
            ushort operationCode,
            out EndfieldHircActionFrameResult failure)
        {
            ulong count = 0;
            var terminated = false;
            for (var index = 0; index < 5; index++)
            {
                if (cursor >= body.Length)
                {
                    failure = ActionFrameFailure("truncated_exception_count", operationCode, cursor, 1, 0);
                    return false;
                }
                var value = body[cursor++];
                if (index == 4 && (value & 0x80) != 0)
                {
                    failure = ActionFrameFailure("unterminated_exception_count", operationCode, cursor - 1, 1, 1);
                    return false;
                }
                if (index == 4 && (value & 0x70) != 0)
                {
                    failure = ActionFrameFailure("exception_count_overflow", operationCode, cursor - 1, 1, 1);
                    return false;
                }
                count |= (ulong)(value & 0x7F) << (index * 7);
                if ((value & 0x80) == 0)
                {
                    terminated = true;
                    break;
                }
            }
            if (!terminated)
            {
                failure = ActionFrameFailure("unterminated_exception_count", operationCode, cursor, 0, body.Length - cursor);
                return false;
            }

            var available = body.Length - cursor;
            var expected = count * 5UL;
            if (expected > (ulong)available)
            {
                failure = ActionFrameFailure(
                    "exception_rows_out_of_range",
                    operationCode,
                    cursor,
                    checked((long)expected),
                    available);
                return false;
            }
            cursor += checked((int)expected);
            failure = null!;
            return true;
        }

        private static void ParseType2SourcePrefix(
            byte[] payload,
            int bodyStart,
            int bodyLength,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            if (bodyLength < 14)
            {
                throw new InvalidDataException(
                    $"AKPK HIRC type 0x02 source prefix truncated: id={bankId}, ordinal={ordinal}, object={objectId}, expected=14, actual={bodyLength}");
            }

            var pluginId = BitConverter.ToUInt32(payload, bodyStart);
            var pluginType = pluginId & 0x0F;
            var prefixLength = 14;
            if (pluginType == 2)
            {
                if (bodyLength < 18)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC type 0x02 source plugin length truncated: id={bankId}, ordinal={ordinal}, object={objectId}, expected=18, actual={bodyLength}");
                }
                var parameterLength = BitConverter.ToUInt32(payload, checked(bodyStart + 14));
                if (parameterLength > (uint)(bodyLength - 18))
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC type 0x02 source plugin range out of object: id={bankId}, ordinal={ordinal}, object={objectId}, parameterLength={parameterLength}, available={bodyLength - 18}");
                }
                prefixLength = checked(18 + (int)parameterLength);
            }

            structure.Type2PrefixCount = checked(structure.Type2PrefixCount + 1);
            structure.Type2PluginTypeCounts.TryGetValue(pluginType, out var pluginCount);
            structure.Type2PluginTypeCounts[pluginType] = checked(pluginCount + 1);
            // The whole plug-in id, not just its type field. Numeric type 0x0B's source
            // records are checked against this set, and that check only has force if
            // both sides are the same sparse 32-bit value.
            HircBump(structure.Type2PluginIdCounts, $"plugin_{pluginId:X8}", 1);
            if (!structure.Type2SourcesByPlugin.TryGetValue(pluginId, out var pluginSources))
            {
                pluginSources = new HashSet<uint>();
                structure.Type2SourcesByPlugin[pluginId] = pluginSources;
            }
            // The source id is the bounded prefix's third field: plug-in id, stream type,
            // then the id, at body offset five.
            pluginSources.Add(BitConverter.ToUInt32(payload, checked(bodyStart + 5)));
            structure.Type2PrefixBytes = checked(structure.Type2PrefixBytes + (uint)prefixLength);
            var opaqueLength = checked(bodyLength - prefixLength);
            structure.Type2OpaqueTailBytes = checked(structure.Type2OpaqueTailBytes + (uint)opaqueLength);
            structure.Type2MinOpaqueTailBytes = structure.Type2PrefixCount == 1
                ? (uint)opaqueLength
                : Math.Min(structure.Type2MinOpaqueTailBytes, (uint)opaqueLength);
            structure.Type2MaxOpaqueTailBytes = Math.Max(structure.Type2MaxOpaqueTailBytes, (uint)opaqueLength);
        }

        // Breadth-first closure from every object whose identity the caller supplied.
        // Edges that leave the bank are counted, never followed: this corpus cannot
        // say what they name.
        private static void WalkHircNamedReach(
            EndfieldHircNamedReachCensus census,
            HashSet<uint> namedIdentityHashes,
            Dictionary<uint, byte> bankObjectTypes,
            Dictionary<uint, List<uint>> bankEdges,
            Dictionary<uint, List<uint>> bankSourceIds)
        {
            if (namedIdentityHashes == null || namedIdentityHashes.Count == 0)
            {
                return;
            }
            foreach (var pair in bankObjectTypes)
            {
                if (!namedIdentityHashes.Contains(pair.Key))
                {
                    continue;
                }
                census.MatchedObjects = checked(census.MatchedObjects + 1);
                var typeKey = $"type{pair.Value:X2}";
                census.MatchesByObjectType.TryGetValue(typeKey, out var typeCount);
                census.MatchesByObjectType[typeKey] = checked(typeCount + 1);
                if (pair.Value != 4)
                {
                    continue;
                }
                census.MatchedNamedType = checked(census.MatchedNamedType + 1);

                var seen = new HashSet<uint> { pair.Key };
                var queue = new Queue<uint>();
                queue.Enqueue(pair.Key);
                var sources = new HashSet<uint>();
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!bankEdges.TryGetValue(current, out var references))
                    {
                        continue;
                    }
                    foreach (var reference in references)
                    {
                        if (!bankObjectTypes.ContainsKey(reference))
                        {
                            census.WalkEdgesLeavingThePackage =
                                checked(census.WalkEdgesLeavingThePackage + 1);
                            continue;
                        }
                        if (!seen.Add(reference))
                        {
                            continue;
                        }
                        // Which types the walk actually arrives at. Without this the
                        // report says how many sources were reached but not where the
                        // walk stopped, and those are different questions.
                        HircBump(
                            census.ReachedObjectTypes,
                            $"type{bankObjectTypes[reference]:X2}",
                            1);
                        if (bankSourceIds.TryGetValue(reference, out var owned))
                        {
                            foreach (var sourceId in owned)
                            {
                                sources.Add(sourceId);
                            }
                        }
                        queue.Enqueue(reference);
                    }
                }
                if (sources.Count > 0)
                {
                    census.ReachingASource = checked(census.ReachingASource + 1);
                }
                else
                {
                    census.ReachingNoSource = checked(census.ReachingNoSource + 1);
                }
                census.ReachedSourceIds = checked(census.ReachedSourceIds + (uint)sources.Count);
                var identity = pair.Key.ToString("X8");
                census.ReachedSourceIdsByIdentity.TryGetValue(identity, out var existing);
                census.ReachedSourceIdsByIdentity[identity] =
                    Math.Max(existing, (uint)sources.Count);
                if (!census.ReachedSourceIdListByIdentity.TryGetValue(identity, out var reached))
                {
                    reached = new SortedSet<uint>();
                    census.ReachedSourceIdListByIdentity[identity] = reached;
                }
                foreach (var sourceId in sources)
                {
                    reached.Add(sourceId);
                }
            }
        }

        // Depth and acyclicity of the reference relation inside one bank. In-degree at
        // most one does not by itself exclude a cycle, so walk it.
        private static void MeasureHircReferenceShape(
            EndfieldHircReferenceCensus census,
            Dictionary<uint, uint> referrerOf)
        {
            var depth = new Dictionary<uint, uint>();
            foreach (var start in referrerOf.Keys)
            {
                if (depth.ContainsKey(start))
                {
                    continue;
                }
                var path = new List<uint>();
                var onPath = new HashSet<uint>();
                var node = start;
                uint baseDepth = 0;
                while (true)
                {
                    if (depth.TryGetValue(node, out var known))
                    {
                        baseDepth = known;
                        break;
                    }
                    // A node with no referrer is a root and sits at depth zero, so depth
                    // counts references traversed rather than nodes visited.
                    if (!referrerOf.TryGetValue(node, out var parent))
                    {
                        break;
                    }
                    if (!onPath.Add(node))
                    {
                        // Every node still on the walk belongs to a cycle or feeds one.
                        census.ReferenceCycleOrFeedingNodes =
                            checked(census.ReferenceCycleOrFeedingNodes + (uint)path.Count);
                        foreach (var member in path)
                        {
                            depth[member] = 0;
                        }
                        path.Clear();
                        break;
                    }
                    path.Add(node);
                    node = parent;
                }
                for (var i = path.Count - 1; i >= 0; i--)
                {
                    baseDepth = checked(baseDepth + 1);
                    depth[path[i]] = baseDepth;
                    if (baseDepth > census.MaximumReferenceDepth)
                    {
                        census.MaximumReferenceDepth = baseDepth;
                    }
                }
            }
        }

        // Join each anonymous reference to the bank's object identities. Resolution is
        // an identity fact; it says nothing about direction, containment or meaning.
        private static void ResolveHircReferences(
            EndfieldHircReferenceCensus census,
            EndfieldHircBodyFrameResult frame,
            byte sourceType,
            uint sourceId,
            Dictionary<uint, byte> bankObjectTypes,
            HashSet<uint> duplicateObjectIds,
            Dictionary<uint, uint> referrerCounts,
            Dictionary<uint, uint> referrerOf)
        {
            if (frame.Status != "exact" || frame.References == null)
            {
                return;
            }
            foreach (var word in frame.CandidateWords ?? new List<uint>())
            {
                census.CandidateWords = checked(census.CandidateWords + 1);
                if (bankObjectTypes.ContainsKey(word))
                {
                    census.CandidateWordsMatchingAnObject =
                        checked(census.CandidateWordsMatchingAnObject + 1);
                }
            }
            foreach (var reference in frame.References)
            {
                census.References = checked(census.References + 1);
                if (reference == sourceId)
                {
                    census.SelfReferences = checked(census.SelfReferences + 1);
                }
                referrerCounts.TryGetValue(reference, out var referrers);
                referrerCounts[reference] = checked(referrers + 1);
                if (referrers == 0)
                {
                    referrerOf[reference] = sourceId;
                }
                if (referrers == 1)
                {
                    census.TargetsWithMultipleReferrers =
                        checked(census.TargetsWithMultipleReferrers + 1);
                }
                // A reference into a duplicated id cannot name one object, so count it
                // rather than let first-wins lookup hide the ambiguity.
                if (duplicateObjectIds.Contains(reference))
                {
                    census.ReferencesToDuplicateIds =
                        checked(census.ReferencesToDuplicateIds + 1);
                }
                if (bankObjectTypes.TryGetValue(reference, out var targetType))
                {
                    census.ResolvedSameBank = checked(census.ResolvedSameBank + 1);
                    var edge = $"type{sourceType:X2}_to_type{targetType:X2}";
                    census.EdgeCounts.TryGetValue(edge, out var edgeCount);
                    census.EdgeCounts[edge] = checked(edgeCount + 1);
                }
                else
                {
                    census.UnresolvedInBank = checked(census.UnresolvedInBank + 1);
                }
            }
        }

        private static void RecordHircBodyFrame(
            EndfieldHircBodyCensus census,
            EndfieldHircBodyFrameResult result,
            ReadOnlySpan<byte> body,
            ulong bankId,
            uint ordinal,
            uint objectId)
        {
            census.FrameCount = checked(census.FrameCount + 1);
            census.BodyBytes = checked(census.BodyBytes + (uint)body.Length);
            if (result.Status == "exact")
            {
                census.ExactCount = checked(census.ExactCount + 1);
                census.ExactCursorBytes = checked(census.ExactCursorBytes + (uint)result.CursorOffset);
                census.MinExactBytes = census.ExactCount == 1
                    ? (uint)result.CursorOffset
                    : Math.Min(census.MinExactBytes, (uint)result.CursorOffset);
                census.MaxExactBytes = Math.Max(census.MaxExactBytes, (uint)result.CursorOffset);
                foreach (var pair in result.GroupCounts)
                {
                    census.GroupCounts.TryGetValue(pair.Key, out var groupTotal);
                    census.GroupCounts[pair.Key] = checked(groupTotal + pair.Value);
                    if (pair.Value == 0)
                    {
                        continue;
                    }
                    // How many bodies exercise the group at all, and the most any one
                    // body contributes. A total on its own cannot distinguish a group
                    // seen in a hundred bodies from one seen in a single body that
                    // happens to declare a hundred entries -- and the difference is
                    // the difference between an established layout and an anecdote.
                    census.GroupBodies.TryGetValue(pair.Key, out var bodies);
                    census.GroupBodies[pair.Key] = checked(bodies + 1);
                    census.GroupMaxInOneBody.TryGetValue(pair.Key, out var most);
                    census.GroupMaxInOneBody[pair.Key] = Math.Max(most, pair.Value);
                }
                foreach (var pair in result.SelectorCounts)
                {
                    census.SelectorCounts.TryGetValue(pair.Key, out var selectorTotal);
                    census.SelectorCounts[pair.Key] = checked(selectorTotal + pair.Value);
                }
                return;
            }

            census.NonExactBytes = checked(census.NonExactBytes + (uint)body.Length);
            if (result.Status == "unsupported")
            {
                census.UnsupportedCount = checked(census.UnsupportedCount + 1);
                census.UnsupportedCategories.TryGetValue(result.Category, out var unsupported);
                census.UnsupportedCategories[result.Category] = checked(unsupported + 1);
            }
            else
            {
                census.FailedCount = checked(census.FailedCount + 1);
                census.FailureCounts.TryGetValue(result.Category, out var failed);
                census.FailureCounts[result.Category] = checked(failed + 1);
            }

            if (census.FailureExamples.Count < 8)
            {
                census.FailureExamples.Add(new EndfieldHircBodyFrameExample
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    Status = result.Status,
                    Category = result.Category,
                    CursorOffset = result.CursorOffset,
                    ExpectedBytes = result.ExpectedBytes,
                    ActualBytes = result.ActualBytes,
                });
            }
        }

        // Anonymous group framing for numeric HIRC type 0x02 bodies. Group letters are
        // deliberate: the corpus proves byte extents, not field ownership or meaning.
        internal static EndfieldHircBodyFrameResult FrameType2Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;

            // Bounded 14-byte source prefix; plugin kind 2 adds a checked length-prefixed range.
            if (!HircTake(body, ref cursor, 4, out var failure, "prefixPluginId"))
            {
                return failure;
            }
            var pluginId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor - 4, 4));
            if (!HircTake(body, ref cursor, 10, out failure, "prefixSourceInfo"))
            {
                return failure;
            }
            if ((pluginId & 0x0F) == 2 && pluginId != 0)
            {
                if (!HircReadUInt32(body, ref cursor, out var parameterLength, out failure, "prefixParamLength"))
                {
                    return failure;
                }
                if (parameterLength > (uint)(body.Length - cursor))
                {
                    return HircFrameOutcome(
                        "failed", "range_prefixParams", cursor, parameterLength, body.Length - cursor);
                }
                cursor = checked(cursor + (int)parameterLength);
                HircBump(groups, "prefixParamBytes", parameterLength);
            }

            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out failure))
            {
                return failure;
            }

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // The nine node groups are shared by every HIRC type whose body opens with
        // this node frame. Extents are proven by whole-corpus exact closure, and the
        // field names come from the Wwise 2023.1.17 SDK's own deserializer
        // (CAkParameterNodeBase::SetNodeBaseParams and the readers it calls, read
        // from the SDK's symbolized static library). The group letters are kept as
        // the report keys so earlier reports stay diffable.
        private static bool FrameHircNodeGroups(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            // Group A = NodeInitialFxParams (CAkParameterNode::SetInitialFxParams):
            // u8 bIsOverrideParentFX, u8 uNumFx, then only when uNumFx > 0 one
            // u8 bitsFXBypass, then uNumFx x { u8 uFXIndex, u32 fxID, u8 flags } with
            // flags bit0 bypass, bit1 bIsShareSet, bit2 bIsRendered.
            if (!HircReadByte(body, ref cursor, out var groupAFlag, out failure, "groupAFlag"))
            {
                return false;
            }
            HircBump(selectors, $"groupAFlag_{groupAFlag:X2}", 1);
            if (!HircReadByte(body, ref cursor, out var groupACount, out failure, "groupACount"))
            {
                return false;
            }
            if (groupACount > 0)
            {
                if (!HircTake(body, ref cursor, 1, out failure, "groupAMask"))
                {
                    return false;
                }
                if (!HircTake(body, ref cursor, groupACount * 6, out failure, "groupAEntries"))
                {
                    return false;
                }
            }
            HircBump(groups, "groupAEntries", groupACount);

            // Group B = NodeInitialMetadataParams (SetInitialMetadataParams):
            // u8 bOverrideParentMetadata, u8 uNumFx, then uNumFx x { u8 uFXIndex,
            // u32 fxID, u8 bIsShareSet }. No body in the current corpus carries a
            // nonempty vector; the six-byte element is the SDK reader's, not a fit.
            if (!HircReadByte(body, ref cursor, out var groupBFlag, out failure, "groupBFlag"))
            {
                return false;
            }
            HircBump(selectors, $"groupBFlag_{groupBFlag:X2}", 1);
            if (!HircReadByte(body, ref cursor, out var groupBCount, out failure, "groupBCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, groupBCount * 6, out failure, "groupBEntries"))
            {
                return false;
            }
            HircBump(groups, "groupBEntries", groupBCount);

            // u32 OverrideBusId (0 = none), u32 DirectParentID (0 = none), u8 byBitVector
            // with bit0 bPriorityOverrideParent, bit1 bPriorityApplyDistFactor and the
            // MIDI override bits above them. Kept opaque here: the reference graph is
            // where the two ids are joined.
            if (!HircTake(body, ref cursor, 9, out failure, "anonymousScalars"))
            {
                return false;
            }

            // Group C = NodeInitialParams PropBundle (CAkParameterNode::SetInitialParams):
            // u8 cProps, cProps x u8 AkPropID, then cProps x u32 value, as two runs.
            if (!HircReadByte(body, ref cursor, out var groupCCount, out failure, "groupCCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, groupCCount * 5, out failure, "groupCEntries"))
            {
                return false;
            }
            HircBump(groups, "groupCEntries", groupCCount);

            // Group D = the ranged PropBundle: u8 cProps, cProps x u8 AkPropID, then
            // cProps x { f32 min, f32 max }.
            if (!HircReadByte(body, ref cursor, out var groupDCount, out failure, "groupDCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, groupDCount * 9, out failure, "groupDEntries"))
            {
                return false;
            }
            HircBump(groups, "groupDEntries", groupDCount);

            if (!FrameHircGroupE(body, ref cursor, groups, selectors, out failure))
            {
                return false;
            }
            if (!FrameHircGroupF(body, ref cursor, selectors, out failure))
            {
                return false;
            }
            // Group G = AdvSettingsParams (CAkParameterNode::SetAdvSettingsParams):
            // u8 byBitVector (bit0 bKillNewest, bit1 bUseVirtualBehavior, bit2
            // bIgnoreParentMaxNumInst, bit3 bIsGlobalLimit, bit4 bVVoicesOptOverrideParent),
            // u8 eVirtualQueueBehavior, u16 u16MaxNumInstance, u8 eBelowThresholdBehavior,
            // u8 byBitVector2 (HDR envelope / loudness bits).
            if (!HircTake(body, ref cursor, 6, out failure, "groupG"))
            {
                return false;
            }
            if (!FrameHircGroupH(body, ref cursor, groups, selectors, out failure))
            {
                return false;
            }
            return FrameHircGroupI(body, ref cursor, groups, selectors, out failure);
        }

        private static EndfieldHircBodyFrameResult HircFrameExact(
            int cursor,
            int length,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            List<uint> references = null,
            List<uint> candidateWords = null) => new()
            {
                Status = "exact",
                Category = "",
                CursorOffset = cursor,
                ExpectedBytes = length,
                ActualBytes = cursor,
                GroupCounts = groups,
                SelectorCounts = selectors,
                References = references ?? new List<uint>(),
                CandidateWords = candidateWords ?? new List<uint>(),
            };

        // Numeric HIRC type 0x06 opens with the shared node groups, then a fixed
        // ten-byte opaque header, one counted vector of four-byte anonymous references,
        // one counted list of groups each holding its own counted reference vector, and
        // one counted vector of fourteen-byte records that open with a reference.
        internal static EndfieldHircBodyFrameResult FrameType6Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var references = new List<uint>();
            // Group items and record leading words match a bank object most of the time but
            // not always, so they are not claimed as references; they are counted instead.
            var candidateWords = new List<uint>();
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            // The header carries two id-shaped words that no bank object matches, so they
            // are consumed opaquely rather than offered to the reference join.
            if (!HircTake(body, ref cursor, 10, out failure, "suffixHeader"))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var childCount, out failure, "childCount"))
            {
                return failure;
            }
            if (childCount > (uint)((body.Length - cursor) / 4))
            {
                return HircFrameOutcome(
                    "failed", "range_childEntries", cursor - 4, (body.Length - cursor) / 4, childCount);
            }
            for (var i = 0U; i < childCount; i++)
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 4);
            }
            HircBump(groups, "childEntries", childCount);

            if (!HircReadUInt32(body, ref cursor, out var groupCount, out failure, "groupCount"))
            {
                return failure;
            }
            if (groupCount > (uint)((body.Length - cursor) / 8))
            {
                return HircFrameOutcome(
                    "failed", "range_groupEntries", cursor - 4, (body.Length - cursor) / 8, groupCount);
            }
            HircBump(groups, "groupEntries", groupCount);
            HircBump(groups, "groupItemEntries", 0);
            for (var i = 0U; i < groupCount; i++)
            {
                // The group's own key is not a bank object identity either.
                if (!HircTake(body, ref cursor, 4, out failure, "groupKey"))
                {
                    return failure;
                }
                if (!HircReadUInt32(body, ref cursor, out var itemCount, out failure, "groupItemCount"))
                {
                    return failure;
                }
                if (itemCount > (uint)((body.Length - cursor) / 4))
                {
                    return HircFrameOutcome(
                        "failed", "range_groupItemEntries", cursor - 4, (body.Length - cursor) / 4, itemCount);
                }
                for (var item = 0U; item < itemCount; item++)
                {
                    candidateWords.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                    cursor = checked(cursor + 4);
                }
                HircBump(groups, "groupItemEntries", itemCount);
            }

            if (!HircReadUInt32(body, ref cursor, out var recordCount, out failure, "recordCount"))
            {
                return failure;
            }
            if (recordCount > (uint)((body.Length - cursor) / 14))
            {
                return HircFrameOutcome(
                    "failed", "range_recordEntries", cursor - 4, (body.Length - cursor) / 14, recordCount);
            }
            for (var i = 0U; i < recordCount; i++)
            {
                candidateWords.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 14);
            }
            HircBump(groups, "recordEntries", recordCount);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, references, candidateWords);
        }

        // Numeric HIRC type 0x05 opens with the shared node groups, then a fixed opaque
        // block, one counted vector of four-byte anonymous references and one counted
        // vector of eight-byte anonymous records. The two counts are independent.
        internal static EndfieldHircBodyFrameResult FrameType5Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircTake(body, ref cursor, 24, out failure, "suffixHeader"))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var referenceCount, out failure, "referenceCount"))
            {
                return failure;
            }
            if (referenceCount > (uint)((body.Length - cursor) / 4))
            {
                return HircFrameOutcome(
                    "failed", "range_referenceEntries", cursor - 4, (body.Length - cursor) / 4, referenceCount);
            }
            var references = new List<uint>(checked((int)referenceCount));
            for (var i = 0U; i < referenceCount; i++)
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 4);
            }
            HircBump(groups, "referenceEntries", referenceCount);
            if (!HircReadUInt16(body, ref cursor, out var recordCount, out failure, "recordCount"))
            {
                return failure;
            }
            if (recordCount > (body.Length - cursor) / 8)
            {
                return HircFrameOutcome(
                    "failed", "range_recordEntries", cursor - 2, (body.Length - cursor) / 8, recordCount);
            }
            cursor = checked(cursor + recordCount * 8);
            HircBump(groups, "recordEntries", recordCount);
            // Publish how often the two counts disagree so "the vectors are counted
            // independently" is a corpus measurement rather than an assertion.
            HircBump(groups, "referenceRecordCountMismatch", referenceCount == recordCount ? 0u : 1u);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, references);
        }

        // Numeric HIRC type 0x07 opens with the shared node groups and ends with one
        // counted vector of fixed-width anonymous references.
        internal const int Type14ElementBytes = 12;
        // The source record numeric types 0x02 and 0x0B share.
        internal const int SourceRecordBytes = 14;
        // Bound on the counted source run a CAkMusicTrack body declares before
        // its ids are offered to the walk; the lane framer has no such cap.
        internal const uint SourceRecordMaximumCount = 64;
        internal const int SourceRecordIdOffset = 5;
        // Numeric type 0x0B's entry area: a 32-bit entry count, then that many entries.
        // Each entry opens with 48 header bytes whose word at 44 counts the elements
        // that follow.
        // The music types' parent reference. Found by asking which FRONT offsets each
        // type's references use -- the census measures distance from the end, and from
        // the end these types look entirely unlocated. At offset 9: 0x0A names an
        // object in 97% of its bodies, 0x0C in 96%, 0x0D in 95%.
        internal const int Type0CParentOffset = 9;
        // Numeric type 0x0C carries a counted array of references whose count sits at
        // 32 + 5 * body[14]. The selector byte counts five-byte optional fields, the
        // same five-byte step the element trailer and the entry widths use.
        internal const int Type0CArrayBaseOffset = 32;
        internal const int Type0CArrayStepBytes = 5;
        internal const int Type0CArraySelectorOffset = 14;
        internal const byte Type0CArrayMaximumSelector = 8;
        internal const uint Type0CArrayMaximumLength = 64;
        // Scored against neighbouring bases and against other steps. The base is
        // decisive on its own -- 704 arrays resolve fully at 32 and none at 30, 31,
        // 33, 34, 36 or 40. The step is decisive on coverage: 5 reaches 704 bodies
        // where 0, 4, 6 and 8 reach 564, and the 140 it adds are exactly the bodies
        // whose selector is nonzero.
        internal static readonly int[] Type0CArrayRivalBases = { 28, 30, 31, 33, 34, 36, 40 };
        // After the array comes a fixed 96-byte region, then a flag, then a 27-byte
        // block the flag gates, then the next reference. The flag is INSIDE the region
        // rather than in the body header, which is why a header scan never found it.
        //
        // The multiplier form holds for 0 and 1 only: flag 0 puts the next reference 99
        // bytes after the array and flag 1 puts it 126, but flag 2 gives 155, 167 or
        // 215 rather than the 153 a repeat would predict. So this is a gate on one
        // block, not a count of them, and values above 1 are censused as unexplained.
        internal const int Type0CRegionFlagOffset = 96;
        internal const int Type0CRegionBaseGap = 99;
        internal const int Type0CRegionBlockBytes = 27;
        internal const byte Type0CRegionMaximumFlag = 1;
        // The parent field: an object naming the object that owns it, at a fixed front
        // offset. Found by sweeping front offsets and asking which carry a reference in
        // most bodies, then keeping only those whose target names the child back.
        //
        // Two offsets the sweep also turned up are NOT parents and are excluded, which
        // the inverse test is what established:
        //   * 0x04 at offset 1 names a 0x03, and the reference graph has 0x04 naming
        //     0x03 too -- the same direction, so it is that edge and not its inverse.
        //     22,317 of its 22,335 cases disagreed.
        //   * The music types at offset 9 are the head reference the reader already
        //     censuses as musicHeadReferences (7,084 at offset 9, 242 at offset 5 by
        //     byte 2). Also downward, also not a parent.
        internal static readonly (byte Type, int Offset)[] ParentFieldOffsets =
        {
            (2, 22), (5, 8), (6, 8), (7, 8), (9, 8),
        };
        internal static EndfieldHircBodyFrameResult FrameType7Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var childCount, out failure, "childCount"))
            {
                return failure;
            }
            if (childCount > (uint)((body.Length - cursor) / 4))
            {
                return HircFrameOutcome(
                    "failed", "range_childEntries", cursor - 4, (body.Length - cursor) / 4, childCount);
            }
            var references = new List<uint>(checked((int)childCount));
            for (var i = 0U; i < childCount; i++)
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 4);
            }
            HircBump(groups, "childEntries", childCount);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, references);
        }


        // Numeric HIRC type 0x09 is CAkLayerCntr. Read from the SDK deserializer
        // (CAkLayerCntr::SetInitialValues, then CAkLayer::SetInitialValues per layer):
        // the shared node frame, u32 ulNumChilds x u32 childID, u32 ulNumLayers x
        // { u32 ulLayerID, InitialRTPC, u32 rtpcID, u8 rtpcType, u32 ulNumAssoc x
        // { u32 ulAssociatedChildID, u32 ulCurveSize x { f32 from, f32 to, u32
        // interpolation } } }, then u8 bIsContinuousValidation. A layer's InitialRTPC
        // is the same structure as group I -- it is the same engine template -- so it
        // is framed by the group I reader and counted under the group I keys. The
        // child vector is the same parent-to-child relation numeric type 0x07 carries,
        // so it joins the reference graph; the per-layer associated child ids repeat
        // those children and stay out of it.
        internal static EndfieldHircBodyFrameResult FrameType9Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var childCount, out failure, "childCount"))
            {
                return failure;
            }
            if (childCount > (uint)((body.Length - cursor) / 4))
            {
                return HircFrameOutcome(
                    "failed", "range_childEntries", cursor - 4, (body.Length - cursor) / 4, childCount);
            }
            var references = new List<uint>(checked((int)childCount));
            for (var i = 0U; i < childCount; i++)
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 4);
            }
            HircBump(groups, "childEntries", childCount);

            if (!HircReadUInt32(body, ref cursor, out var layerCount, out failure, "layerCount"))
            {
                return failure;
            }
            // A layer is at least its id, an empty curve count, the RTPC id and type,
            // and an empty association count.
            if (layerCount > (uint)((body.Length - cursor) / 15))
            {
                return HircFrameOutcome(
                    "failed", "range_layerEntries", cursor - 4, (body.Length - cursor) / 15, layerCount);
            }
            HircBump(groups, "layerEntries", layerCount);
            HircBump(groups, "layerAssocEntries", 0);
            HircBump(groups, "layerAssocPoints", 0);
            for (var layer = 0U; layer < layerCount; layer++)
            {
                if (!HircTake(body, ref cursor, 4, out failure, "layerId"))
                {
                    return failure;
                }
                if (!FrameHircGroupI(body, ref cursor, groups, selectors, out failure))
                {
                    return failure;
                }
                if (!HircTake(body, ref cursor, 5, out failure, "layerRtpc"))
                {
                    return failure;
                }
                if (!HircReadUInt32(body, ref cursor, out var assocCount, out failure, "layerAssocCount"))
                {
                    return failure;
                }
                if (assocCount > (uint)((body.Length - cursor) / 8))
                {
                    return HircFrameOutcome(
                        "failed", "range_layerAssocEntries", cursor - 4, (body.Length - cursor) / 8, assocCount);
                }
                HircBump(groups, "layerAssocEntries", assocCount);
                for (var assoc = 0U; assoc < assocCount; assoc++)
                {
                    if (!HircTake(body, ref cursor, 4, out failure, "layerAssocChild"))
                    {
                        return failure;
                    }
                    if (!HircReadUInt32(body, ref cursor, out var pointCount, out failure, "layerAssocPointCount"))
                    {
                        return failure;
                    }
                    if (pointCount > (uint)((body.Length - cursor) / 12))
                    {
                        return HircFrameOutcome(
                            "failed", "range_layerAssocPoints", cursor - 4, (body.Length - cursor) / 12, pointCount);
                    }
                    cursor = checked(cursor + (int)pointCount * 12);
                    HircBump(groups, "layerAssocPoints", pointCount);
                }
            }
            if (!HircReadByte(body, ref cursor, out var tail, out failure, "type09Tail"))
            {
                return failure;
            }
            HircBump(selectors, $"type09Tail_{tail:X2}", 1);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, references);
        }


        // Music node parameters, read from the SDK's AkMusicEngine library
        // (CAkMusicNode::SetMusicNodeParams): u8 uFlags, then the shared node frame,
        // u32 ulNumChilds x u32 childID, AkMeterInfo (f64 fGridPeriod, f64 fGridOffset,
        // f32 fTempo, u8 uTimeSigNumBeatsBar, u8 uTimeSigBeatValue, u8 bMeterInfoFlag),
        // and u32 numStingers x { u32 TriggerID, u32 SegmentID, u32 SyncPlayAt, u32
        // uCueFilterHash, s32 DontRepeatTime, u32 numSegmentLookAhead }. This is why
        // the music types never opened with the node frame at offset 0: it sits one
        // flag byte in.
        private const int MusicMeterInfoBytes = 23;
        private const int MusicStingerBytes = 24;
        private const int MusicTransitionSourceRuleBytes = 21;
        private const int MusicTransitionDestinationRuleBytes = 26;
        private const int MusicTransitionObjectBytes = 30;
        private const int MusicTransitionRuleMinimumBytes = 4 + 4
            + MusicTransitionSourceRuleBytes + MusicTransitionDestinationRuleBytes + 1;

        private static bool FrameMusicNodeParams(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var flags, out failure, "musicFlags"))
            {
                return false;
            }
            HircBump(selectors, $"musicFlags_{flags:X2}", 1);
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out failure))
            {
                return false;
            }
            if (!HircReadUInt32(body, ref cursor, out var childCount, out failure, "childCount"))
            {
                return false;
            }
            if (childCount > (uint)((body.Length - cursor) / 4))
            {
                failure = HircFrameOutcome(
                    "failed", "range_childEntries", cursor - 4, (body.Length - cursor) / 4, childCount);
                return false;
            }
            cursor = checked(cursor + (int)childCount * 4);
            HircBump(groups, "childEntries", childCount);
            if (!HircTake(body, ref cursor, MusicMeterInfoBytes, out failure, "meterInfo"))
            {
                return false;
            }
            if (!HircReadUInt32(body, ref cursor, out var stingerCount, out failure, "stingerCount"))
            {
                return false;
            }
            if (stingerCount > (uint)((body.Length - cursor) / MusicStingerBytes))
            {
                failure = HircFrameOutcome(
                    "failed", "range_stingerEntries", cursor - 4,
                    (body.Length - cursor) / MusicStingerBytes, stingerCount);
                return false;
            }
            cursor = checked(cursor + (int)stingerCount * MusicStingerBytes);
            HircBump(groups, "stingerEntries", stingerCount);
            return true;
        }

        // Transition-aware music nodes (CAkMusicTransAware::SetMusicTransNodeParams):
        // the music node parameters, then u32 numRules x { u32 numSrc x u32 srcID, u32
        // numDst x u32 dstID, a 21-byte source rule (s32 transitionTime, u32 eFadeCurve,
        // s32 iFadeOffset, u32 eSyncType, u32 uCueFilterHash, u8 bPlayPostExit), a
        // 26-byte destination rule (s32 transitionTime, u32 eFadeCurve, s32 iFadeOffset,
        // u32 uCueFilterHash, u32 uJumpToID, u16 eJumpToType, u16 eEntryType, u8
        // bPlayPreEntry, u8 bDestMatchSourceCueName), u8 bAllocTransObjectFlag and,
        // when that flag is nonzero, a 30-byte transition object (u32 segmentID, two
        // fades of s32 transitionTime, u32 eFadeCurve, s32 iFadeOffset, u8 bPlayPreEntry,
        // u8 bPlayPostExit) }.
        private static bool FrameMusicTransNodeParams(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!FrameMusicNodeParams(body, ref cursor, groups, selectors, out failure))
            {
                return false;
            }
            if (!HircReadUInt32(body, ref cursor, out var ruleCount, out failure, "transitionRuleCount"))
            {
                return false;
            }
            if (ruleCount > (uint)((body.Length - cursor) / MusicTransitionRuleMinimumBytes))
            {
                failure = HircFrameOutcome(
                    "failed", "range_transitionRuleEntries", cursor - 4,
                    (body.Length - cursor) / MusicTransitionRuleMinimumBytes, ruleCount);
                return false;
            }
            HircBump(groups, "transitionRuleEntries", ruleCount);
            HircBump(groups, "transitionRuleSourceEntries", 0);
            HircBump(groups, "transitionRuleDestinationEntries", 0);
            HircBump(groups, "transitionObjectEntries", 0);
            for (var rule = 0U; rule < ruleCount; rule++)
            {
                foreach (var side in new[] { "transitionRuleSourceEntries", "transitionRuleDestinationEntries" })
                {
                    if (!HircReadUInt32(body, ref cursor, out var idCount, out failure, side + "Count"))
                    {
                        return false;
                    }
                    if (idCount > (uint)((body.Length - cursor) / 4))
                    {
                        failure = HircFrameOutcome(
                            "failed", "range_" + side, cursor - 4, (body.Length - cursor) / 4, idCount);
                        return false;
                    }
                    cursor = checked(cursor + (int)idCount * 4);
                    HircBump(groups, side, idCount);
                }
                if (!HircTake(body, ref cursor, MusicTransitionSourceRuleBytes, out failure, "transitionSourceRule"))
                {
                    return false;
                }
                if (!HircTake(body, ref cursor, MusicTransitionDestinationRuleBytes, out failure, "transitionDestinationRule"))
                {
                    return false;
                }
                if (!HircReadByte(body, ref cursor, out var allocFlag, out failure, "transitionObjectFlag"))
                {
                    return false;
                }
                HircBump(selectors, $"transitionObject_{allocFlag:X2}", 1);
                if (allocFlag != 0)
                {
                    if (!HircTake(body, ref cursor, MusicTransitionObjectBytes, out failure, "transitionObject"))
                    {
                        return false;
                    }
                    HircBump(groups, "transitionObjectEntries", 1);
                }
            }
            return true;
        }

        // Numeric HIRC type 0x0A is CAkMusicSegment (CAkMusicSegment::SetInitialValues):
        // the music node parameters, f64 fDuration, and u32 ulNumMarkers x { u32 id,
        // f64 fPosition, a NUL-terminated name }. The name is why no fixed stride ever
        // framed this type from the corpus alone.
        internal static EndfieldHircBodyFrameResult FrameType0ABody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameMusicNodeParams(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircTake(body, ref cursor, 8, out failure, "segmentDuration"))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var markerCount, out failure, "markerCount"))
            {
                return failure;
            }
            // A marker is at least its id, its position and an empty name's terminator.
            if (markerCount > (uint)((body.Length - cursor) / 13))
            {
                return HircFrameOutcome(
                    "failed", "range_markerEntries", cursor - 4, (body.Length - cursor) / 13, markerCount);
            }
            HircBump(groups, "markerEntries", markerCount);
            HircBump(groups, "markerNameBytes", 0);
            for (var marker = 0U; marker < markerCount; marker++)
            {
                if (!HircTake(body, ref cursor, 12, out failure, "markerHead"))
                {
                    return failure;
                }
                var terminator = body[cursor..].IndexOf((byte)0);
                if (terminator < 0)
                {
                    return HircFrameOutcome(
                        "failed", "unterminated_markerName", cursor, 1, body.Length - cursor);
                }
                HircBump(groups, "markerNameBytes", (uint)terminator);
                cursor = checked(cursor + terminator + 1);
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // Numeric HIRC type 0x0B is CAkMusicTrack (CAkMusicTrack::SetInitialValues):
        // u8 uFlags, u32 numSources x the same source record numeric type 0x02 carries
        // (CAkBankMgr::LoadSource, with a length-prefixed parameter block for source
        // plug-ins), u32 numPlaylistItem x 44 bytes (u32 trackID, u32 sourceID, u32
        // eventID, f64 fPlayAt, f64 fBeginTrimOffset, f64 fEndTrimOffset, f64
        // fSrcDuration), u32 numSubTrack only when that playlist is nonempty, u32
        // numClipAutomationItem x { u32
        // uClipIndex, u32 eAutoType, u32 uNumPoints x 12-byte points }, then the
        // shared node frame, u8 eTrackType and, only for type 3 (switch), u8
        // eGroupType, u32 uGroupID, u32 uDefaultSwitch, u32 numSwitchAssoc x u32 and
        // a 32-byte transition block; finally s32 iLookAheadTime. The node frame sits
        // after the media lists here, unlike every other node type.
        internal static EndfieldHircBodyFrameResult FrameType0BBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!HircReadByte(body, ref cursor, out var flags, out var failure, "musicFlags"))
            {
                return failure;
            }
            HircBump(selectors, $"musicFlags_{flags:X2}", 1);
            if (!HircReadUInt32(body, ref cursor, out var sourceCount, out failure, "sourceCount"))
            {
                return failure;
            }
            if (sourceCount > (uint)((body.Length - cursor) / SourceRecordBytes))
            {
                return HircFrameOutcome(
                    "failed", "range_sourceEntries", cursor - 4,
                    (body.Length - cursor) / SourceRecordBytes, sourceCount);
            }
            HircBump(groups, "sourceEntries", sourceCount);
            HircBump(groups, "sourceParamBytes", 0);
            for (var source = 0U; source < sourceCount; source++)
            {
                if (!HircTake(body, ref cursor, 4, out failure, "sourcePluginId"))
                {
                    return failure;
                }
                var pluginId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor - 4, 4));
                if (!HircTake(body, ref cursor, SourceRecordBytes - 4, out failure, "sourceInfo"))
                {
                    return failure;
                }
                if ((pluginId & 0x0F) == 2 && pluginId != 0)
                {
                    if (!HircReadUInt32(body, ref cursor, out var parameterLength, out failure, "sourceParamLength"))
                    {
                        return failure;
                    }
                    if (parameterLength > (uint)(body.Length - cursor))
                    {
                        return HircFrameOutcome(
                            "failed", "range_sourceParams", cursor, parameterLength, body.Length - cursor);
                    }
                    cursor = checked(cursor + (int)parameterLength);
                    HircBump(groups, "sourceParamBytes", parameterLength);
                }
            }
            if (!HircReadUInt32(body, ref cursor, out var playlistCount, out failure, "playlistCount"))
            {
                return failure;
            }
            if (playlistCount > (uint)((body.Length - cursor) / 44))
            {
                return HircFrameOutcome(
                    "failed", "range_playlistEntries", cursor - 4, (body.Length - cursor) / 44, playlistCount);
            }
            cursor = checked(cursor + (int)playlistCount * 44);
            HircBump(groups, "playlistEntries", playlistCount);
            // The engine skips the whole playlist block, sub-track count included,
            // when there are no playlist items; two shipped bodies have none.
            var subTrackCount = 0U;
            if (playlistCount > 0
                && !HircReadUInt32(body, ref cursor, out subTrackCount, out failure, "subTrackCount"))
            {
                return failure;
            }
            HircBump(groups, "subTrackCount", subTrackCount);
            if (!HircReadUInt32(body, ref cursor, out var clipCount, out failure, "clipAutomationCount"))
            {
                return failure;
            }
            if (clipCount > (uint)((body.Length - cursor) / 12))
            {
                return HircFrameOutcome(
                    "failed", "range_clipAutomationEntries", cursor - 4, (body.Length - cursor) / 12, clipCount);
            }
            HircBump(groups, "clipAutomationEntries", clipCount);
            HircBump(groups, "clipAutomationPoints", 0);
            for (var clip = 0U; clip < clipCount; clip++)
            {
                if (!HircTake(body, ref cursor, 8, out failure, "clipAutomationHead"))
                {
                    return failure;
                }
                if (!HircReadUInt32(body, ref cursor, out var pointCount, out failure, "clipAutomationPointCount"))
                {
                    return failure;
                }
                if (pointCount > (uint)((body.Length - cursor) / 12))
                {
                    return HircFrameOutcome(
                        "failed", "range_clipAutomationPoints", cursor - 4, (body.Length - cursor) / 12, pointCount);
                }
                cursor = checked(cursor + (int)pointCount * 12);
                HircBump(groups, "clipAutomationPoints", pointCount);
            }
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out failure))
            {
                return failure;
            }
            if (!HircReadByte(body, ref cursor, out var trackType, out failure, "trackType"))
            {
                return failure;
            }
            HircBump(selectors, $"trackType_{trackType:X2}", 1);
            HircBump(groups, "switchAssocEntries", 0);
            if (trackType == 3)
            {
                if (!HircTake(body, ref cursor, 9, out failure, "trackSwitchHead"))
                {
                    return failure;
                }
                if (!HircReadUInt32(body, ref cursor, out var assocCount, out failure, "switchAssocCount"))
                {
                    return failure;
                }
                if (assocCount > (uint)((body.Length - cursor) / 4))
                {
                    return HircFrameOutcome(
                        "failed", "range_switchAssocEntries", cursor - 4, (body.Length - cursor) / 4, assocCount);
                }
                cursor = checked(cursor + (int)assocCount * 4);
                HircBump(groups, "switchAssocEntries", assocCount);
                if (!HircTake(body, ref cursor, 32, out failure, "trackTransitionParams"))
                {
                    return failure;
                }
            }
            if (!HircTake(body, ref cursor, 4, out failure, "lookAheadTime"))
            {
                return failure;
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // Numeric HIRC type 0x0C is CAkMusicSwitchCntr: the transition-aware music
        // node parameters, u8 bIsContinuePlayback, u32 uTreeDepth, that many u32
        // argument group ids then that many u8 group types, u32 uTreeDataSize, u8
        // uMode, and uTreeDataSize bytes of decision tree handed whole to
        // AkDecisionTree::SetTree. The tree's own node layout is not framed here.
        internal static EndfieldHircBodyFrameResult FrameType0CBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameMusicTransNodeParams(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircReadByte(body, ref cursor, out var continuePlayback, out failure, "continuePlayback"))
            {
                return failure;
            }
            HircBump(selectors, $"continuePlayback_{continuePlayback:X2}", 1);
            if (!HircReadUInt32(body, ref cursor, out var depth, out failure, "decisionArgumentCount"))
            {
                return failure;
            }
            if (depth > (uint)((body.Length - cursor) / 5))
            {
                return HircFrameOutcome(
                    "failed", "range_decisionArgumentEntries", cursor - 4, (body.Length - cursor) / 5, depth);
            }
            cursor = checked(cursor + (int)depth * 5);
            HircBump(groups, "decisionArgumentEntries", depth);
            if (!HircReadUInt32(body, ref cursor, out var treeSize, out failure, "decisionTreeSize"))
            {
                return failure;
            }
            if (!HircReadByte(body, ref cursor, out var mode, out failure, "decisionMode"))
            {
                return failure;
            }
            HircBump(selectors, $"decisionMode_{mode:X2}", 1);
            if (treeSize > (uint)(body.Length - cursor))
            {
                return HircFrameOutcome(
                    "failed", "range_decisionTreeBytes", cursor - 5, body.Length - cursor, treeSize);
            }
            if (treeSize % 12 != 0)
            {
                return HircFrameOutcome("failed", "decisionTree_not_whole_nodes", cursor, 12, (int)(treeSize % 12));
            }
            cursor = checked(cursor + (int)treeSize);
            HircBump(groups, "decisionTreeBytes", treeSize);
            HircBump(groups, "decisionTreeNodes", treeSize / 12);
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // Numeric HIRC type 0x0D is CAkMusicRanSeqCntr: the transition-aware music
        // node parameters, then u32 numPlaylistItems x 30 bytes (u32 SegmentID, u32
        // playlistItemID, u32 NumChildren, u32 eRSType, s16 Loop, s16 LoopMin, s16
        // LoopMax, u32 Weight, u16 wAvoidRepeatCount, u8 bIsUsingWeight, u8
        // bIsShuffle); the items nest by NumChildren in the engine and are read flat here.
        internal static EndfieldHircBodyFrameResult FrameType0DBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameMusicTransNodeParams(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var itemCount, out failure, "playlistCount"))
            {
                return failure;
            }
            if (itemCount > (uint)((body.Length - cursor) / 30))
            {
                return HircFrameOutcome(
                    "failed", "range_playlistEntries", cursor - 4, (body.Length - cursor) / 30, itemCount);
            }
            cursor = checked(cursor + (int)itemCount * 30);
            HircBump(groups, "playlistEntries", itemCount);
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }


        // The effect base every effect-shaped object reads (CAkFxBase::SetInitialValues):
        // u32 fxID (0xFFFFFFFF = none), u32 uSize and that many plug-in parameter bytes,
        // u8 numBankData x { u8 index, u32 sourceID }, InitialRTPC (group I), the
        // StateChunk (group H), then u16 numValues x { varint AkPropID, u8 rtpcAccum,
        // f32 value }. Numeric types 0x10 (FxShareSet) and 0x11 (FxCustom) are exactly
        // this; 0x15 (AudioDevice) adds AkOwnedEffectSlots::SetInitialValues: u8 uNumFx,
        // u8 bitsFXBypass when nonzero, then uNumFx x { u8 index, u32 fxID, u8 flags }.
        internal static EndfieldHircBodyFrameResult FrameFxBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion,
            bool withDeviceSlots)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!HircTake(body, ref cursor, 4, out var failure, "fxPluginId"))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var paramBytes, out failure, "fxParamSize"))
            {
                return failure;
            }
            if (paramBytes > (uint)(body.Length - cursor))
            {
                return HircFrameOutcome("failed", "range_fxParamBytes", cursor - 4, body.Length - cursor, paramBytes);
            }
            cursor = checked(cursor + (int)paramBytes);
            HircBump(groups, "fxParamBytes", paramBytes);
            if (!HircReadByte(body, ref cursor, out var mediaCount, out failure, "fxMediaCount"))
            {
                return failure;
            }
            if (!HircTake(body, ref cursor, mediaCount * 5, out failure, "fxMediaEntries"))
            {
                return failure;
            }
            HircBump(groups, "fxMediaEntries", mediaCount);
            if (!FrameHircGroupI(body, ref cursor, groups, selectors, out failure))
            {
                return failure;
            }
            if (!FrameHircGroupH(body, ref cursor, groups, selectors, out failure))
            {
                return failure;
            }
            if (!HircReadUInt16(body, ref cursor, out var valueCount, out failure, "fxPropertyCount"))
            {
                return failure;
            }
            // A value is at least a one-byte id, the accumulation byte and a float.
            if (valueCount > (body.Length - cursor) / 6)
            {
                return HircFrameOutcome("failed", "range_fxPropertyEntries", cursor - 2, (body.Length - cursor) / 6, valueCount);
            }
            HircBump(groups, "fxPropertyEntries", valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                if (!HircReadVariableSize(body, ref cursor, out _, out failure, "fxPropertyId"))
                {
                    return failure;
                }
                if (!HircTake(body, ref cursor, 5, out failure, "fxPropertyValue"))
                {
                    return failure;
                }
            }
            HircBump(groups, "deviceEffectEntries", 0);
            if (withDeviceSlots)
            {
                if (!HircReadByte(body, ref cursor, out var slotCount, out failure, "deviceEffectCount"))
                {
                    return failure;
                }
                if (slotCount > 0)
                {
                    if (!HircTake(body, ref cursor, 1, out failure, "deviceEffectBypass"))
                    {
                        return failure;
                    }
                    if (!HircTake(body, ref cursor, slotCount * 6, out failure, "deviceEffectEntries"))
                    {
                        return failure;
                    }
                }
                HircBump(groups, "deviceEffectEntries", slotCount);
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // Modulators (CAkModulator::SetInitialValues) -- numeric types 0x13 (LFO), 0x14
        // (Envelope) and 0x16 (TimeMod) alike: the property bundle (u8 cProps, that
        // many u8 AkModulatorPropID keys, then that many u32 values), the ranged
        // bundle (u8 cProps, keys, then f32 min/max pairs) and InitialRTPC (group I).
        // The ranged bundle is the byte the earlier 0x16 frame consumed as anonymous.
        internal static EndfieldHircBodyFrameResult FrameModulatorBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameHircPropertyBundles(body, ref cursor, groups, out var failure))
            {
                return failure;
            }
            if (!FrameHircGroupI(body, ref cursor, groups, selectors, out failure))
            {
                return failure;
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // The two property bundles as CAkParameterNode::SetInitialParams,
        // CAkModulator::SetInitialValues, CAkAction::SetInitialValues and
        // CAkDialogueEvent::SetInitialValues all read them: keys then values, twice.
        private static bool FrameHircPropertyBundles(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var propCount, out failure, "groupCCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, propCount * 5, out failure, "groupCEntries"))
            {
                return false;
            }
            HircBump(groups, "groupCEntries", propCount);
            if (!HircReadByte(body, ref cursor, out var rangedCount, out failure, "groupDCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, rangedCount * 9, out failure, "groupDEntries"))
            {
                return false;
            }
            HircBump(groups, "groupDEntries", rangedCount);
            return true;
        }

        // Numeric HIRC type 0x0F is CAkDialogueEvent: u8 uProbability, u32 uTreeDepth,
        // that many u32 argument group ids then that many u8 group types, u32
        // uTreeDataSize, u8 uMode, the tree bytes handed to AkDecisionTree::SetTree,
        // then the two property bundles.
        internal static EndfieldHircBodyFrameResult FrameType0FBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!HircTake(body, ref cursor, 1, out var failure, "dialogueProbability"))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var depth, out failure, "decisionArgumentCount"))
            {
                return failure;
            }
            if (depth > (uint)((body.Length - cursor) / 5))
            {
                return HircFrameOutcome(
                    "failed", "range_decisionArgumentEntries", cursor - 4, (body.Length - cursor) / 5, depth);
            }
            cursor = checked(cursor + (int)depth * 5);
            HircBump(groups, "decisionArgumentEntries", depth);
            if (!HircReadUInt32(body, ref cursor, out var treeSize, out failure, "decisionTreeSize"))
            {
                return failure;
            }
            if (!HircReadByte(body, ref cursor, out var mode, out failure, "decisionMode"))
            {
                return failure;
            }
            HircBump(selectors, $"decisionMode_{mode:X2}", 1);
            if (treeSize > (uint)(body.Length - cursor))
            {
                return HircFrameOutcome(
                    "failed", "range_decisionTreeBytes", cursor - 5, body.Length - cursor, treeSize);
            }
            if (treeSize % 12 != 0)
            {
                return HircFrameOutcome("failed", "decisionTree_not_whole_nodes", cursor, 12, (int)(treeSize % 12));
            }
            cursor = checked(cursor + (int)treeSize);
            HircBump(groups, "decisionTreeBytes", treeSize);
            HircBump(groups, "decisionTreeNodes", treeSize / 12);
            if (!FrameHircPropertyBundles(body, ref cursor, groups, out failure))
            {
                return failure;
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // Numeric type 0x0E does not use the shared node frame. Its body is a fixed
        // 21-byte head, an optional 20-byte block selected by the second byte, then a
        // counted list whose entries each carry a selector, a counted run of 12-byte
        // elements, and finally a two-byte terminator.
        //
        // The optional block is the only branch, and it is decided by a byte, not by a
        // search: byte 1 is 0 or 1 across the whole corpus and predicts the prefix
        // length in every body. Any other value fails closed rather than guessing.
        internal const int Type14FixedHeadBytes = 21;
        internal const int Type14OptionalBlockBytes = 20;

        internal static EndfieldHircBodyFrameResult FrameType14Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (body.Length < Type14FixedHeadBytes)
            {
                return HircFrameOutcome(
                    "failed", "short_fixedHead", 0, Type14FixedHeadBytes, body.Length);
            }
            HircBump(selectors, $"headByte_{body[0]:X2}", 1);

            var flag = body[1];
            if (flag > 1)
            {
                // The prefix length depends on this byte, so an unobserved value means the
                // body cannot be framed at all. Widening it would be a guess.
                return HircFrameOutcome("failed", "unknown_optionalBlockFlag", 1, 1, flag);
            }
            HircBump(selectors, $"optionalBlockFlag_{flag:X2}", 1);
            cursor = Type14FixedHeadBytes;
            if (flag == 1)
            {
                if (!HircTake(body, ref cursor, Type14OptionalBlockBytes, out var blockFailure, "optionalBlock"))
                {
                    return blockFailure;
                }
                HircBump(groups, "optionalBlock", 1);
            }

            if (!HircReadByte(body, ref cursor, out var entryCount, out var failure, "listEntryCount"))
            {
                return failure;
            }
            HircBump(groups, "listEntries", entryCount);
            for (var entry = 0; entry < entryCount; entry++)
            {
                if (!HircReadByte(body, ref cursor, out var entrySelector, out failure, "listEntrySelector"))
                {
                    return failure;
                }
                HircBump(selectors, $"listEntrySelector_{entrySelector:X2}", 1);
                if (!HircReadUInt16(body, ref cursor, out var elementCount, out failure, "listElementCount"))
                {
                    return failure;
                }
                if (elementCount > (body.Length - cursor) / Type14ElementBytes)
                {
                    return HircFrameOutcome(
                        "failed",
                        "range_listElements",
                        cursor - 2,
                        (body.Length - cursor) / Type14ElementBytes,
                        elementCount);
                }
                cursor = checked(cursor + elementCount * Type14ElementBytes);
                HircBump(groups, "listElements", elementCount);
            }

            // Two bytes close every body. They read as zero everywhere, so a nonzero value
            // would mean content this frame does not describe: reject instead of ignoring.
            if (!HircReadUInt16(body, ref cursor, out var terminator, out failure, "listTerminator"))
            {
                return failure;
            }
            if (terminator != 0)
            {
                return HircFrameOutcome("failed", "nonzero_listTerminator", cursor - 2, 0, terminator);
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, null);
        }

        // Numeric type 0x08's tail is anchored by a nine-byte constant that every body
        // carries. It is treated as a landmark, not as data: a body whose bytes do not
        // match it here is refused rather than framed with an offset that fits.
        // Numeric type 0x0A's head: a fixed part, then that many five-byte elements,
        // then the reference. The count sits at byte 14 -- it equals (headLength - 36)
        // / 5 in every body of the dominant family, all 3,441 of them.
        // Numeric type 0x0A also carries its reference at a fixed distance from the
        // END. Where the head rule and this anchor both apply they agree; where the
        // head rule cannot reach, the anchor still places 25 of the 28 bodies it
        // leaves behind.
        internal const int Type0AEndAnchorDistance = 69;
        // The reference does not sit at ONE distance from the end; it sits at one of a
        // small set. 69 carries 3,707 of the 4,158 bodies and 73 carries 288, and the
        // head rule's own distance distribution shows the rest of the family -- 77, 82,
        // 93, 125. Treating 73 as a control made it look like the anchor was leaking,
        // when in fact it is a second anchor.
        internal static readonly int[] Type0AEndAnchorDistances = { 69, 73 };
        // Scored against every distance within five bytes, because a fixed offset
        // that happens to land on a reference is not a rule until the neighbours are
        // shown to land on nothing.
        internal const int Type0AEndAnchorControlSpan = 5;
        internal const int Type0AHeadCountOffset = 14;
        internal const int Type0AHeadFixedBytes = 36;
        internal const int Type0AHeadElementBytes = 5;
        // Byte 17 says whether the rule applies. Where it is zero the rule places the
        // reference in 3,744 of 3,745 bodies that have one; where it is not, in 6 of 144.
        internal const int Type0AHeadDiscriminantOffset = 17;
        // The other reference every numeric type 0x0A body carries, inside its fixed
        // head. It resolves in every conditioned body and only ever to 0x0C or 0x0D.
        internal const int Type0AHeadWordOffset = 9;
        // The second counted run, present only when the flag byte is nonzero.
        internal const int Type0AHeadSecondCountOffset = 21;
        internal const int Type0AHeadSecondRunFixedBytes = 7;
        // Above this, byte 21 is not a count and a fixed block follows instead.
        internal const int Type0AHeadSecondCountCeiling = 16;
        internal const int Type0AHeadFixedBlockBytes = 16;
        // Twenty bytes past the reference sits a float. Across the conditioned bodies
        // it stays inside a narrow band and its commonest values are whole numbers,
        // which is what this censuses; what it measures is not claimed here.
        internal const int Type0ATailFloatOffset = 20;
        internal const float Type0ATailFloatFloor = 50f;
        internal const float Type0ATailFloatCeiling = 200f;
        // Its neighbour twelve bytes earlier is the opposite kind of field: never a
        // whole number in any body. Censused so the contrast is measured, not assumed.
        internal const int Type0ATailNeighbourOffset = 8;
        // Four bytes past the reference: a 32-bit fixed-point fraction of one, not an
        // integer and not a float. Its nonzero values land on simple rationals -- 1/3,
        // 2/3, 4/7, 10/11, 7/13 -- to within a few parts in 10^10.
        // Inside the fixed head: a float whose range runs from -96 to 98 and whose
        // values are whole numbers. -96 is the same floor the numeric type 0x08 and
        // 0x12 middle block carries. The control two bytes earlier overlaps it and
        // must behave differently.
        // Five bytes in: a word that is zero in almost every body and, where it is
        // not, names an object -- but never one this package ships. It is the first
        // cross-package reference found in these types, so it is counted rather than
        // resolved here, and the join is done over the whole corpus elsewhere.
        internal const int Type0AHeadWordFiveOffset = 5;
        internal const int Type0AHeadDecibelOffset = 16;
        internal const int Type0AHeadDecibelControlOffset = 14;
        internal const float Type0AHeadDecibelFloor = -96f;
        internal const float Type0AHeadDecibelCeiling = 98f;
        internal const int Type0ATailFractionOffset = 4;
        internal const int Type0ATailFractionMaximumDenominator = 64;
        // A few parts in 10^9 of 2^32, which exact rationals clear by two orders.
        internal const long Type0ATailFractionTolerance = 16;

        /// <summary>
        /// Is a 32-bit value a fixed-point fraction with a small denominator?
        /// </summary>
        private static bool IsSmallFraction(uint value)
        {
            if (value == 0)
            {
                return false;
            }
            const long scale = 1L << 32;
            for (var q = 2; q <= Type0ATailFractionMaximumDenominator; q++)
            {
                var p = (long)Math.Round((double)value * q / scale);
                if (p <= 0 || p >= q)
                {
                    continue;
                }
                if (Math.Abs((long)value - p * scale / q) <= Type0ATailFractionTolerance)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Keep the words at and around numeric type 0x0A's end anchor.
        /// </summary>
        /// <remarks>
        /// The head rule works from the front and needs three of the body's bytes to
        /// be counts; where any of them is not, it predicts past the end or into the
        /// wrong place. The anchor works from the back and needs nothing.
        ///
        /// Every distance within five bytes is collected so the anchors can be scored
        /// against the distances that are not anchors. That control is what makes this
        /// a rule: at -69 the word names a type 0x0B object in 3,707 bodies and at -73
        /// in 288, while -64 through -68, -70, -71, -72, -74 and -75 name one in
        /// **none**.
        /// </remarks>
        private static void CollectType0AEndAnchor(
            List<uint[]> sink,
            ReadOnlySpan<byte> body)
        {
            var span = Type0AEndAnchorControlSpan;
            var words = new uint[span * 2 + 1];
            for (var i = -span; i <= span; i++)
            {
                var at = body.Length - (Type0AEndAnchorDistance + i);
                words[i + span] = at >= 0 && at + 4 <= body.Length
                    ? BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(at, 4))
                    : 0U;
            }
            sink.Add(words);
        }

        /// <summary>
        /// Keep the word numeric type 0x0A's head-length rule predicts, and its controls.
        /// </summary>
        /// <remarks>
        /// Three controls, because a formula that hits often is not evidence on its own.
        /// The fixed offset asks whether the count matters at all; the two neighbours
        /// ask whether the position is the one the rule names or merely near it.
        /// </remarks>
        private static void CollectType0AHeadPrediction(
            List<(uint Predicted, uint Fixed, uint Plus, uint Minus, bool Discriminant, uint HeadWord, uint LeadBad, uint PadBad, ushort[] Values, int TailBytes, float TailFloat, float NeighbourFloat, uint Fraction, uint FractionControl, float Decibel, float DecibelControl, uint HeadWordFive)> sink,
            ReadOnlySpan<byte> body)
        {
            if (body.Length <= Type0AHeadCountOffset)
            {
                return;
            }
            // Two counted runs, not one. Byte 17 is a flag, not a length: where it is
            // nonzero a second run follows, seven bytes plus five per body[21]. In the
            // bodies where byte 17 is zero, byte 21 is zero too, so the second term
            // vanishes and the rule reduces to the single-run form.
            // Three shapes, selected by two bytes. Byte 17 says whether anything
            // follows the first run at all. Byte 21 then says which: read as a count it
            // gives a second five-byte run, but in some bodies it is part of a 32-bit
            // value rather than a count, and those carry a fixed sixteen-byte block
            // instead. Its magnitude separates the two -- a count here is never above
            // a handful, and the bodies carrying the block hold 65, 97, 111 or 243.
            var extra = body[Type0AHeadDiscriminantOffset] == 0
                ? 0
                : body[Type0AHeadSecondCountOffset] <= Type0AHeadSecondCountCeiling
                    ? checked(Type0AHeadSecondRunFixedBytes
                        + Type0AHeadElementBytes * body[Type0AHeadSecondCountOffset])
                    : Type0AHeadFixedBlockBytes;
            var at = checked(
                Type0AHeadFixedBytes + Type0AHeadElementBytes * body[Type0AHeadCountOffset] + extra);
            if (at + 4 > body.Length)
            {
                return;
            }
            static uint Read(ReadOnlySpan<byte> span, int offset) =>
                offset >= 0 && offset + 4 <= span.Length
                    ? BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4))
                    : 0U;
            // Read the counted five-byte elements while the body is here, but do not
            // aggregate them yet: they are only meaningful for bodies whose reference
            // turns out to be where the rule says, and that needs the package's object
            // set. The per-body summary travels with the prediction instead.
            var leadBad = 0U;
            var padBad = 0U;
            var values = new List<ushort>();
            for (var e = 0; e < body[Type0AHeadCountOffset]; e++)
            {
                var element = Type0AHeadFixedBytes + e * Type0AHeadElementBytes;
                if (element + Type0AHeadElementBytes > body.Length)
                {
                    break;
                }
                if (body[element] != 0)
                {
                    leadBad = checked(leadBad + 1);
                }
                if (BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(element + 3, 2)) != 0)
                {
                    padBad = checked(padBad + 1);
                }
                values.Add(BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(element + 1, 2)));
            }
            sink.Add((
                Read(body, at),
                Read(body, Type0AHeadFixedBytes),
                Read(body, at + 4),
                Read(body, at - 4),
                // The flag still marks the single-run family. The prediction above now
                // covers both families, but the conditioned scores and the head-word
                // census stay on the family they were measured over.
                body.Length > Type0AHeadDiscriminantOffset && body[Type0AHeadDiscriminantOffset] == 0,
                Read(body, Type0AHeadWordOffset),
                leadBad,
                padBad,
                values.ToArray(),
                body.Length - at,
                at + Type0ATailFloatOffset + 4 <= body.Length
                    ? BinaryPrimitives.ReadSingleLittleEndian(
                        body.Slice(at + Type0ATailFloatOffset, 4))
                    : float.NaN,
                at + Type0ATailNeighbourOffset + 4 <= body.Length
                    ? BinaryPrimitives.ReadSingleLittleEndian(
                        body.Slice(at + Type0ATailNeighbourOffset, 4))
                    : float.NaN,
                at + Type0ATailFractionOffset + 4 <= body.Length
                    ? BinaryPrimitives.ReadUInt32LittleEndian(
                        body.Slice(at + Type0ATailFractionOffset, 4))
                    : 0U,
                at + Type0ATailNeighbourOffset + 4 <= body.Length
                    ? BinaryPrimitives.ReadUInt32LittleEndian(
                        body.Slice(at + Type0ATailNeighbourOffset, 4))
                    : 0U,
                Type0AHeadDecibelOffset + 4 <= body.Length
                    ? BinaryPrimitives.ReadSingleLittleEndian(
                        body.Slice(Type0AHeadDecibelOffset, 4))
                    : float.NaN,
                Type0AHeadDecibelControlOffset + 4 <= body.Length
                    ? BinaryPrimitives.ReadSingleLittleEndian(
                        body.Slice(Type0AHeadDecibelControlOffset, 4))
                    : float.NaN,
                Type0AHeadWordFiveOffset + 4 <= body.Length
                    ? BinaryPrimitives.ReadUInt32LittleEndian(
                        body.Slice(Type0AHeadWordFiveOffset, 4))
                    : 0U));
        }

        /// <summary>
        /// Keep every distinct nonzero 32-bit word a music body contains.
        /// </summary>
        /// <remarks>
        /// Offered at every byte offset, not every fourth: these types are not framed,
        /// so nothing says a reference is aligned. Object ids are sparse against the
        /// 32-bit range, so offering more candidates costs a little more chance and
        /// buys every reference the body actually carries.
        /// </remarks>
        private static void CollectMusicBodyWords(
            List<(byte Type, uint[] Words, int[] DistancesFromEnd)> sink,
            byte objectType,
            ReadOnlySpan<byte> body)
        {
            if (body.Length < 4)
            {
                return;
            }
            // Keep where each word sat, measured from the *end*: these types' heads are
            // variable, so a distance from the front is not comparable across bodies.
            // The first occurrence wins; a word appearing twice is one candidate.
            var seen = new Dictionary<uint, int>();
            for (var at = 0; at + 4 <= body.Length; at++)
            {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(at, 4));
                if (word != 0 && !seen.ContainsKey(word))
                {
                    seen[word] = at - body.Length;
                }
            }
            var flat = new uint[seen.Count];
            var distances = new int[seen.Count];
            var index = 0;
            foreach (var pair in seen)
            {
                flat[index] = pair.Key;
                distances[index] = pair.Value;
                index++;
            }
            sink.Add((objectType, flat, distances));
        }

        // Distances from the end of a music body at which a 32-bit word carries a name
        // hash. Counted from the end because these types' heads are variable-length:
        // across the corpus only three byte positions from the front take a single
        // value, so nothing can be located from there.
        internal static ReadOnlySpan<int> MusicTailWordOffsets => new[] { 12, 24 };

        /// <summary>
        /// Census the two tail words of a music body against the known name hashes.
        /// </summary>
        /// <remarks>
        /// This frames nothing. It reads two words at fixed distances from the end and
        /// asks whether they are hashes of strings the game ships. Name hashes are
        /// sparse against the 32-bit range -- under fifty thousand of them -- so chance
        /// matches across the whole corpus are expected far below one, and the offsets
        /// are counted separately so a rate can be read per offset rather than pooled.
        /// </remarks>
        private static void CensusMusicTailWords(
            EndfieldHircMusicHeadReferenceCensus census,
            ReadOnlySpan<byte> body,
            HashSet<uint>? names)
        {
            if (names == null || names.Count == 0)
            {
                return;
            }
            foreach (var back in MusicTailWordOffsets)
            {
                if (body.Length < back)
                {
                    census.BodiesTooShortForTailWords =
                        checked(census.BodiesTooShortForTailWords + 1);
                    continue;
                }
                var word = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(body.Length - back, 4));
                census.TailWordsTested = checked(census.TailWordsTested + 1);
                HircBump(census.TailWordTestedByOffset, $"minus{back}", 1);
                if (names.Contains(word))
                {
                    census.TailWordsNamed = checked(census.TailWordsNamed + 1);
                    HircBump(census.TailWordNamedByOffset, $"minus{back}", 1);
                }
            }
        }

        private static ReadOnlySpan<byte> Type08Signature =>
            new byte[] { 0x02, 0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xC2 };
        // The one-entry list after the property block: its key decides the value width,
        // and the two observed keys are exactly sixteen bytes apart.
        private const byte Type08SecondListShortKey = 0x15;
        private const int Type08SecondListShortBytes = 11;
        private const byte Type08SecondListLongKey = 0x1D;
        private const int Type08SecondListLongBytes = 27;
        private const int Type08EntryBytes = 6;
        private const int Type08TrailerBytes = 5;

        // Numeric types 0x08 and 0x12 share one body layout, so they share one framer.
        // Both second-list widths numeric type 0x08 uses plus the one numeric type
        // 0x12 adds; a key outside the set is held unsupported rather than guessed.
        internal const byte SharedSecondListKeyA = 0x0A;
        internal const int SharedSecondListKeyABytes = 12;
        // Between the second list and the zero word sit nine bytes. They were once
        // matched against a constant, because numeric type 0x08's commonest bodies all
        // carry the same ones. They are a field: read as a flag byte, a 32-bit value
        // and a float, numeric type 0x08 carries (2, 1000, -96.0) in 148 bodies,
        // (2, 0, -96.0) in 8 and (2, 500, -96.0) in 1, and numeric type 0x12 carries
        // (0, 0, -96.3) in all of its. A magic does not vary in one 32-bit slot.
        internal const int SharedMiddleBlockBytes = 9;
        // After the middle block: a 32-bit count and that many eighteen-byte
        // elements. A bound so a corrupt count cannot make the reader walk the body.
        internal const int SharedMiddleRunElementBytes = 18;
        internal const uint SharedMiddleRunMaximum = 64;

        /// <summary>
        /// Frame the body layout numeric HIRC types 0x08 and 0x12 share.
        /// </summary>
        /// <remarks>
        /// A 32-bit reference, the counted key/value block numeric type 0x16 uses, a
        /// second list whose key sizes its value, the nine-byte middle block, a zero
        /// word, a counted run of six-byte entries, and then either five zero bytes or
        /// the shared tail block.
        ///
        /// Two details carry the framing rather than fit it. The second list's key
        /// *predicts* its value width -- 0x15 at 11 bytes, 0x1D at 27 and 0x0A at 12 --
        /// so a body with an unobserved key is refused instead of walked with a guessed
        /// width. And the entry run carries one extra byte when its count is nonzero
        /// and nothing at all when the count is zero, which the bodies declaring no
        /// entries fix rather than leaving to assumption.
        ///
        /// The reference is read but not published as a frame reference: numeric type
        /// 0x08's leading word already has its own census, and counting it twice would
        /// inflate the reference graph.
        /// </remarks>
        internal static EndfieldHircBodyFrameResult FrameSharedBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!HircTake(body, ref cursor, 4, out var failure, "reference"))
            {
                return failure;
            }
            // A null reference is followed by a second 32-bit word before the property
            // count. Four bodies carry it, and the evidence is where the rest of the
            // frame lands rather than the count being plausible: skipping the word puts
            // the second list's key on 0x15 in every one of them, and not skipping it
            // asks for 74, 205 or 167 properties in a body too short to hold them.
            if (BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)) == 0)
            {
                if (!HircTake(body, ref cursor, 4, out failure, "nullReferenceExtra"))
                {
                    return failure;
                }
                HircBump(selectors, "leadingReference_null", 1);
            }
            else
            {
                HircBump(selectors, "leadingReference_set", 1);
            }
            if (!HircReadByte(body, ref cursor, out var propertyCount, out failure, "propertyCount"))
            {
                return failure;
            }
            if (propertyCount > (body.Length - cursor) / 5)
            {
                return HircFrameOutcome(
                    "failed", "range_properties", cursor - 1, (body.Length - cursor) / 5, propertyCount);
            }
            for (var i = 0; i < propertyCount; i++)
            {
                HircBump(selectors, $"propertyKey_{body[cursor + i]:X2}", 1);
            }
            cursor = checked(cursor + propertyCount * 5);
            HircBump(groups, "propertyEntries", propertyCount);

            if (!HircReadByte(body, ref cursor, out var secondCount, out failure, "secondListCount"))
            {
                return failure;
            }
            if (!HircReadByte(body, ref cursor, out var secondKey, out failure, "secondListKey"))
            {
                return failure;
            }
            var secondWidth = secondKey switch
            {
                Type08SecondListShortKey => Type08SecondListShortBytes,
                Type08SecondListLongKey => Type08SecondListLongBytes,
                SharedSecondListKeyA => SharedSecondListKeyABytes,
                _ => -1,
            };
            if (secondWidth < 0)
            {
                return HircFrameOutcome("unsupported", "unsupported_second_list_key", cursor - 1, 0, secondKey);
            }
            HircBump(selectors, $"secondListKey_{secondKey:X2}", 1);
            HircBump(selectors, $"secondListCount_{secondCount}", 1);
            if (!HircTake(body, ref cursor, secondWidth, out failure, "secondListValue"))
            {
                return failure;
            }
            if (!HircTake(body, ref cursor, SharedMiddleBlockBytes, out failure, "middleBlock"))
            {
                return failure;
            }
            var middle = body.Slice(cursor - SharedMiddleBlockBytes, SharedMiddleBlockBytes);
            HircBump(
                selectors,
                $"middleBlock_{middle[0]:X2}_{BinaryPrimitives.ReadUInt32LittleEndian(middle.Slice(1, 4))}",
                1);
            // A 32-bit count, then that many eighteen-byte elements. It reads as a
            // constant zero in almost every body of both types, which is why it was
            // taken for one; the fourteen that declare 1, 2, 3 or 7 are what show it is
            // a count.
            //
            // The width was NOT settled by asking which value lets those fourteen
            // close -- widths 5 and 11 close all fourteen as well. It is settled by
            // which value lands them on the five zero bytes: eighteen puts ten of the
            // fourteen there and every rival width puts none. ClassifySharedFrameConstants
            // re-measures that margin, and the corpus gate fails if a rival ever ties.
            if (!HircTake(body, ref cursor, 4, out failure, "middleRunCount"))
            {
                return failure;
            }
            var middleRun = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor - 4, 4));
            if (middleRun > SharedMiddleRunMaximum)
            {
                return HircFrameOutcome(
                    "failed", "range_middle_run", cursor - 4, (int)SharedMiddleRunMaximum, (int)middleRun);
            }
            var middleSpan = checked((int)middleRun * SharedMiddleRunElementBytes);
            if (middleSpan > body.Length - cursor)
            {
                return HircFrameOutcome(
                    "failed", "range_middle_run", cursor - 4, body.Length - cursor, middleSpan);
            }
            cursor = checked(cursor + middleSpan);
            HircBump(groups, "middleRunElements", checked((uint)middleRun));
            if (!HircReadByte(body, ref cursor, out var entryCount, out failure, "entryCount"))
            {
                return failure;
            }
            var span = entryCount == 0 ? 0 : checked(entryCount * Type08EntryBytes + 1);
            if (span > body.Length - cursor)
            {
                return HircFrameOutcome("failed", "range_entries", cursor - 1, body.Length - cursor, span);
            }
            cursor = checked(cursor + span);
            HircBump(groups, "entryRunElements", entryCount);
            if (body.Length - cursor != Type08TrailerBytes)
            {
                // Bodies that do not end on the five zero bytes end on the shared tail
                // block instead. Nothing is searched for: the block either starts here
                // and finishes the body, or the body is refused.
                if (FrameTailBlock(body, ref cursor, groups, selectors, out failure))
                {
                    return HircFrameExact(cursor, body.Length, groups, selectors, null);
                }
                return failure;
            }
            for (var i = cursor; i < body.Length; i++)
            {
                if (body[i] != 0)
                {
                    return HircFrameOutcome("failed", "trailer_is_not_zero", i, 0, body[i]);
                }
            }
            cursor = body.Length;
            return HircFrameExact(cursor, body.Length, groups, selectors, null);
        }

        // How a body ends under a candidate set of shared-frame constants.
        internal enum SharedFrameProbe
        {
            NoClose = 0,
            ClosesOnTailBlock = 1,
            ClosesOnZeroTrailer = 2,
        }

        /// <summary>
        /// Walk the shared body with the constants supplied rather than the chosen ones.
        /// </summary>
        /// <remarks>
        /// This exists to score the chosen constants against their alternatives. It
        /// deliberately stops at the same places <see cref="FrameSharedBody"/> does and
        /// reports *how* the body ended, because that turns out to be the whole point:
        /// three of the four constants are not picked out by whether a body closes --
        /// several rival values close every body too -- but by whether it lands on the
        /// five zero bytes. A rival that closes by routing every body to the tail block
        /// has explained nothing.
        /// </remarks>
        internal static SharedFrameProbe ProbeSharedFrame(
            ReadOnlySpan<byte> body,
            int middleBlockBytes,
            int elementBytes,
            int entryBytes,
            int trailerBytes)
        {
            if (body.Length < 5)
            {
                return SharedFrameProbe.NoClose;
            }
            var cursor = 4;
            if (BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)) == 0)
            {
                if (body.Length < 9)
                {
                    return SharedFrameProbe.NoClose;
                }
                cursor = 8;
            }
            var propertyCount = body[cursor];
            cursor = checked(cursor + 1);
            if (propertyCount > (body.Length - cursor) / 5)
            {
                return SharedFrameProbe.NoClose;
            }
            cursor = checked(cursor + propertyCount * 5);
            if (body.Length - cursor < 2)
            {
                return SharedFrameProbe.NoClose;
            }
            var secondWidth = body[cursor + 1] switch
            {
                Type08SecondListShortKey => Type08SecondListShortBytes,
                Type08SecondListLongKey => Type08SecondListLongBytes,
                SharedSecondListKeyA => SharedSecondListKeyABytes,
                _ => -1,
            };
            if (secondWidth < 0)
            {
                return SharedFrameProbe.NoClose;
            }
            cursor = checked(cursor + 2 + secondWidth);
            if (body.Length - cursor < middleBlockBytes + 4)
            {
                return SharedFrameProbe.NoClose;
            }
            cursor = checked(cursor + middleBlockBytes);
            var middleRun = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            cursor = checked(cursor + 4);
            if (middleRun > SharedMiddleRunMaximum)
            {
                return SharedFrameProbe.NoClose;
            }
            var middleSpan = checked((long)middleRun * elementBytes);
            if (middleSpan > body.Length - cursor)
            {
                return SharedFrameProbe.NoClose;
            }
            cursor = checked(cursor + (int)middleSpan);
            if (cursor >= body.Length)
            {
                return SharedFrameProbe.NoClose;
            }
            var entryCount = body[cursor];
            cursor = checked(cursor + 1);
            var span = entryCount == 0 ? 0 : checked(entryCount * entryBytes + 1);
            if (span > body.Length - cursor)
            {
                return SharedFrameProbe.NoClose;
            }
            cursor = checked(cursor + span);
            if (body.Length - cursor != trailerBytes)
            {
                return SharedFrameProbe.ClosesOnTailBlock;
            }
            for (var i = cursor; i < body.Length; i++)
            {
                if (body[i] != 0)
                {
                    return SharedFrameProbe.NoClose;
                }
            }
            return SharedFrameProbe.ClosesOnZeroTrailer;
        }

        // The counts the probe varies, and how far. Wide enough that a value winning
        // inside the range is not winning because the range was drawn around it.
        private static readonly (string Name, int Chosen, int First, int Last)[] SharedFrameConstants =
        {
            ("middleBlockBytes", SharedMiddleBlockBytes, 1, 16),
            ("middleRunElementBytes", SharedMiddleRunElementBytes, 1, 32),
            ("entryBytes", Type08EntryBytes, 1, 16),
            ("trailerBytes", Type08TrailerBytes, 0, 12),
        };

        // The record the fenced tails end with: two 32-bit floats and a 32-bit code.
        internal const int Type08TailRecordBytes = 12;

        /// <summary>
        /// Walk numeric type 0x08's body to the end of its counted entry run, or fail.
        /// </summary>
        /// <remarks>
        /// Shared with <see cref="FrameType8Body"/> so the tail census and the framer
        /// can never disagree about where the body's framed part ends.
        /// </remarks>
        private static bool TryWalkToTail(
            ReadOnlySpan<byte> body,
            bool requireSignature,
            out int cursor)
        {
            cursor = 0;
            if (body.Length < 5)
            {
                return false;
            }
            cursor = 4;
            // Same rule as the framer: a null leading reference carries a second word.
            // Shared so the tail census and the framer can never disagree about where
            // the body's framed part begins.
            if (BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)) == 0)
            {
                if (body.Length < 9)
                {
                    return false;
                }
                cursor = 8;
            }
            var propertyCount = body[cursor];
            cursor = checked(cursor + 1);
            if (propertyCount > (body.Length - cursor) / 5)
            {
                return false;
            }
            cursor = checked(cursor + propertyCount * 5);
            // Numeric type 0x08's second list always declares one entry; numeric type
            // 0x12's declares three when its key is 0x0A. The count is therefore only
            // constrained for the type whose own corpus constrains it.
            if (body.Length - cursor < 2 || (requireSignature && body[cursor] != 1))
            {
                return false;
            }
            var secondWidth = body[cursor + 1] switch
            {
                Type08SecondListShortKey => Type08SecondListShortBytes,
                Type08SecondListLongKey => Type08SecondListLongBytes,
                SharedSecondListKeyA when !requireSignature => SharedSecondListKeyABytes,
                _ => -1,
            };
            if (secondWidth < 0)
            {
                return false;
            }
            cursor = checked(cursor + 2 + secondWidth);
            if (body.Length - cursor < Type08Signature.Length)
            {
                return false;
            }
            // Numeric type 0x08 carries the same nine bytes in every body, so matching
            // them is a cheap extra guard there. Numeric type 0x12 carries different
            // ones, so for that type the block is only skipped.
            if (requireSignature
                && !body.Slice(cursor, Type08Signature.Length).SequenceEqual(Type08Signature))
            {
                return false;
            }
            cursor = checked(cursor + Type08Signature.Length);
            if (body.Length - cursor < 5
                || BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)) != 0)
            {
                return false;
            }
            cursor = checked(cursor + 4);
            var entries = body[cursor];
            cursor = checked(cursor + 1);
            var span = entries == 0 ? 0 : checked(entries * Type08EntryBytes + 1);
            if (span > body.Length - cursor)
            {
                return false;
            }
            cursor = checked(cursor + span);
            return true;
        }

        /// <summary>
        /// Census what the framer fences: the tail some numeric type 0x08 bodies carry
        /// after their entry run.
        /// </summary>
        /// <remarks>
        /// This deliberately does **not** frame the tail. The bytes before its trailing
        /// run are not understood, so the run is anchored from the *end* instead: the
        /// tail must finish with a zero 16-bit word, and the count must sit exactly two
        /// bytes before a run of that many twelve-byte records. Only a uniquely
        /// determined count is accepted -- if two lengths both fit, the alignment is not
        /// evidence of anything and the body is left unresolved.
        ///
        /// What makes the alignment credible rather than arithmetic is each record's
        /// third field: across the corpus it takes a handful of small values, which
        /// arbitrary bytes read at a wrong offset would not do.
        /// </remarks>
        // The width of the unframed bytes between the entry run and the records in
        // every tail where a word at these offsets could be tested at all.
        internal const int Type08TailHeadBytes = 15;
        internal const int Type08TailFirstWordOffset = 3;
        internal const int Type08TailSecondWordOffset = 10;

        private static void CensusType08Tail(
            EndfieldHircType08TailCensus census,
            List<(uint First, uint Second)> candidates,
            ReadOnlySpan<byte> body,
            bool requireSignature = true)
        {
            census.Bodies = checked(census.Bodies + 1);
            if (!TryWalkToTail(body, requireSignature, out var cursor))
            {
                census.NotWalkable = checked(census.NotWalkable + 1);
                return;
            }
            if (body.Length - cursor == Type08TrailerBytes)
            {
                census.FramedByTheReader = checked(census.FramedByTheReader + 1);
                return;
            }
            census.Tails = checked(census.Tails + 1);
            var tail = body.Slice(cursor);
            if (tail.Length < 6
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice(tail.Length - 2, 2)) != 0)
            {
                census.NoZeroWordAtTheEnd = checked(census.NoZeroWordAtTheEnd + 1);
                return;
            }
            var matches = 0;
            var chosen = 0;
            var limit = (tail.Length - 4) / Type08TailRecordBytes;
            for (var count = 1; count <= limit; count++)
            {
                var at = tail.Length - 2 - count * Type08TailRecordBytes - 2;
                if (at < 0)
                {
                    break;
                }
                if (tail[at] == count)
                {
                    matches = checked(matches + 1);
                    chosen = count;
                }
            }
            if (matches == 0)
            {
                census.NoCountBeforeTheRecords = checked(census.NoCountBeforeTheRecords + 1);
                return;
            }
            if (matches > 1)
            {
                census.CountIsAmbiguous = checked(census.CountIsAmbiguous + 1);
                return;
            }
            census.TailsWithAUniqueCount = checked(census.TailsWithAUniqueCount + 1);
            census.Records = checked(census.Records + (uint)chosen);
            HircBump(census.RecordCountCounts, $"records_{chosen}", 1);
            var start = tail.Length - 2 - chosen * Type08TailRecordBytes;
            for (var i = 0; i < chosen; i++)
            {
                var code = BinaryPrimitives.ReadUInt32LittleEndian(
                    tail.Slice(start + i * Type08TailRecordBytes + 8, 4));
                HircBump(census.ThirdFieldCounts, $"code_{code}", 1);
            }
            census.UnexplainedHeadBytes = checked(census.UnexplainedHeadBytes + (uint)start);
            // The bytes before the records are not framed, but in most located tails
            // they are exactly this wide, and two 32-bit words sit at fixed places in
            // them. Both are collected: the second is the control, and it is what
            // stops the first one's hits from being read as an artefact of the offset.
            var headBytes = start - 2;
            if (headBytes != Type08TailHeadBytes)
            {
                census.HeadIsNotTheObservedWidth = checked(census.HeadIsNotTheObservedWidth + 1);
                return;
            }
            census.HeadsOfTheObservedWidth = checked(census.HeadsOfTheObservedWidth + 1);
            candidates.Add((
                BinaryPrimitives.ReadUInt32LittleEndian(tail.Slice(Type08TailFirstWordOffset, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(tail.Slice(Type08TailSecondWordOffset, 4))));
        }

        // The tail block numeric types 0x08 and 0x12 share. It was first read as a
        // fifteen-byte head plus one record run, which is what a block with a single
        // unit looks like. It is a *counted run of units*: the byte at offset 1 is the
        // unit count, and it was 1 in every body that framed under the old reading --
        // which is exactly why that reading looked like a constant.
        internal const int TailBlockPrefixBytes = 3;
        // The section that follows the units. Its entry widths are chosen by the high
        // bit of the entry's first byte, the same trick the format uses elsewhere.
        internal const int TailSectionEntryShortBytes = 3;
        internal const int TailSectionEntryLongBytes = 4;
        internal const byte TailSectionEntryWideFlag = 0x80;
        // one byte, a 32-bit reference, a zero byte, a record count.
        internal const int TailSectionHeadBytes = 7;
        // A record: a 32-bit id, a value count, a zero byte, then that many 16-bit
        // values followed by that many floats. The count is one in all but two
        // records, which is why the record looked twelve bytes wide.
        internal const int TailSectionRecordHeadBytes = 6;
        internal const int TailSectionRecordValueBytes = 2;
        internal const int TailSectionRecordFloatBytes = 4;
        internal const byte TailSectionRecordMaximumValues = 8;
        // When the section declares no entries it is absent, and one byte closes the
        // body. That byte plus its count are the two bytes the previous reading took
        // for a fixed closing word.
        internal const int TailSectionAbsentBytes = 1;
        internal const int TailBlockUnitHeadBytes = 12;
        // After a unit's head come two bytes -- a record count and a pad -- unless the
        // high bit is set on head byte 6, in which case there are three and the count
        // is the middle one. 111 units take the short form and 1 takes the long, with
        // no counter-examples either way.
        //
        // One unit is thin evidence for a rule, and the closure is not what carries
        // this one. Under the long form that unit's eight records all read as curve
        // records with interpolation codes 9, 9, 9, 9, 5, 4, 4 and 4, and the body
        // lands exactly on its two-byte absent section. Under the short form they are
        // noise. Eight independent twelve-byte windows agreeing on a ten-value enum is
        // the evidence; the unit count is not.
        internal const int TailBlockUnitGapBytes = 2;
        internal const int TailBlockUnitWideGapBytes = 3;
        internal const int TailBlockUnitWideHeadOffset = 6;
        internal const int TailBlockRecordBytes = 12;
        // A bound so a corrupt count cannot make the reader walk the whole body.
        internal const int TailBlockMaximumUnits = 64;

        /// <summary>
        /// Frame the tail block some numeric type 0x08 and 0x12 bodies carry instead of
        /// the five zero bytes.
        /// </summary>
        /// <remarks>
        /// Layout: a zero byte, a unit count, a zero byte, then that many units, then a
        /// zero sixteen-bit word. Each unit is a 32-bit value, a zero byte, two bytes,
        /// a second 32-bit value, one byte, a record count, one byte, and that many
        /// twelve-byte records.
        ///
        /// Only the bytes that are zero in every body of both types are checked. The
        /// three per-unit bytes are published as a selector instead: numeric type 0x08
        /// carries one combination and numeric type 0x12 another, and a corpus where
        /// each type is uniform cannot tell "this depends on the type" from "this is
        /// whatever this type happens to carry".
        /// </remarks>
        private static bool FrameTailBlock(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            failure = default!;
            if (body.Length - cursor < TailBlockPrefixBytes + 2)
            {
                failure = HircFrameOutcome(
                    "failed", "tail_block_too_short", cursor, TailBlockPrefixBytes + 2, body.Length - cursor);
                return false;
            }
            if (body[cursor] != 0 || body[cursor + 2] != 0)
            {
                failure = HircFrameOutcome(
                    "failed", "tail_block_prefix_is_not_the_observed_shape", cursor, 0, body[cursor]);
                return false;
            }
            var units = body[cursor + 1];
            if (units > TailBlockMaximumUnits)
            {
                failure = HircFrameOutcome(
                    "failed", "range_tail_block_units", cursor + 1, TailBlockMaximumUnits, units);
                return false;
            }
            cursor = checked(cursor + TailBlockPrefixBytes);
            HircBump(groups, "tailBlockUnits", units);
            for (var unit = 0; unit < units; unit++)
            {
                if (body.Length - cursor < TailBlockUnitHeadBytes + 2)
                {
                    failure = HircFrameOutcome(
                        "failed", "tail_block_unit_runs_past_the_end", cursor,
                        TailBlockUnitHeadBytes + 2, body.Length - cursor);
                    return false;
                }
                if (body[cursor + 4] != 0)
                {
                    failure = HircFrameOutcome(
                        "failed", "tail_block_unit_is_not_the_observed_shape",
                        cursor + 4, 0, body[cursor + 4]);
                    return false;
                }
                HircBump(
                    selectors,
                    $"tailBlockUnitSelector_{body[cursor + 5]:X2}{body[cursor + 6]:X2}{body[cursor + 11]:X2}",
                    1);
                var wideGap = (body[cursor + TailBlockUnitWideHeadOffset] & 0x80) != 0;
                cursor = checked(cursor + TailBlockUnitHeadBytes);
                var gap = wideGap ? TailBlockUnitWideGapBytes : TailBlockUnitGapBytes;
                if (body.Length - cursor < gap)
                {
                    failure = HircFrameOutcome(
                        "failed", "tail_block_unit_runs_past_the_end", cursor, gap,
                        body.Length - cursor);
                    return false;
                }
                var records = body[cursor + (wideGap ? 1 : 0)];
                HircBump(selectors, wideGap ? "tailBlockUnitGap_wide" : "tailBlockUnitGap_short", 1);
                cursor = checked(cursor + gap);
                var span = checked(records * TailBlockRecordBytes);
                if (span > body.Length - cursor)
                {
                    failure = HircFrameOutcome(
                        "failed", "range_tail_block_records", cursor - 2, body.Length - cursor, span);
                    return false;
                }
                cursor = checked(cursor + span);
                HircBump(groups, "tailBlockRecords", records);
            }
            return FrameTailSection(body, ref cursor, groups, selectors, out failure);
        }

        /// <summary>
        /// Frame the section that closes a tail block, which may be absent.
        /// </summary>
        /// <remarks>
        /// This is the part the previous reading missed, and missing it cost eighteen
        /// bodies. The section opens with an entry count; when that count is zero the
        /// section is absent and a single byte closes the body -- and those two bytes
        /// are exactly what the old code hardcoded as a fixed closing word, which is
        /// why 64 bodies closed and the rest did not.
        ///
        /// When the count is nonzero the entries follow, each three bytes or four
        /// depending on the high bit of its first byte, then a byte, a 32-bit
        /// reference, a zero and a record count.
        ///
        /// Nothing here is a guess about meaning. The entries' first bytes run 0, 1,
        /// 2, ... in the bodies that carry several, and the records end in floats, but
        /// neither observation is claimed as semantics.
        /// </remarks>
        private static bool FrameTailSection(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var entryCount, out failure, "tailSectionEntryCount"))
            {
                return false;
            }
            HircBump(selectors, $"tailSectionEntries_{entryCount}", 1);
            if (entryCount == 0)
            {
                if (body.Length - cursor != TailSectionAbsentBytes)
                {
                    failure = HircFrameOutcome(
                        "failed", "tail_block_does_not_end_the_body",
                        cursor, TailSectionAbsentBytes, body.Length - cursor);
                    return false;
                }
                cursor = body.Length;
                return true;
            }
            for (var entry = 0; entry < entryCount; entry++)
            {
                if (cursor >= body.Length)
                {
                    failure = HircFrameOutcome(
                        "failed", "range_tail_section_entries", cursor, 1, 0);
                    return false;
                }
                var wide = (body[cursor] & TailSectionEntryWideFlag) != 0;
                var width = wide ? TailSectionEntryLongBytes : TailSectionEntryShortBytes;
                if (!HircTake(body, ref cursor, width, out failure, "tailSectionEntry"))
                {
                    return false;
                }
                HircBump(groups, wide ? "tailSectionWideEntries" : "tailSectionEntries", 1);
            }
            if (body.Length - cursor < TailSectionHeadBytes)
            {
                failure = HircFrameOutcome(
                    "failed", "range_tail_section_head",
                    cursor, TailSectionHeadBytes, body.Length - cursor);
                return false;
            }
            if (body[cursor + 5] != 0)
            {
                failure = HircFrameOutcome(
                    "failed", "tail_section_head_is_not_the_observed_shape",
                    cursor + 5, 0, body[cursor + 5]);
                return false;
            }
            HircBump(selectors, $"tailSectionLead_{body[cursor]:X2}", 1);
            var records = body[cursor + 6];
            cursor = checked(cursor + TailSectionHeadBytes);
            HircBump(groups, "tailSectionRecords", records);
            for (var record = 0; record < records; record++)
            {
                if (body.Length - cursor < TailSectionRecordHeadBytes)
                {
                    failure = HircFrameOutcome(
                        "failed", "range_tail_section_records",
                        cursor, TailSectionRecordHeadBytes, body.Length - cursor);
                    return false;
                }
                var values = body[cursor + 4];
                if (body[cursor + 5] != 0)
                {
                    failure = HircFrameOutcome(
                        "failed", "tail_section_record_is_not_the_observed_shape",
                        cursor + 5, 0, body[cursor + 5]);
                    return false;
                }
                if (values == 0 || values > TailSectionRecordMaximumValues)
                {
                    failure = HircFrameOutcome(
                        "failed", "range_tail_section_record_values",
                        cursor + 4, TailSectionRecordMaximumValues, values);
                    return false;
                }
                var span = checked(TailSectionRecordHeadBytes
                    + values * (TailSectionRecordValueBytes + TailSectionRecordFloatBytes));
                if (span > body.Length - cursor)
                {
                    failure = HircFrameOutcome(
                        "failed", "range_tail_section_record_values",
                        cursor + 4, body.Length - cursor, span);
                    return false;
                }
                cursor = checked(cursor + span);
                HircBump(groups, "tailSectionRecordValues", values);
            }
            if (cursor != body.Length)
            {
                failure = HircFrameOutcome(
                    "failed", "tail_section_does_not_end_the_body",
                    cursor, 0, body.Length - cursor);
                return false;
            }
            return true;
        }

        // Group E = PositioningParams (CAkParameterNodeBase::SetPositioningParams).
        // u8 uBitsPositioning: bit0 bPositioningInfoOverrideParent, bit1
        // bHasListenerRelativeRouting, bits 2-3 panner type, bits 5-6 e3DPositionType.
        // The engine returns as soon as bit0 is clear, and again as soon as bit1 is
        // clear, so the extension exists only when both are set: u8 uBits3D (bits 0-1
        // spatialization mode, bit5 bHoldEmitterPosAndOrient, bit6 bHoldListenerOrient
        // as stored bits), and then, only when e3DPositionType is 1 or 2 (emitter or
        // listener with automation), u8 ePathMode, s32 TransitionTime, u32 ulNumVertices
        // x { f32 x, f32 y, f32 z, s32 duration }, u32 ulNumPlayListItem x { u32
        // ulVerticesOffset, u32 iNumVertices } and then ulNumPlayListItem x { f32 xRange,
        // f32 yRange, f32 zRange }. The two per-item runs are counted here as one
        // twenty-byte item so the published inventory keeps its key.
        private static bool FrameHircGroupE(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var bits, out failure, "groupESelector"))
            {
                return false;
            }
            HircBump(selectors, $"groupESelector_{bits:X2}", 1);
            if ((bits & 0x03) != 0x03)
            {
                // Override cleared, or no listener-relative routing: nothing more is read.
                return true;
            }
            if (!HircTake(body, ref cursor, 1, out failure, "groupEFlags"))
            {
                return false;
            }
            var branch = (bits >> 5) & 0x03;
            HircBump(selectors, $"groupEBranch_{branch}", 1);
            if (branch is 0 or 3)
            {
                // e3DPositionType 0 (emitter) and 3 carry no automation block.
                return true;
            }
            if (!HircTake(body, ref cursor, 5, out failure, "groupEBranchHeader"))
            {
                return false;
            }
            if (!HircReadUInt32(body, ref cursor, out var vertexCount, out failure, "groupEVertexCount"))
            {
                return false;
            }
            if (vertexCount > (uint)((body.Length - cursor) / 16))
            {
                failure = HircFrameOutcome(
                    "failed", "range_groupEVertices", cursor - 4, (body.Length - cursor) / 16, vertexCount);
                return false;
            }
            cursor = checked(cursor + (int)vertexCount * 16);
            if (!HircReadUInt32(body, ref cursor, out var itemCount, out failure, "groupEItemCount"))
            {
                return false;
            }
            if (itemCount > (uint)((body.Length - cursor) / 20))
            {
                failure = HircFrameOutcome(
                    "failed", "range_groupEItems", cursor - 4, (body.Length - cursor) / 20, itemCount);
                return false;
            }
            // Playlist items, then their range triples: two runs, one counted key.
            cursor = checked(cursor + (int)itemCount * 8);
            cursor = checked(cursor + (int)itemCount * 12);
            HircBump(groups, "groupEVertices", vertexCount);
            HircBump(groups, "groupEItems", itemCount);
            return true;
        }

        // Group F = AuxParams (CAkParameterNodeBase::SetAuxParams): u8 byBitVector with
        // bit0 bOverrideGameAuxSends, bit1 bUseGameAuxSends, bit2 bOverrideUserAuxSends,
        // bit3 bHasAux, bit4 bOverrideReflectionsAuxBus; when bit3 is set 4 x u32 aux
        // bus id; then always u32 reflectionsAuxBus.
        private static bool FrameHircGroupF(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var bits, out failure, "groupFSelector"))
            {
                return false;
            }
            HircBump(selectors, $"groupFSelector_{bits:X2}", 1);
            if ((bits & 0x08) != 0 && !HircTake(body, ref cursor, 16, out failure, "groupFBlock"))
            {
                return false;
            }
            return HircTake(body, ref cursor, 4, out failure, "groupFScalar");
        }

        // Widths above this are bucketed so a malformed bank cannot flood the published
        // selector inventory with one key per observed element count.
        private const int HircStateWidthHistogramLimit = 8;

        // Group H = StateChunk (CAkStateAware::ReadStateChunk). Every count here is a
        // seven-bit continuation value in the engine, so the byte the current corpus
        // spends on each is the short form, not a fixed width: varint ulNumStateProps
        // x { varint AkPropID, u8 accumType, u8 inDb }, varint ulNumStateGroups x
        // { u32 ulStateGroupID, u8 eStateSyncType, varint ulNumStates x { u32
        // ulStateID, u16 count, count x u16 AkPropID, count x u32 value } }. A state's
        // property ids must be strictly increasing; the engine rejects the bank
        // otherwise, and that ordering is not re-checked here.
        private static bool FrameHircGroupH(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadVariableSize(body, ref cursor, out var propCountWide, out failure, "groupHPropCount"))
            {
                return false;
            }
            var propCount = checked((uint)propCountWide);
            for (var i = 0U; i < propCount; i++)
            {
                if (!HircReadVariableSize(body, ref cursor, out _, out failure, "groupHPropId"))
                {
                    return false;
                }
                if (!HircTake(body, ref cursor, 2, out failure, "groupHProps"))
                {
                    return false;
                }
            }
            HircBump(groups, "groupHProps", propCount);
            if (!HircReadVariableSize(body, ref cursor, out var groupCountWide, out failure, "groupHGroupCount"))
            {
                return false;
            }
            var groupCount = checked((uint)groupCountWide);
            HircBump(groups, "groupHGroups", groupCount);
            // Seed both state counters so a body that frames no group still publishes the
            // rows; an absent row and a zero row must not look different to a consumer.
            HircBump(groups, "groupHStates", 0);
            HircBump(groups, "groupHStateElements", 0);
            for (var i = 0; i < groupCount; i++)
            {
                if (!HircTake(body, ref cursor, 5, out failure, "groupHGroupHeader"))
                {
                    return false;
                }
                if (!HircReadVariableSize(body, ref cursor, out var stateCountWide, out failure, "groupHStateCount"))
                {
                    return false;
                }
                var stateCount = checked((uint)stateCountWide);
                HircBump(groups, "groupHStates", stateCount);
                // Each state is a four-byte id and its own u16-counted property list,
                // stored as a run of u16 ids then a run of u32 values: six bytes per
                // property, counted here as one element.
                for (var state = 0U; state < stateCount; state++)
                {
                    if (!HircTake(body, ref cursor, 4, out failure, "groupHStateKey"))
                    {
                        return false;
                    }
                    if (!HircReadUInt16(body, ref cursor, out var elementCount, out failure, "groupHStateElementCount"))
                    {
                        return false;
                    }
                    if (elementCount > (body.Length - cursor) / 6)
                    {
                        failure = HircFrameOutcome(
                            "failed",
                            "range_groupHStateElements",
                            cursor - 2,
                            (body.Length - cursor) / 6,
                            elementCount);
                        return false;
                    }
                    cursor = checked(cursor + elementCount * 6);
                    HircBump(groups, "groupHStateElements", elementCount);
                    // Bucket wide states rather than minting one selector key per width:
                    // a malformed bank could otherwise put 65,536 rows in the report.
                    HircBump(
                        selectors,
                        elementCount <= HircStateWidthHistogramLimit
                            ? $"groupHStateWidth_{6 + elementCount * 6}"
                            : $"groupHStateWidth_over_{6 + HircStateWidthHistogramLimit * 6}",
                        1);
                }
            }
            return true;
        }

        // Group I = InitialRTPC (AK::RTPC::ReadRtpcCurves): u16 uNumCurves x { u32
        // RTPCID, u8 rtpcType, u8 rtpcAccum, varint ParamID, u32 rtpcCurveID, u8
        // eScaling, u16 ulSize x { f32 from, f32 to, u32 interpolation } }. The engine
        // accumulates the varint most-significant group first and has no width cap;
        // this reader follows that order and adds a five-byte cap of its own.
        private static bool FrameHircGroupI(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadUInt16(body, ref cursor, out var entryCount, out failure, "groupICount"))
            {
                return false;
            }
            if (entryCount > (body.Length - cursor) / 14)
            {
                failure = HircFrameOutcome(
                    "failed", "range_groupIEntries", cursor - 2, (body.Length - cursor) / 14, entryCount);
                return false;
            }
            HircBump(groups, "groupIEntries", entryCount);
            for (var i = 0; i < entryCount; i++)
            {
                if (!HircTake(body, ref cursor, 6, out failure, "groupIEntryHead"))
                {
                    return false;
                }
                // The ParamID is a variable-size key. Type 0x02 bodies only ever spend
                // one byte here, so a fixed width survived that corpus; type 0x07 bodies
                // carry continued values and disprove it.
                var keyStart = cursor;
                if (!HircReadVariableSize(body, ref cursor, out _, out failure, "groupIKey"))
                {
                    return false;
                }
                // Publish the observed width histogram: the five-byte cap and the 32-bit
                // range are inherited policy, so a reader must be able to audit how wide
                // this corpus actually goes.
                HircBump(groups, "groupIKeyBytes", (uint)(cursor - keyStart));
                HircBump(selectors, $"groupIKeyWidth_{cursor - keyStart}", 1);
                if (!HircTake(body, ref cursor, 5, out failure, "groupIEntryTail"))
                {
                    return false;
                }
                if (!HircReadUInt16(body, ref cursor, out var pointCount, out failure, "groupIPointCount"))
                {
                    return false;
                }
                if (pointCount > (body.Length - cursor) / 12)
                {
                    failure = HircFrameOutcome(
                        "failed", "range_groupIPoints", cursor - 2, (body.Length - cursor) / 12, pointCount);
                    return false;
                }
                cursor = checked(cursor + pointCount * 12);
                HircBump(groups, "groupIPoints", pointCount);
            }
            return true;
        }

        private static void HircBump(Dictionary<string, uint> counters, string key, uint value)
        {
            if (value == 0 && !counters.ContainsKey(key))
            {
                counters[key] = 0;
                return;
            }
            counters.TryGetValue(key, out var current);
            counters[key] = checked(current + value);
        }

        private static EndfieldHircBodyFrameResult HircFrameOutcome(
            string status,
            string category,
            int cursor,
            long expected,
            long actual) => new()
            {
                Status = status,
                Category = category,
                CursorOffset = cursor,
                ExpectedBytes = expected,
                ActualBytes = actual,
                GroupCounts = new Dictionary<string, uint>(StringComparer.Ordinal),
                SelectorCounts = new Dictionary<string, uint>(StringComparer.Ordinal),
            };

        private static bool HircTake(
            ReadOnlySpan<byte> body,
            ref int cursor,
            int length,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (length < 0 || body.Length - cursor < length)
            {
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, length, body.Length - cursor);
                return false;
            }
            cursor = checked(cursor + length);
            failure = null;
            return true;
        }

        // Seven-bit continuation groups, least significant first, at most five bytes.
        // This matches the encoding the type 0x03 Action reader already validates.
        private static bool HircReadVariableSize(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out ulong value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            value = 0;
            // Four bytes may continue; the fifth must terminate, so there is no
            // unreachable fall-through and every guard below is separately reachable.
            for (var index = 0; index < 4; index++)
            {
                if (body.Length - cursor < 1)
                {
                    failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 1, 0);
                    return false;
                }
                var current = body[cursor];
                cursor = checked(cursor + 1);
                // Most-significant group first, as the engine's readers accumulate it.
                value = (value << 7) | (ulong)(current & 0x7F);
                if ((current & 0x80) == 0)
                {
                    failure = null;
                    return true;
                }
            }
            if (body.Length - cursor < 1)
            {
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 1, 0);
                return false;
            }
            var last = body[cursor];
            cursor = checked(cursor + 1);
            if ((last & 0x80) != 0)
            {
                failure = HircFrameOutcome("failed", $"unterminated_{what}", cursor - 1, 1, 1);
                return false;
            }
            // Five groups carry 35 bits; anything above 32 is an overflow.
            value = (value << 7) | (ulong)(last & 0x7F);
            if (value > uint.MaxValue)
            {
                failure = HircFrameOutcome("failed", $"overflow_{what}", cursor - 1, 1, 1);
                return false;
            }
            failure = null;
            return true;
        }

        private static bool HircReadByte(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out byte value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (body.Length - cursor < 1)
            {
                value = 0;
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 1, body.Length - cursor);
                return false;
            }
            value = body[cursor];
            cursor = checked(cursor + 1);
            failure = null;
            return true;
        }

        private static bool HircReadUInt16(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out ushort value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (body.Length - cursor < 2)
            {
                value = 0;
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 2, body.Length - cursor);
                return false;
            }
            value = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor = checked(cursor + 2);
            failure = null;
            return true;
        }

        private static bool HircReadUInt32(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out uint value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (body.Length - cursor < 4)
            {
                value = 0;
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 4, body.Length - cursor);
                return false;
            }
            value = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            cursor = checked(cursor + 4);
            failure = null;
            return true;
        }

        private static void RecordType4U32VectorFrame(
            ReadOnlySpan<byte> body,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure,
            Dictionary<uint, byte> bankObjectTypes,
            HashSet<uint> duplicateObjectIds,
            Dictionary<uint, uint> referrerCounts,
            Dictionary<uint, uint> referrerOf)
        {
            structure.Type4U32VectorFrameCount = checked(structure.Type4U32VectorFrameCount + 1);
            structure.Type4U32VectorBodyBytes = checked(
                structure.Type4U32VectorBodyBytes + (uint)body.Length);

            if (body.Length < 1)
            {
                RecordType4U32VectorFailure(
                    "truncated_count",
                    1,
                    body.Length,
                    bankId,
                    ordinal,
                    objectId,
                    structure);
                structure.Type4U32VectorFailedBodyBytes = checked(
                    structure.Type4U32VectorFailedBodyBytes + (uint)body.Length);
                return;
            }

            // CAkEvent::SetInitialValues reads the action count as a seven-bit
            // continuation value; every shipped event spends one byte on it.
            var countCursor = 0;
            if (!HircReadVariableSize(body, ref countCursor, out var entryCountWide, out _, "eventActionCount"))
            {
                RecordType4U32VectorFailure(
                    "truncated_count", 1, body.Length, bankId, ordinal, objectId, structure);
                structure.Type4U32VectorFailedBodyBytes = checked(
                    structure.Type4U32VectorFailedBodyBytes + (uint)body.Length);
                return;
            }
            var entryCount = checked((int)entryCountWide);
            var expectedBytes = checked(countCursor + entryCount * sizeof(uint));
            if (expectedBytes > body.Length)
            {
                RecordType4U32VectorFailure(
                    "truncated_entries",
                    expectedBytes,
                    body.Length,
                    bankId,
                    ordinal,
                    objectId,
                    structure);
                structure.Type4U32VectorFailedBodyBytes = checked(
                    structure.Type4U32VectorFailedBodyBytes + (uint)body.Length);
                return;
            }

            structure.Type4U32VectorPrefixBytes = checked(
                structure.Type4U32VectorPrefixBytes + (uint)expectedBytes);
            structure.Type4U32VectorEntryCount = checked(
                structure.Type4U32VectorEntryCount + (uint)entryCount);
            if (expectedBytes == body.Length)
            {
                structure.Type4U32VectorExactCount = checked(
                    structure.Type4U32VectorExactCount + 1);
                structure.Type4U32VectorExactCursorBytes = checked(
                    structure.Type4U32VectorExactCursorBytes + (uint)body.Length);
                // Only an exact body's entries reach the reference census, so publish
                // that subset rather than the total the gate would otherwise tie against.
                structure.Type4U32VectorExactEntryCount = checked(
                    structure.Type4U32VectorExactEntryCount + (uint)entryCount);
                var references = new List<uint>(entryCount);
                for (var entry = 0; entry < entryCount; entry++)
                {
                    references.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                        body.Slice(checked(countCursor + entry * 4), 4)));
                }
                ResolveHircReferences(
                    structure.ReferenceCensus,
                    HircFrameExact(body.Length, body.Length, new Dictionary<string, uint>(StringComparer.Ordinal), new Dictionary<string, uint>(StringComparer.Ordinal), references),
                    4,
                    objectId,
                    bankObjectTypes,
                    duplicateObjectIds,
                    referrerCounts,
                    referrerOf);
                return;
            }

            structure.Type4U32VectorUnsupportedCount = checked(
                structure.Type4U32VectorUnsupportedCount + 1);
            const string unsupportedCategory = "opaque_tail_after_candidate_vector";
            structure.Type4U32VectorUnsupportedCategories.TryGetValue(unsupportedCategory, out var unsupportedCount);
            structure.Type4U32VectorUnsupportedCategories[unsupportedCategory] = checked(unsupportedCount + 1);
            structure.Type4U32VectorUnsupportedPrefixBytes = checked(
                structure.Type4U32VectorUnsupportedPrefixBytes + (uint)expectedBytes);
            structure.Type4U32VectorOpaqueTailBytes = checked(
                structure.Type4U32VectorOpaqueTailBytes + (uint)(body.Length - expectedBytes));
            if (structure.Type4U32VectorFailureExamples.Count < 8)
            {
                structure.Type4U32VectorFailureExamples.Add(new EndfieldHircType4VectorFrameExample
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    Status = "unsupported",
                    Category = "opaque_tail_after_candidate_vector",
                    ExpectedBytes = expectedBytes,
                    ActualBytes = body.Length,
                    OpaqueTailBytes = body.Length - expectedBytes,
                });
            }
        }

        private static void RecordType4U32VectorFailure(
            string category,
            int expectedBytes,
            int actualBytes,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            structure.Type4U32VectorFailedCount = checked(
                structure.Type4U32VectorFailedCount + 1);
            structure.Type4U32VectorFailureCounts.TryGetValue(category, out var failureCount);
            structure.Type4U32VectorFailureCounts[category] = checked(failureCount + 1);
            if (structure.Type4U32VectorFailureExamples.Count < 8)
            {
                structure.Type4U32VectorFailureExamples.Add(new EndfieldHircType4VectorFrameExample
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    Status = "failed",
                    Category = category,
                    ExpectedBytes = expectedBytes,
                    ActualBytes = actualBytes,
                    OpaqueTailBytes = 0,
                });
            }
        }

        private static byte[] ReadBytesWithin(BinaryReader reader, int count, long end, string field)
        {
            if (count < 0 || reader.BaseStream.Position > end || end - reader.BaseStream.Position < count)
            {
                throw new InvalidDataException($"truncated AKPK {field}");
            }
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new InvalidDataException($"short AKPK {field}: expected={count}, actual={bytes.Length}");
            }
            return bytes;
        }

        private static bool IsAsciiSectionTag(string tag)
        {
            if (tag.Length != 4)
            {
                return false;
            }
            for (var i = 0; i < tag.Length; i++)
            {
                if (tag[i] < 'A' || tag[i] > 'Z')
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasMagicAt(byte[] buffer, int offset, string magic)
        {
            if (offset < 0 || offset + magic.Length > buffer.Length)
            {
                return false;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (buffer[offset + i] != (byte)magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static byte[] ToBytes(List<ushort> values)
        {
            var bytes = new byte[values.Count * 2];
            for (var i = 0; i < values.Count; i++)
            {
                BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 2);
            }
            return bytes;
        }
    }

    public sealed class EndfieldWemEntry
    {
        public ulong Id { get; set; }
        public ulong Offset { get; set; }
        public ulong Size { get; set; }
        public string Language { get; set; }
        public uint? ContainerSeed { get; set; }
        public ulong ContainerDataOffset { get; set; }
    }

    public sealed class EndfieldBnkStructure
    {
        public ulong BankId { get; set; }
        public int ByteLength { get; set; }
        public uint? Version { get; set; }
        public List<EndfieldBnkSection> Sections { get; } = new();
        public uint HircObjectCount { get; set; }
        public Dictionary<byte, uint> HircObjectTypeCounts { get; } = new();
        public Dictionary<byte, EndfieldBnkObjectTypeStats> HircObjectTypeStats { get; } = new();
        public uint Type2PrefixCount { get; set; }
        public Dictionary<uint, uint> Type2PluginTypeCounts { get; } = new();
        public Dictionary<string, uint> Type2PluginIdCounts { get; } = new(StringComparer.Ordinal);
        public uint Type2PrefixBytes { get; set; }
        public uint Type2OpaqueTailBytes { get; set; }
        public uint Type2MinOpaqueTailBytes { get; set; }
        public uint Type2MaxOpaqueTailBytes { get; set; }
        public uint Type3ActionFrameCount { get; set; }
        public uint Type3ActionExactCount { get; set; }
        public uint Type3ActionUnsupportedCount { get; set; }
        public uint Type3ActionFailedCount { get; set; }
        public uint Type3ActionBodyBytes { get; set; }
        public uint Type3ActionExactCursorBytes { get; set; }
        public Dictionary<ushort, uint> Type3ActionOperationCounts { get; } = new();
        public Dictionary<string, uint> Type3ActionFailureCounts { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircActionFrameFailure> Type3ActionFailureExamples { get; } = new();
        public uint Type4U32VectorFrameCount { get; set; }
        public uint Type4U32VectorExactCount { get; set; }
        public uint Type4U32VectorUnsupportedCount { get; set; }
        public uint Type4U32VectorFailedCount { get; set; }
        public uint Type4U32VectorBodyBytes { get; set; }
        public uint Type4U32VectorPrefixBytes { get; set; }
        public uint Type4U32VectorUnsupportedPrefixBytes { get; set; }
        public uint Type4U32VectorExactCursorBytes { get; set; }
        public uint Type4U32VectorOpaqueTailBytes { get; set; }
        public uint Type4U32VectorFailedBodyBytes { get; set; }
        public uint Type4U32VectorEntryCount { get; set; }
        public uint Type4U32VectorExactEntryCount { get; set; }
        public Dictionary<string, uint> Type4U32VectorFailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type4U32VectorUnsupportedCategories { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircType4VectorFrameExample> Type4U32VectorFailureExamples { get; } = new();
        public EndfieldHircReferenceCensus ReferenceCensus { get; } = new();
        public EndfieldHircNamedReachCensus NamedReachCensus { get; } = new();
        public EndfieldHircBodyCensus Type08Body { get; } = new();
        public EndfieldHircBodyCensus Type12Body { get; } = new();
        public EndfieldHircBodyCensus Type14Body { get; } = new();
        public EndfieldHircBodyCensus Type22Body { get; } = new();
        public EndfieldHircMusicHeadReferenceCensus MusicHeadReferences { get; } = new();
        public EndfieldHircType08HeadCensus Type08Head { get; } = new();
        public EndfieldHircType08TailCensus Type08Tail { get; } = new();
        public EndfieldHircType08TailCensus Type12Tail { get; } = new();
        // (firstWord, secondWord) from each located tail head, classified once the
        // package's whole object set is known.
        public List<(uint First, uint Second)> Type08TailWords { get; } = new();
        public List<(uint First, uint Second)> Type12TailWords { get; } = new();
        // (music type, distinct nonzero words) per body, classified once the package's
        // whole object set is known. The music types are not framed, so a reference
        // cannot be read from a known offset; every word is offered instead and the
        // sparseness of the id space decides which are real.
        public List<(byte Type, uint[] Words, int[] DistancesFromEnd)> MusicBodyWords { get; } = new();
        // (music object id, its numeric type, the distinct words its body carries),
        // kept per bank so the relation can be resolved in the scope its endpoints
        // actually live in.
        public List<(uint Id, byte Type, uint[] Words)> MusicSources { get; } = new();
        public Dictionary<uint, (byte Type, uint Parent)> Type0CParents { get; } = new();
        public EndfieldHircType0CArrayCensus Type0CArray { get; } = new();
        // Downward music edges: numeric type 0x0C's counted array and numeric type
        // 0x0A's counted array of 0x0B references. Kept per bank like every other
        // relation here, because object ids repeat across banks.
        public Dictionary<uint, List<uint>> MusicEdges { get; } = new();
        // objectId -> the object its parent field names, for every type that has one.
        public Dictionary<uint, uint> ParentFields { get; } = new();
        public EndfieldHircParentFieldCensus ParentField { get; } = new();
        // Numeric types 0x08 and 0x12's bodies, kept so the constants in the shared
        // framer can be scored against their alternatives rather than asserted. All
        // 412 of them together are about 32 KB.
        public List<byte[]> SharedBodies { get; } = new();
        // objectId -> (numeric type, the id its leading word names). One map per bank.
        public Dictionary<uint, (byte Type, uint Parent)> SharedParents { get; } = new();
        // Per numeric type 0x0A body: the word the head-length rule predicts, and three
        // controls. Classified once the package's object set is known.
        // Per numeric type 0x0A body: the words at the end anchor and its neighbours.
        public List<uint[]> Type0AEndAnchors { get; } = new();
        // The owner id travels with the body: without it the counted array it
        // carries cannot become an edge, only a count.
        public List<(uint Id, byte[] Body)> Type0ABodies { get; } = new();
        public List<(uint Predicted, uint Fixed, uint Plus, uint Minus, bool Discriminant, uint HeadWord, uint LeadBad, uint PadBad, ushort[] Values, int TailBytes, float TailFloat, float NeighbourFloat, uint Fraction, uint FractionControl, float Decibel, float DecibelControl, uint HeadWordFive)> Type0AHeadPredictions { get; } = new();
        // (plug-in id -> source ids) for numeric type 0x02, kept so the package can
        // join them against its own media entries once every sector is parsed.
        public Dictionary<uint, HashSet<uint>> Type2SourcesByPlugin { get; } = new();
        // (action byte, target word) for numeric type 0x03, classified once the
        // package's full object set is known.
        public List<(byte Action, uint Target)> Type03Targets { get; } = new();
        public HashSet<uint> DeclaredObjectIds { get; } = new();
        // Retained for the package-wide named-reach walk.
        public Dictionary<uint, byte> WalkObjectTypes { get; } = new();
        public Dictionary<uint, List<uint>> WalkEdges { get; } = new();
        // One object, several sources: numeric type 0x0B owns a counted array of
        // the same 14-byte record numeric type 0x02 carries exactly one of.
        public Dictionary<uint, List<uint>> WalkSourceIds { get; } = new();
        // The 14-byte source records this bank carries, by the type that carries them.
        public Dictionary<byte, List<byte[]>> SourceRecordsByType { get; } = new();
        public EndfieldHircBodyCensus Type2Body { get; } = new();
        public EndfieldHircBodyCensus Type5Body { get; } = new();
        public EndfieldHircBodyCensus Type6Body { get; } = new();
        public EndfieldHircBodyCensus Type7Body { get; } = new();
        public EndfieldHircBodyCensus Type9Body { get; } = new();
        public EndfieldHircBodyCensus Type0ABody { get; } = new();
        public EndfieldHircBodyCensus Type0BBody { get; } = new();
        public EndfieldHircBodyCensus Type0CBody { get; } = new();
        public EndfieldHircBodyCensus Type0DBody { get; } = new();
        public EndfieldHircBodyCensus Type0FBody { get; } = new();
        public EndfieldHircBodyCensus Type10Body { get; } = new();
        public EndfieldHircBodyCensus Type11Body { get; } = new();
        public EndfieldHircBodyCensus Type13Body { get; } = new();
        public EndfieldHircBodyCensus Type14ModBody { get; } = new();
        public EndfieldHircBodyCensus Type15Body { get; } = new();
    }

    // One numeric HIRC type's whole-body census. Every type that opens with the shared
    // node frame accumulates through this, so their counters cannot drift apart.
    public sealed class EndfieldHircBodyCensus
    {
        public uint FrameCount { get; set; }
        public uint ExactCount { get; set; }
        public uint UnsupportedCount { get; set; }
        public uint FailedCount { get; set; }
        public uint BodyBytes { get; set; }
        public uint ExactCursorBytes { get; set; }
        public uint NonExactBytes { get; set; }
        public uint MinExactBytes { get; set; }
        public uint MaxExactBytes { get; set; }
        public Dictionary<string, uint> GroupCounts { get; } = new(StringComparer.Ordinal);
        // Per group: how many exact bodies exercise it at all, and the largest count
        // any single body declares. Published beside the entry totals because a total
        // says nothing about how broadly a group's layout has actually been seen.
        public Dictionary<string, uint> GroupBodies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> GroupMaxInOneBody { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> SelectorCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> FailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> UnsupportedCategories { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircBodyFrameExample> FailureExamples { get; } = new();
    }

    // Where each anonymous reference lands. This is an identity join, not a claim
    // about what the relation means.
    public sealed class EndfieldHircReferenceCensus
    {
        public uint References { get; set; }
        public uint ResolvedSameBank { get; set; }
        public uint UnresolvedInBank { get; set; }
        public uint SelfReferences { get; set; }
        public uint TargetsWithMultipleReferrers { get; set; }
        public uint DuplicateObjectIds { get; set; }
        public uint ReferencesToDuplicateIds { get; set; }
        public uint CandidateWords { get; set; }
        public uint CandidateWordsMatchingAnObject { get; set; }
        // Nodes on a cycle plus any tail feeding it: the walk stops at the repeat and
        // cannot separate the two, so the name must not promise cycle membership.
        public uint ReferenceCycleOrFeedingNodes { get; set; }
        public uint DistinctDuplicateObjectIds { get; set; }
        public Dictionary<string, uint> ObjectCountsByType { get; } = new(StringComparer.Ordinal);
        public uint MaximumReferenceDepth { get; set; }
        public Dictionary<string, uint> EdgeCounts { get; } = new(StringComparer.Ordinal);
    }

    // Where a shipped identifier reaches. Counters only: the walk direction is the
    // physical one, which object's body holds the value, and nothing is named here
    // except the entry point the caller supplied a hash for.
    // Numeric types 0x0A and 0x0D are not framed: their bodies still contain a
    // variable-length region nobody has isolated. One thing in them *is* determined,
    // and only that is recorded here -- a single 32-bit word near the head whose
    // offset is selected by body byte 2. This is identity resolution, not framing,
    // and it says nothing about what the relation means.
    // Numeric type 0x0B is not framed either, but its head is a counted run of
    // fourteen-byte records whose first word is a plug-in id drawn from the same
    // sparse set numeric type 0x02 uses. Plug-in ids are not small integers, so a
    // wrong record stride would scatter them out of that set almost immediately --
    // which is what makes the stride evidence rather than an assumption.
    // Numeric type 0x08 is not framed. Its leading 32-bit word, however, is either
    // null or the identity of an object in the same bank -- never a non-null value
    // that names nothing. Null is a real outcome here, not a failure, so it is
    // counted separately instead of being folded into either side.
    // Numeric types 0x11 and 0x10 share one grammar: an eight-byte header whose
    // second word sizes an opaque section, one byte, the node frame's group I
    // structure, a flag, and a counted run of six-byte elements.
    //
    // The flag gates an optional block whose width this corpus cannot determine.
    // Two widths, 21 and 27, consume every flagged body exactly -- they are the same
    // bytes read two ways, with 27 swallowing the run's single element and reading a
    // zero count. The flag is never greater than 1 anywhere, so no body can separate
    // them, and a flagged body is therefore fenced as unsupported rather than framed
    // on a coin flip.
    // Numeric type 0x09 does use the shared node frame -- it opens every one of its
    // bodies. After it comes a counted run of four-byte entries, then a second count.
    // When that second count is zero the body ends with one more byte and is consumed
    // exactly. When it is not zero it introduces records whose shape is not
    // established, so those bodies are fenced rather than framed.
    // Numeric types 0x13, 0x14 and 0x15. These corpora are tiny -- four, nine and five
    // bodies -- so the witness counts matter as much as the pass count and are
    // published beside it. A layout that consumes four bodies exactly is a weak claim
    // on its own; what makes the eight-byte value width more than a fit is that some
    // bodies actually carry a nonempty second block, and those are counted here.
    // The two id sets a media join needs, reported rather than joined here.
    //
    // Joining them inside one package is the wrong question: a bank's media almost
    // always lives in a *different* package of the same corpus, so a same-package
    // join answers 12 of 75,958 and means nothing. The union belongs to whoever can
    // see every package, so this type carries the inputs and no verdict.
    // Where the numeric type 0x03 target word lands.
    //
    // This is the one HIRC relation in this corpus that crosses a bank boundary. The
    // gated reference vectors never do, and that was recorded as a property of the
    // corpus; it is a property of those vectors. Actions are different, so the
    // classification is counted here rather than folded into the reference graph.
    public sealed class EndfieldHircType03TargetCensus
    {
        // The object types an action's target word resolves to.
        public Dictionary<string, uint> TargetTypes { get; } = new(StringComparer.Ordinal);
        public uint Objects { get; set; }
        public uint Zero { get; set; }
        public uint SameBank { get; set; }
        public uint OtherBankInPackage { get; set; }
        public uint OutsidePackage { get; set; }
        // Per action byte, so the caller can see whether it decides the outcome.
        public Dictionary<string, uint> SameBankByActionByte { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> OtherBankByActionByte { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> OutsideByActionByte { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircMediaJoinCensus
    {
        public uint MediaEntries { get; set; }
        public SortedSet<uint> MediaIds { get; } = new();
        // Media ids some source record names, whichever type carries the record.

        // Distinct source ids per plug-in id, so a caller can test whether the
        // plug-in decides the outcome instead of assuming it.
        public Dictionary<string, SortedSet<uint>> SourceIdsByPlugin { get; } =
            new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircType08HeadCensus
    {
        public uint Bodies { get; set; }
        public uint Resolved { get; set; }
        public uint Null { get; set; }
        public uint Unresolved { get; set; }
        public uint TooShort { get; set; }
    }

    // The counted five-byte elements inside numeric type 0x0A's head.
    public sealed class EndfieldHircType0AElementCensus
    {
        public uint Total { get; set; }
        public uint LeadingByteNotZero { get; set; }
        public uint PadNotZero { get; set; }
        public Dictionary<string, uint> ValueCounts { get; } = new(StringComparer.Ordinal);
    }

    // Whether the music types' relation is symmetric, resolved inside one bank.
    public sealed class EndfieldHircMusicMutualityCensus
    {
        public uint SameBankEdges { get; set; }
        public uint EdgesIntoUnscannedObjects { get; set; }
        public uint EdgesBetweenScannedObjects { get; set; }
        public uint MutualEdges { get; set; }
        public Dictionary<string, uint> MutualEdgeKinds { get; } = new(StringComparer.Ordinal);
    }

    // Whether the parent field and the reference graph are inverse relations.
    public sealed class EndfieldHircParentFieldCensus
    {
        public uint Checkable { get; set; }
        public uint ParentNamesTheChildBack { get; set; }
        public uint ParentDoesNotNameTheChild { get; set; }
        public uint NamesSomethingOutsideTheBank { get; set; }
        public uint ParentDeclaresNoChildren { get; set; }
        public Dictionary<string, uint> EdgeTypes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> DisagreementTypes { get; } = new(StringComparer.Ordinal);
    }

    // The 14-byte source record numeric types 0x02 and 0x0B share.
    public sealed class EndfieldHircSourceRecordCensus
    {
        public uint Records { get; set; }
        public uint RecordsTooShort { get; set; }
        public uint MediaIdsDeclared { get; set; }
        // Distinct values at the id field and at the words one byte either side, per
        // type. Joined against the pooled media set by the Python audit, because the
        // media a record names are declared by another package.
        public Dictionary<string, SortedSet<uint>> IdValuesByType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SortedSet<uint>> IdValuesBeforeByType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SortedSet<uint>> IdValuesAfterByType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> RecordsByType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> NamingMediaByType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> PluginIdsByType { get; } = new(StringComparer.Ordinal);
    }

    // Numeric type 0x0C's counted reference array, at 32 + 5 * body[14].
    public sealed class EndfieldHircType0CArrayCensus
    {
        public uint Bodies { get; set; }
        public uint TooShort { get; set; }
        public uint SelectorOutOfRange { get; set; }
        public uint CountPastTheEnd { get; set; }
        public uint CountOutOfRange { get; set; }
        public uint ArrayPastTheEnd { get; set; }
        public uint ArraysTested { get; set; }
        public uint ArraysFullyResolving { get; set; }
        public uint RivalArraysTested { get; set; }
        public uint RivalArraysFullyResolving { get; set; }
        public Dictionary<string, uint> SelectorValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ArrayLengths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> TargetTypes { get; } = new(StringComparer.Ordinal);
        public uint RegionGapsTested { get; set; }
        public uint RegionGapsPredicted { get; set; }
        public uint RegionFlagAboveTheObservedRange { get; set; }
        public Dictionary<string, uint> RegionFlags { get; } = new(StringComparer.Ordinal);
    }

    // Numeric type 0x0C's parent relation, read at a fixed front offset.
    public sealed class EndfieldHircType0CHierarchyCensus
    {
        public uint Banks { get; set; }
        public uint Objects { get; set; }
        public uint ObjectsNamingAParent { get; set; }
        public uint RootsWithNoParent { get; set; }
        public uint ParentsOutsideTheBank { get; set; }
        public uint Cycles { get; set; }
        public uint ParentsWithSeveralChildren { get; set; }
        public Dictionary<string, uint> Depths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ChildrenPerParent { get; } = new(StringComparer.Ordinal);
        // Which type names which. The chain is 0x0A -> 0x0D -> 0x0C -> 0x0C.
        public Dictionary<string, uint> EdgeTypes { get; } = new(StringComparer.Ordinal);
    }

    // The parent relation numeric types 0x08 and 0x12 declare, measured per bank.
    public sealed class EndfieldHircHierarchyCensus
    {
        public uint Banks { get; set; }
        public uint Objects { get; set; }
        public uint Cycles { get; set; }
        public uint RootsWithNoParent { get; set; }
        public uint RootsNamingOutsideTheBank { get; set; }
        public Dictionary<string, uint> RootsPerBank { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> RootTypes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> OutsideBankTypes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> InternalTypes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> LeafTypes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Depths { get; } = new(StringComparer.Ordinal);
        public uint ParentsWithSeveralChildren { get; set; }
        public Dictionary<string, uint> ChildrenPerParent { get; } = new(StringComparer.Ordinal);
    }

    // Whether each constant in the shared framer beats every rival value, and on
    // what test. Closure is not the test -- most of these constants have rivals that
    // close every body -- so the zero trailer is scored separately.
    public sealed class EndfieldHircSharedConstantCensus
    {
        public uint Bodies { get; set; }
        public Dictionary<string, uint> Chosen { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> BodiesExercising { get; } = new(StringComparer.Ordinal);
        // Per "{constant}_{candidate}": how many exercising bodies close under that
        // value, and how many of those land on the zero trailer. Published per
        // candidate rather than reduced to a winner here, because the winner can only
        // be decided after the packages are summed.
        public Dictionary<string, uint> ZeroTrailerByCandidate { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ClosesByCandidate { get; } = new(StringComparer.Ordinal);
    }

    // The STMG section's header and first record run.
    public sealed class EndfieldStmgCensus
    {
        public uint Sections { get; set; }
        public uint SectionBytes { get; set; }
        public uint SectionsTooShort { get; set; }
        public uint CountOutOfRange { get; set; }
        public uint RunPastTheEnd { get; set; }
        public uint SectionsFramed { get; set; }
        public uint DeclaredRecords { get; set; }
        public uint DistinctRecordIds { get; set; }
        public uint RivalStridesTested { get; set; }
        public uint RivalStridesWithDistinctIds { get; set; }
        public uint RunsFollowedByAPlausibleCount { get; set; }
        public uint BytesFramed { get; set; }
        public uint BytesUnframed { get; set; }
        public Dictionary<string, uint> RecordValues { get; } = new(StringComparer.Ordinal);
        // The trailing run, framed backward from the section end.
        public uint TailTrailingBytesNotZero { get; set; }
        public uint TailRunNotFound { get; set; }
        public uint TailRunsCheckable { get; set; }
        public uint TailCountDoesNotMatchTheRun { get; set; }
        public uint TailRunsFramed { get; set; }
        public uint TailRecords { get; set; }
        public uint TailDistinctIds { get; set; }
        public uint TailFloatsTested { get; set; }
        public uint TailFloatsBounded { get; set; }
        public uint TailRivalStridesTested { get; set; }
        public uint TailRivalStridesWithDistinctIds { get; set; }
        public uint TailRivalStridesWithTheZeroRun { get; set; }
        public Dictionary<string, uint> TailSelectors { get; } = new(StringComparer.Ordinal);
        // The middle block of variable-length entries.
        public uint EntryBlocks { get; set; }
        public uint EntryBlockBytes { get; set; }
        public uint EntryBlocksTooShort { get; set; }
        public uint EntryCountOutOfRange { get; set; }
        public uint EntryHeadPastTheEnd { get; set; }
        public uint EntryRecordCountOutOfRange { get; set; }
        public uint EntryRecordsPastTheEnd { get; set; }
        public uint EntryBlocksNotClosing { get; set; }
        public uint EntryBlocksFramed { get; set; }
        public uint Entries { get; set; }
        public uint DistinctEntryIds { get; set; }
        public uint EntryRecords { get; set; }
        public uint EntryRecordsCarryingTheMarker { get; set; }
        public Dictionary<string, uint> EntryRecordCounts { get; } = new(StringComparer.Ordinal);
    }

    // The ENVS section: curves over the same 12-byte point numeric type 0x0B uses.
    public sealed class EndfieldEnvsCensus
    {
        public uint Sections { get; set; }
        public uint SectionBytes { get; set; }
        public uint SectionsNotClosing { get; set; }
        public uint SectionsFramed { get; set; }
        public uint Curves { get; set; }
        public uint Points { get; set; }
        public uint CodesInRange { get; set; }
        public uint FloatsBounded { get; set; }
        public uint CurvesWithRisingX { get; set; }
        public Dictionary<string, uint> InterpolationCodes { get; } = new(StringComparer.Ordinal);
    }

    // The INIT plugin name table and the PLAT platform string.
    public sealed class EndfieldInitCensus
    {
        public uint Sections { get; set; }
        public uint SectionsTooShort { get; set; }
        public uint CountOutOfRange { get; set; }
        public uint SectionsNotClosing { get; set; }
        public uint SectionsFramed { get; set; }
        public uint Entries { get; set; }
        public uint DistinctPluginIds { get; set; }
        public uint PlatSections { get; set; }
        public uint PlatSectionsNotClosing { get; set; }
        public uint PlatSectionsFramed { get; set; }
        public Dictionary<uint, string> PluginNames { get; } = new();
        public Dictionary<string, uint> PlatformNames { get; } = new(StringComparer.Ordinal);
    }

    // Whether the STMG section names HIRC objects at all.
    public sealed class EndfieldHircStmgWordCensus
    {
        public uint Sections { get; set; }
        public uint SectionBytes { get; set; }
        public uint HircObjects { get; set; }
        public uint WordsTested { get; set; }
        public uint WordsNamingAnObject { get; set; }
        public Dictionary<string, uint> TypesNamed { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> WordsTestedByTag { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> OffsetsModTwelve { get; } = new(StringComparer.Ordinal);
    }

    // How much of the music family the only edges entering it can reach.
    public sealed class EndfieldHircMusicReachCensus
    {
        public uint MusicObjects { get; set; }
        public uint EntryEdges { get; set; }
        public uint BanksWithAnEntry { get; set; }
        public uint ReachedObjects { get; set; }
        public uint ReachedSourceIdCount { get; set; }
        public uint Edges { get; set; }
        public uint EdgeSources { get; set; }
        public uint RootsWithNoIncomingEdge { get; set; }
        public uint EntriesWithOutgoingEdges { get; set; }
        public uint EntriesThatAreRoots { get; set; }
        public Dictionary<string, uint> RootTypes { get; } = new(StringComparer.Ordinal);
        public HashSet<uint> ReachedSourceIds { get; } = new();
        public Dictionary<string, uint> EntryKinds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ReachedTypes { get; } = new(StringComparer.Ordinal);
    }

    // Numeric type 0x0A's reference to 0x0B, read as a counted array.
    public sealed class EndfieldHircType0ACountedArrayCensus
    {
        public uint Bodies { get; set; }
        public uint BodiesWithNoReference { get; set; }
        public uint NoRoomForACount { get; set; }
        public uint Checkable { get; set; }
        public uint CountMatchesTheRun { get; set; }
        public uint CountDoesNotMatch { get; set; }
        public Dictionary<string, uint> RunLengths { get; } = new(StringComparer.Ordinal);
    }

    // Numeric type 0x0A's reference measured from the end, and its neighbours.
    public sealed class EndfieldHircType0AEndAnchorCensus
    {
        public uint Bodies { get; set; }
        public uint AnchorNamesTheTargetType { get; set; }
        public uint ControlsNameTheTargetType { get; set; }
        public Dictionary<string, uint> AnchorHits { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ControlHits { get; } = new(StringComparer.Ordinal);
    }

    // Whether numeric type 0x0A's head-length rule puts the reference where it says.
    public sealed class EndfieldHircType0AHeadCensus
    {
        public uint Bodies { get; set; }
        public uint BodiesWhereTheRuleApplies { get; set; }
        public Dictionary<string, uint> NamesTheSourceType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> NamesTheSourceTypeWhereTheRuleApplies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> HeadWordTargets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> TailBytesByOutcome { get; } = new(StringComparer.Ordinal);
        public uint TailFloats { get; set; }
        public uint TailFloatsInBand { get; set; }
        public uint TailFloatsWhole { get; set; }
        public uint NeighbourFloats { get; set; }
        public uint NeighbourFloatsWhole { get; set; }
        public uint FractionCandidates { get; set; }
        public uint FractionsWithASmallDenominator { get; set; }
        public uint FractionControls { get; set; }
        public uint FractionControlsWithASmallDenominator { get; set; }
        public uint DecibelBodies { get; set; }
        public uint DecibelsInRange { get; set; }
        public uint DecibelsWhole { get; set; }
        public uint DecibelControlsInRange { get; set; }
        public uint WordFiveNonZero { get; set; }
        public uint WordFiveInPackage { get; set; }
        public Dictionary<string, uint> WordFiveValues { get; } = new(StringComparer.Ordinal);
    }

    // Which words in a music body name objects the package ships, and what they name.
    public sealed class EndfieldHircMusicReferenceCensus
    {
        public uint Bodies { get; set; }
        public uint PackagePopulation { get; set; }
        public uint WordsOffered { get; set; }
        public uint References { get; set; }
        public uint BodiesWithNoReference { get; set; }
        public Dictionary<string, uint> ReferencesPerBody { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> EdgeCounts { get; } = new(StringComparer.Ordinal);
        // Per edge kind: how many distinct objects it reaches, how many of those it
        // reaches more than once, and how many objects of that type the package
        // declares. Together these say whether an edge partitions its targets --
        // which an edge total on its own cannot, however suggestive the number.
        public Dictionary<string, uint> DistinctTargets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> TargetsReachedTwice { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> TargetPopulation { get; } = new(StringComparer.Ordinal);
        // Where each reference sat, measured from the end of its body. The music heads
        // are variable, so this is the only comparable coordinate they have.
        public Dictionary<string, uint> EdgeDistanceFromEnd { get; } = new(StringComparer.Ordinal);
    }

    // Whether the words in numeric type 0x08's tail head name package objects. The
    // second word is the control for the first.
    public sealed class EndfieldHircType08TailWordCensus
    {
        public uint Heads { get; set; }
        public uint PackagePopulation { get; set; }
        public uint FirstWordSameBank { get; set; }
        public uint FirstWordOtherBankInPackage { get; set; }
        public uint FirstWordOutsidePackage { get; set; }
        public uint SecondWordResolves { get; set; }
        public Dictionary<string, uint> FirstWordTargetTypeCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> SecondWordTargetTypeCounts { get; } = new(StringComparer.Ordinal);
    }

    // What numeric type 0x08's framer fences. The tail is not framed; only its
    // trailing counted run is located, and only when the count is unambiguous.
    public sealed class EndfieldHircType08TailCensus
    {
        public uint Bodies { get; set; }
        public uint NotWalkable { get; set; }
        public uint FramedByTheReader { get; set; }
        public uint Tails { get; set; }
        public uint NoZeroWordAtTheEnd { get; set; }
        public uint NoCountBeforeTheRecords { get; set; }
        public uint CountIsAmbiguous { get; set; }
        public uint TailsWithAUniqueCount { get; set; }
        public uint Records { get; set; }
        public uint UnexplainedHeadBytes { get; set; }
        public uint HeadsOfTheObservedWidth { get; set; }
        public uint HeadIsNotTheObservedWidth { get; set; }
        public Dictionary<string, uint> RecordCountCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ThirdFieldCounts { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircMusicHeadReferenceCensus
    {
        public uint Bodies { get; set; }
        public uint Resolved { get; set; }
        public uint Unresolved { get; set; }
        public uint Zero { get; set; }
        public uint UnknownDiscriminant { get; set; }
        // Bodies whose first byte is not 0. Only numeric type 0x0C has any, and their
        // head is a different shape, so they are excluded from the claim and counted
        // here rather than quietly shrinking the denominator.
        public uint UnknownHeadShape { get; set; }
        public uint TooShort { get; set; }
        public Dictionary<string, uint> BodiesByType { get; } = new(StringComparer.Ordinal);
        // Two 32-bit words at fixed distances from the *end* of a music body. The head
        // of these types is variable, so nothing can be counted from the front; the
        // tail is not, and these two words carry name hashes.
        public uint TailWordsTested { get; set; }
        public uint TailWordsNamed { get; set; }
        public uint BodiesTooShortForTailWords { get; set; }
        public Dictionary<string, uint> TailWordNamedByOffset { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> TailWordTestedByOffset { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> OffsetCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> DiscriminantCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> HeadShapeCounts { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircNamedReachCensus
    {
        // Object types the walk arrives at, so a zero source count can be told apart
        // from a walk that never left the first hop.
        public Dictionary<string, uint> ReachedObjectTypes { get; } = new(StringComparer.Ordinal);
        public uint MatchedObjects { get; set; }
        public uint MatchedNamedType { get; set; }
        public uint ReachingASource { get; set; }
        public uint ReachingNoSource { get; set; }
        public uint ReachedSourceIds { get; set; }
        public uint WalkEdgesLeavingThePackage { get; set; }
        // Populations other than HIRC objects that the caller's hashes might name.
        // Reported so the same coincidence test can be applied to them.
        public uint BanksMatched { get; set; }
        public uint BanksSeen { get; set; }
        public uint MediaMatched { get; set; }
        public uint MediaSeen { get; set; }
        public Dictionary<string, uint> MatchesByObjectType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ReachedSourceIdsByIdentity { get; } = new(StringComparer.Ordinal);
        // The reached ids themselves, not just how many. There are few enough of them
        // to carry, and a caller cannot join counts to a media table.
        public Dictionary<string, SortedSet<uint>> ReachedSourceIdListByIdentity { get; } =
            new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircBodyFrameExample
    {
        public ulong BankId { get; init; }
        public uint Ordinal { get; init; }
        public uint ObjectId { get; init; }
        public string Status { get; init; } = "";
        public string Category { get; init; } = "";
        public int CursorOffset { get; init; }
        public long ExpectedBytes { get; init; }
        public long ActualBytes { get; init; }
    }

    public sealed class EndfieldHircBodyFrameResult
    {
        public string Status { get; init; } = "";
        public string Category { get; init; } = "";
        public int CursorOffset { get; init; }
        public long ExpectedBytes { get; init; }
        public long ActualBytes { get; init; }
        public Dictionary<string, uint> GroupCounts { get; init; }
        public Dictionary<string, uint> SelectorCounts { get; init; }
        public List<uint> References { get; init; }
        // Words that look like identities but are not claimed as references. They are
        // counted against the bank so the counter-evidence is published, not hidden.
        public List<uint> CandidateWords { get; init; }
    }

    public sealed class EndfieldHircActionFrameResult
    {
        public string Status { get; init; } = "";
        public ushort? OperationCode { get; init; }
        public string FailureCategory { get; init; } = "";
        public int CursorOffset { get; init; }
        public long? ExpectedBytes { get; init; }
        public long? ActualBytes { get; init; }
    }

    public sealed class EndfieldHircActionFrameFailure
    {
        public ulong BankId { get; init; }
        public uint Ordinal { get; init; }
        public uint ObjectId { get; init; }
        public ushort? OperationCode { get; init; }
        public string Status { get; init; } = "";
        public string FailureCategory { get; init; } = "";
        public int CursorOffset { get; init; }
        public long? ExpectedBytes { get; init; }
        public long? ActualBytes { get; init; }
    }

    public sealed class EndfieldHircType4VectorFrameExample
    {
        public ulong BankId { get; init; }
        public uint Ordinal { get; init; }
        public uint ObjectId { get; init; }
        public string Status { get; init; } = "";
        public string Category { get; init; } = "";
        public int ExpectedBytes { get; init; }
        public int ActualBytes { get; init; }
        public int OpaqueTailBytes { get; init; }
    }

    public sealed class EndfieldBnkObjectTypeStats
    {
        public uint Count { get; set; }
        public ulong DeclaredLengthBytes { get; set; }
        public uint MinDeclaredLength { get; set; }
        public uint MaxDeclaredLength { get; set; }
    }

    public sealed class EndfieldBnkSection
    {
        public string Tag { get; set; }
        public int Offset { get; set; }
        public uint DeclaredSize { get; set; }
        // Retained only for the sections nothing parses yet, so that framing them does
        // not require a second pass over the package.
        public byte[]? Body { get; set; }
    }
}
