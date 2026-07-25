using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeStudio.CLI.Properties;
using Newtonsoft.Json;
using static AnimeStudio.CLI.Studio;

namespace AnimeStudio.CLI 
{
    public class Program
    {
        public static void Main(string[] args)
        {
            if (EndfieldVfsCli.TryRun(args, out var exitCode))
            {
                Environment.ExitCode = exitCode;
                return;
            }

            CommandLine.Init(args);
        }

        public static void Run(Options o)
        {
            try
            {
                var game = GameManager.GetGame(o.GameName);

                // See https://github.com/Eleiyas/Z3-Asset-Map 
                var paths = File.Exists("./Maps/Z3-AssetIndex-Eleiyas.json")
                    ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText("./Maps/Z3-AssetIndex-Eleiyas.json"))
                    : new Dictionary<ulong, string>();

                Studio.Paths = paths;
                AssetsHelper.Paths = paths;

                if (game == null)
                {
                    Console.WriteLine("Invalid Game !!");
                    Console.WriteLine(GameManager.SupportedGames());
                    return;
                }

                if (game is UnityCNGame unityCNGame)
                {
                    UnityCN.SetKey(unityCNGame.Key);
                    Logger.Info($"[UnityCN] Selected Key is {unityCNGame.Key.Name} - {unityCNGame.Key.Key}");
                }

                Studio.Game = game;
                Logger.Default = new ConsoleLogger();
                Logger.Flags = o.LoggerFlags.Aggregate((e, x) => e |= x);
                Logger.FileLogging = Settings.Default.enableFileLogging;
                AssetsHelper.Minimal = Settings.Default.minimalAssetMap;
                AssetsHelper.SetUnityVersion(o.UnityVersion);
                Studio.MonoBehaviourTypeTreePriorityMode = o.MonoBehaviourTypeTreePriority;

                var typeFilterPlan = ConfigureTypeFilters(o);

                assetsManager.Silent = o.Silent;
                assetsManager.Game = game;
                assetsManager.SpecifyUnityVersion = o.UnityVersion;

                var exportTargets = CreateExportTargets(o, typeFilterPlan);
                if (exportTargets == null)
                {
                    return;
                }
                if (o.ObjectIndexJsonl != null && !HasIndexableJsonTarget(o, typeFilterPlan))
                {
                    Console.Error.WriteLine(
                        "--object_index_jsonl requires a JSON export target selecting MonoBehaviour or PlayableDirector."
                    );
                    Environment.ExitCode = 1;
                    return;
                }
                using var objectIndex = ObjectIndexJsonlWriter.Open(o.ObjectIndexJsonl, o.Input);

                if (o.Key != default)
                {
                    MiHoYoBinData.Encrypted = true;
                    MiHoYoBinData.Key = o.Key;
                }

                if (o.AIFile != null && game.Type.IsGISubGroup())
                {
                    ResourceIndex.FromFile(o.AIFile.FullName);
                }

                if (o.DummyDllFolder != null)
                {
                    assemblyLoader.Load(o.DummyDllFolder.FullName);
                    Logger.Info(
                        $"[DummyDll] Loaded {assemblyLoader.LastLoadSuccessCount}/{assemblyLoader.LastLoadFileCount} assemblies " +
                        $"from {o.DummyDllFolder.FullName} ({assemblyLoader.LastLoadFailureCount} failed, {assemblyLoader.ModuleCount} modules available)"
                    );
                    if (!assemblyLoader.Loaded)
                    {
                        Logger.Warning($"[DummyDll] No usable assemblies were loaded from {o.DummyDllFolder.FullName}");
                    }
                }

                if (Studio.MonoBehaviourTypeTreePriorityMode == MonoBehaviourTypeTreePriority.ScriptFirst && !assemblyLoader.Loaded)
                {
                    Logger.Warning("[DummyDll] ScriptFirst MonoBehaviour TypeTree priority requested without usable DummyDlls; falling back to SerializedFirst.");
                    Studio.MonoBehaviourTypeTreePriorityMode = MonoBehaviourTypeTreePriority.SerializedFirst;
                }

                LoadFilterData(o);

                var inputFiles = new InputFileProvider(o.Input);
                RunMapOperations(o, game, typeFilterPlan, inputFiles);
                var indexComplete = ShouldRunAssetExport(o.MapOp);
                var exportHadErrors = false;
                if (ShouldRunAssetExport(o.MapOp)
                    && RunExportPlan(o, typeFilterPlan, exportTargets, inputFiles, out exportHadErrors))
                {
                    Environment.ExitCode = 1;
                }
                if (ShouldRunAssetExport(o.MapOp))
                {
                    indexComplete = !exportHadErrors;
                }
                objectIndex?.Complete(indexComplete);
                if (Properties.Settings.Default.scrapeMonos)
                {
                    File.WriteAllLines("./Maps/PathStrings_Sorted.txt", PathStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/VOStrings_Sorted.txt", VOStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/EventStrings_Sorted.txt", EventStrings.Distinct().OrderBy(p => p));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Environment.ExitCode = 1;
            }
        }


        private static TypeFilterPlan ConfigureTypeFilters(Options o)
        {
            var configuredTypes = JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Settings.Default.types)
                ?? new Dictionary<ClassIDType, (bool, bool)>();
            TypeFlags.SetTypes(configuredTypes);

            var primaryClassTypeFilter = Array.Empty<ClassIDType>();
            var secondaryClassTypeFilter = Array.Empty<ClassIDType>();
            var classTypeFilter = Array.Empty<ClassIDType>();
            if (!o.TypeFilter.IsNullOrEmpty() || !o.SecondaryTypeFilter.IsNullOrEmpty())
            {
                // When the CLI receives an explicit type list, treat it as the full parse/export surface
                // instead of layering it on top of the default settings from App.config.
                TypeFlags.SetTypes(new Dictionary<ClassIDType, (bool, bool)>());

                var classTypeFilterList = new List<ClassIDType>();

                ClassIDType[] ParseTypeFilter(string[] typeFilters, string label)
                {
                    var targetClassTypeFilterList = new List<ClassIDType>();
                    if (typeFilters.IsNullOrEmpty())
                    {
                        return targetClassTypeFilterList.ToArray();
                    }

                    for (int i = 0; i < typeFilters.Length; i++)
                    {
                        var typeStr = typeFilters[i];
                        var type = ClassIDType.UnknownType;
                        var flag = TypeFlag.Both;

                        try
                        {
                            if (typeStr.Contains(':'))
                            {
                                var param = typeStr.Split(':');

                                flag = (TypeFlag)Enum.Parse(typeof(TypeFlag), param[1], true);

                                typeStr = param[0];
                            }

                            type = (ClassIDType)Enum.Parse(typeof(ClassIDType), typeStr, true);

                            TypeFlags.SetType(type, flag.HasFlag(TypeFlag.Parse), flag.HasFlag(TypeFlag.Export));

                            if (!targetClassTypeFilterList.Contains(type))
                            {
                                targetClassTypeFilterList.Add(type);
                            }
                            if (!classTypeFilterList.Contains(type))
                            {
                                classTypeFilterList.Add(type);
                            }
                        }
                        catch(Exception)
                        {
                            Logger.Error($"{label} type {typeStr} has invalid format, skipping...");
                            continue;
                        }
                    }

                    return targetClassTypeFilterList.ToArray();
                }

                primaryClassTypeFilter = ParseTypeFilter(o.TypeFilter, "primary");
                secondaryClassTypeFilter = o.SecondaryTypeFilter.IsNullOrEmpty()
                    ? primaryClassTypeFilter
                    : ParseTypeFilter(o.SecondaryTypeFilter, "secondary");

                void EnsureParseDependency(ClassIDType type)
                {
                    var export = type.CanExport();
                    TypeFlags.SetType(type, true, export);
                    if (!classTypeFilterList.Contains(type))
                    {
                        classTypeFilterList.Add(type);
                    }
                }

                if (ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport())
                {
                    EnsureParseDependency(ClassIDType.Texture2D);
                    EnsureParseDependency(ClassIDType.Material);
                    EnsureParseDependency(ClassIDType.Transform);
                    EnsureParseDependency(ClassIDType.RectTransform);
                    EnsureParseDependency(ClassIDType.MeshFilter);
                    EnsureParseDependency(ClassIDType.MeshRenderer);
                    EnsureParseDependency(ClassIDType.SkinnedMeshRenderer);
                    EnsureParseDependency(ClassIDType.Mesh);
                    EnsureParseDependency(ClassIDType.Avatar);
                    EnsureParseDependency(ClassIDType.AnimatorController);
                    EnsureParseDependency(ClassIDType.AnimatorOverrideController);
                    EnsureParseDependency(ClassIDType.AnimationClip);
                    if (ClassIDType.GameObject.CanExport())
                    {
                        EnsureParseDependency(ClassIDType.Animator);
                    }
                    else if(ClassIDType.Animator.CanExport())
                    {
                        EnsureParseDependency(ClassIDType.GameObject);
                    }
                }

                classTypeFilter = classTypeFilterList.ToArray();
            }
            if (o.GroupAssetsType == AssetGroupOption.ByContainer)
            {
                TypeFlags.SetType(ClassIDType.AssetBundle, true, false);
            }

            if (o.DummyDllFolder != null
                || o.MonoBehaviourTypeTreePriority == MonoBehaviourTypeTreePriority.ScriptFirst
                || ClassIDType.MonoBehaviour.CanExport())
            {
                TypeFlags.SetType(ClassIDType.MonoScript, true, ClassIDType.MonoScript.CanExport());
            }

            return new TypeFilterPlan(primaryClassTypeFilter, secondaryClassTypeFilter, classTypeFilter);
        }

        private static List<ExportTarget> CreateExportTargets(Options o, TypeFilterPlan typeFilterPlan)
        {
            var hasSecondaryExportType = o.SecondaryAssetExportType.HasValue;
            var hasSecondaryOutput = o.SecondaryOutput != null;
            if (hasSecondaryExportType != hasSecondaryOutput)
            {
                Console.Error.WriteLine("--secondary_export_type and --secondary_export_path must be specified together.");
                Environment.ExitCode = 1;
                return null;
            }

            var exportTargets = new List<ExportTarget>
            {
                new ExportTarget(o.Output, o.AssetExportType, "primary", typeFilterPlan.PrimaryExportTypes)
            };

            o.Output.Create();
            if (hasSecondaryExportType)
            {
                o.SecondaryOutput.Create();
                exportTargets.Add(new ExportTarget(o.SecondaryOutput, o.SecondaryAssetExportType.Value, "secondary", typeFilterPlan.SecondaryExportTypes));
                Logger.Info($"[SecondaryExport] Also exporting {o.SecondaryAssetExportType.Value} assets to {o.SecondaryOutput.FullName}");
            }

            return exportTargets;
        }

        private static void LoadFilterData(Options o)
        {
            if (o.FilterDataFile == null || !o.FilterDataFile.Exists)
            {
                return;
            }

            var filterJson = File.ReadAllText(o.FilterDataFile.FullName);
            var filterItems = JsonConvert.DeserializeObject<List<AssetsManager.AssetFilterDataItem>>(filterJson);
            if (filterItems != null && filterItems.Count > 0)
            {
                assetsManager.FilterData.Items.AddRange(filterItems);
                Logger.Info($"[FilterData] Loaded {filterItems.Count} items from {o.FilterDataFile.FullName}");
            }
        }

        private static void RunMapOperations(Options o, Game game, TypeFilterPlan typeFilterPlan, InputFileProvider inputFiles)
        {
            if (o.MapOp.HasFlag(MapOpType.CABMap))
            {
                if (o.MapOp.HasFlag(MapOpType.Load))
                {
                    AssetsHelper.LoadCABMapInternal(o.MapName);
                    assetsManager.ResolveDependencies = true;
                }
                else
                {
                    AssetsHelper.BuildCABMap(inputFiles.GetInputFiles(), o.MapName, o.Input.FullName, game);
                }
            }
            if (o.MapOp.HasFlag(MapOpType.AssetMap))
            {
                if (o.MapOp.HasFlag(MapOpType.Load))
                {
                    var matchedEntries = AssetsHelper.ParseAssetMapEntries(o.MapName, o.MapType, typeFilterPlan.AssetSelectionTypes, o.NameFilter, o.ContainerFilter);
                    inputFiles.SetSelectedFiles(matchedEntries.Select(entry => entry.Source).Distinct(StringComparer.OrdinalIgnoreCase));
                    assetsManager.FilterData.Items.AddRange(matchedEntries.Select(entry => new AssetsManager.AssetFilterDataItem
                    {
                        Source = entry.Source,
                        Offset = entry.Offset,
                        Name = entry.Name,
                        PathID = entry.PathID,
                        Type = entry.Type,
                    }));
                    Logger.Info($"[AssetMap] Loaded {matchedEntries.Count} matching map entries across {inputFiles.SelectedFileCount} files");
                }
                else
                {
                    AssetsHelper.BuildAssetMap(inputFiles.GetInputFiles(), o.MapName, game, o.Output.FullName, o.MapType, typeFilterPlan.AssetSelectionTypes, o.NameFilter, o.ContainerFilter).GetAwaiter().GetResult();
                }
            }
            if (o.MapOp.HasFlag(MapOpType.Both))
            {
                AssetsHelper.BuildBoth(inputFiles.GetInputFiles(), o.MapName, o.Input.FullName, game, o.Output.FullName, o.MapType, typeFilterPlan.AssetSelectionTypes, o.NameFilter, o.ContainerFilter).GetAwaiter().GetResult();
            }
        }

        private static bool ShouldRunAssetExport(MapOpType mapOp) =>
            mapOp.Equals(MapOpType.None) || mapOp.HasFlag(MapOpType.Load);

        private static bool HasIndexableJsonTarget(Options options, TypeFilterPlan plan)
        {
            bool ContainsIndexableType(ClassIDType[] types)
            {
                if (types.IsNullOrEmpty())
                {
                    return ClassIDType.MonoBehaviour.CanExport() || ClassIDType.PlayableDirector.CanExport();
                }
                return types.Contains(ClassIDType.MonoBehaviour) || types.Contains(ClassIDType.PlayableDirector);
            }

            return (options.AssetExportType == ExportType.JSON && ContainsIndexableType(plan.PrimaryExportTypes))
                || (options.SecondaryAssetExportType == ExportType.JSON
                    && ContainsIndexableType(plan.SecondaryExportTypes));
        }

        private static bool RunExportPlan(
            Options o,
            TypeFilterPlan typeFilterPlan,
            List<ExportTarget> exportTargets,
            InputFileProvider inputFiles,
            out bool anyExportErrors
        )
        {
            var exportHadErrors = false;
            anyExportErrors = false;
            var isMultiOutputExport = exportTargets.Count > 1;
            var selectedFiles = inputFiles.GetSelectedFiles();
            if (selectedFiles.Length == 0)
            {
                Logger.Warning("No files selected for export after map/filter matching.");
                ObjectIndexJsonlWriter.Current?.RecordError("no_selected_files", "No input files were selected for export.");
                anyExportErrors = true;
                return true;
            }

            var i = 0;
            var loadedBatchCount = 0;

            var path = Path.GetDirectoryName(Path.GetFullPath(selectedFiles[0]));
            ImportHelper.MergeSplitAssets(path);
            var toReadFile = ImportHelper.ProcessingSplitFiles(selectedFiles.ToList());

            foreach (var file in toReadFile)
            {
                assetsManager.LoadPreparedFiles(file);
                if (assetsManager.assetsFileList.Count > 0)
                {
                    loadedBatchCount++;
                    ObjectIndexJsonlWriter.Current?.WriteLoadedMonoScripts(assetsManager.assetsFileList);
                    BuildAssetData(typeFilterPlan.AssetSelectionTypes, o.NameFilter, o.ContainerFilter, ref i);
                    foreach (var target in exportTargets)
                    {
                        if (isMultiOutputExport)
                        {
                            Logger.Info($"[{target.Label}] Exporting {target.ExportType} assets to {target.Output.FullName}");
                        }
                        var targetAssets = target.SelectAssets(exportableAssets);
                        var result = ExportAssets(target.Output.FullName, targetAssets, o.GroupAssetsType, target.ExportType);
                        var skippedCount = result.RequestedCount - result.ExportedCount;
                        var incompleteIndex = ObjectIndexJsonlWriter.Current != null && skippedCount > 0;
                        if (result.ErrorCount > 0 || incompleteIndex)
                        {
                            exportHadErrors = true;
                            if (result.ErrorCount > 0)
                            {
                                ObjectIndexJsonlWriter.Current?.RecordError(
                                    "asset_export_error",
                                    $"{target.Label} reported {result.ErrorCount} export error(s)."
                                );
                            }
                            if (incompleteIndex)
                            {
                                ObjectIndexJsonlWriter.Current?.RecordError(
                                    "asset_export_incomplete",
                                    $"{target.Label} exported {result.ExportedCount}/{result.RequestedCount} requested asset(s)."
                                );
                            }
                            if (isMultiOutputExport)
                            {
                                Logger.Error($"[{target.Label}] Export failed for {result.ErrorCount} assets.");
                            }
                        }
                    }
                }
                exportableAssets.Clear();
                assetsManager.Clear();
            }

            if (loadedBatchCount == 0 && ObjectIndexJsonlWriter.Current != null)
            {
                exportHadErrors = true;
                ObjectIndexJsonlWriter.Current.RecordError(
                    "no_loaded_asset_files",
                    "Selected inputs produced no loaded serialized asset files."
                );
            }

            anyExportErrors = exportHadErrors;
            return exportHadErrors;
        }

        private sealed class TypeFilterPlan
        {
            public TypeFilterPlan(ClassIDType[] primaryExportTypes, ClassIDType[] secondaryExportTypes, ClassIDType[] assetSelectionTypes)
            {
                PrimaryExportTypes = primaryExportTypes;
                SecondaryExportTypes = secondaryExportTypes;
                AssetSelectionTypes = assetSelectionTypes;
            }

            public ClassIDType[] PrimaryExportTypes { get; }
            public ClassIDType[] SecondaryExportTypes { get; }
            public ClassIDType[] AssetSelectionTypes { get; }
        }

        private sealed class ExportTarget
        {
            private readonly HashSet<ClassIDType> typeFilter;

            public ExportTarget(DirectoryInfo output, ExportType exportType, string label, ClassIDType[] typeFilter)
            {
                Output = output;
                ExportType = exportType;
                Label = label;
                this.typeFilter = typeFilter.IsNullOrEmpty()
                    ? new HashSet<ClassIDType>()
                    : new HashSet<ClassIDType>(typeFilter);
            }

            public DirectoryInfo Output { get; }
            public ExportType ExportType { get; }
            public string Label { get; }

            public List<AssetItem> SelectAssets(List<AssetItem> assets) =>
                typeFilter.Count == 0 ? assets : assets.Where(asset => typeFilter.Contains(asset.Type)).ToList();
        }

        private sealed class InputFileProvider
        {
            private readonly FileInfo input;
            private string[] files;

            public InputFileProvider(FileInfo input)
            {
                this.input = input;
            }

            public int SelectedFileCount => files?.Length ?? 0;

            public string[] GetInputFiles()
            {
                if (files != null)
                {
                    return files;
                }

                Logger.Info("Scanning for files...");
                files = input.Attributes.HasFlag(FileAttributes.Directory)
                    ? Directory.GetFiles(input.FullName, "*.*", SearchOption.AllDirectories).OrderBy(x => x.Length).ToArray()
                    : new string[] { input.FullName };
                Logger.Info($"Found {files.Length} files");
                return files;
            }

            public string[] GetSelectedFiles() => files ?? GetInputFiles();

            public void SetSelectedFiles(IEnumerable<string> selectedFiles)
            {
                files = selectedFiles.ToArray();
            }
        }

    }
}
