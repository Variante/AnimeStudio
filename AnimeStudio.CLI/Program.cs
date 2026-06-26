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

                    var exportTexture2D = false;
                    var exportMaterial = false;
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

                                if (type == ClassIDType.Texture2D)
                                {
                                    exportTexture2D = flag.HasFlag(TypeFlag.Export);
                                }
                                else if (type == ClassIDType.Material)
                                {
                                    exportMaterial = flag.HasFlag(TypeFlag.Export);
                                }

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
                    classTypeFilter = classTypeFilterList.ToArray();

                    if (ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport())
                    {
                        TypeFlags.SetType(ClassIDType.Texture2D, true, exportTexture2D);
                        if (Settings.Default.exportMaterials)
                        {
                            TypeFlags.SetType(ClassIDType.Material, true, exportMaterial);
                        }
                        if (ClassIDType.GameObject.CanExport())
                        {
                            TypeFlags.SetType(ClassIDType.Animator, true, false);
                        }
                        else if(ClassIDType.Animator.CanExport())
                        {
                            TypeFlags.SetType(ClassIDType.GameObject, true, false);
                        }
                    }
                }
                if (o.GroupAssetsType == AssetGroupOption.ByContainer)
                {
                    TypeFlags.SetType(ClassIDType.AssetBundle, true, false);
                }

                if (o.DummyDllFolder != null || o.MonoBehaviourTypeTreePriority == MonoBehaviourTypeTreePriority.ScriptFirst)
                {
                    TypeFlags.SetType(ClassIDType.MonoScript, true, ClassIDType.MonoScript.CanExport());
                }

                assetsManager.Silent = o.Silent;
                assetsManager.Game = game;
                assetsManager.SpecifyUnityVersion = o.UnityVersion;

                var hasSecondaryExportType = o.SecondaryAssetExportType.HasValue;
                var hasSecondaryOutput = o.SecondaryOutput != null;
                if (hasSecondaryExportType != hasSecondaryOutput)
                {
                    Console.Error.WriteLine("--secondary_export_type and --secondary_export_path must be specified together.");
                    Environment.ExitCode = 1;
                    return;
                }

                var exportTargets = new List<(DirectoryInfo Output, ExportType ExportType, string Label, ClassIDType[] TypeFilter)>
                {
                    (o.Output, o.AssetExportType, "primary", primaryClassTypeFilter)
                };

                o.Output.Create();
                if (hasSecondaryExportType)
                {
                    o.SecondaryOutput.Create();
                    exportTargets.Add((o.SecondaryOutput, o.SecondaryAssetExportType.Value, "secondary", secondaryClassTypeFilter));
                    Logger.Info($"[SecondaryExport] Also exporting {o.SecondaryAssetExportType.Value} assets to {o.SecondaryOutput.FullName}");
                }

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

                if (o.FilterDataFile != null && o.FilterDataFile.Exists)
                {
                    var filterJson = File.ReadAllText(o.FilterDataFile.FullName);
                    var filterItems = JsonConvert.DeserializeObject<List<AssetsManager.AssetFilterDataItem>>(filterJson);
                    if (filterItems != null && filterItems.Count > 0)
                    {
                        assetsManager.FilterData.Items.AddRange(filterItems);
                        Logger.Info($"[FilterData] Loaded {filterItems.Count} items from {o.FilterDataFile.FullName}");
                    }
                }

                string[] files = null;
                string[] GetInputFiles()
                {
                    if (files != null)
                    {
                        return files;
                    }

                    Logger.Info("Scanning for files...");
                    files = o.Input.Attributes.HasFlag(FileAttributes.Directory) ? Directory.GetFiles(o.Input.FullName, "*.*", SearchOption.AllDirectories).OrderBy(x => x.Length).ToArray() : new string[] { o.Input.FullName };
                    Logger.Info($"Found {files.Length} files");
                    return files;
                }

                if (o.MapOp.HasFlag(MapOpType.CABMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        AssetsHelper.LoadCABMapInternal(o.MapName);
                        assetsManager.ResolveDependencies = true;
                    }
                    else
                    {
                        AssetsHelper.BuildCABMap(GetInputFiles(), o.MapName, o.Input.FullName, game);
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.AssetMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        var matchedEntries = AssetsHelper.ParseAssetMapEntries(o.MapName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter);
                        files = matchedEntries.Select(entry => entry.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        assetsManager.FilterData.Items.AddRange(matchedEntries.Select(entry => new AssetsManager.AssetFilterDataItem
                        {
                            Source = entry.Source,
                            Offset = entry.Offset,
                            Name = entry.Name,
                            PathID = entry.PathID,
                            Type = entry.Type,
                        }));
                        Logger.Info($"[AssetMap] Loaded {matchedEntries.Count} matching map entries across {files.Length} files");
                    }
                    else
                    {
                        Task.Run(() => AssetsHelper.BuildAssetMap(GetInputFiles(), o.MapName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.Both))
                {
                    Task.Run(() => AssetsHelper.BuildBoth(GetInputFiles(), o.MapName, o.Input.FullName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                }
                if (o.MapOp.Equals(MapOpType.None) || o.MapOp.HasFlag(MapOpType.Load))
                {
                    var exportHadErrors = false;
                    var isMultiOutputExport = exportTargets.Count > 1;
                    var selectedFiles = files ?? GetInputFiles();
                    if (selectedFiles.Length == 0)
                    {
                        Logger.Warning("No files selected for export after map/filter matching.");
                        return;
                    }

                    var i = 0;

                    var path = Path.GetDirectoryName(Path.GetFullPath(selectedFiles[0]));
                    ImportHelper.MergeSplitAssets(path);
                    var toReadFile = ImportHelper.ProcessingSplitFiles(selectedFiles.ToList());

                    var fileList = new List<string>(toReadFile);
                    foreach (var file in fileList)
                    {
                        assetsManager.LoadPreparedFiles(file);
                        if (assetsManager.assetsFileList.Count > 0)
                        {
                            BuildAssetData(classTypeFilter, o.NameFilter, o.ContainerFilter, ref i);
                            foreach (var target in exportTargets)
                            {
                                if (isMultiOutputExport)
                                {
                                    Logger.Info($"[{target.Label}] Exporting {target.ExportType} assets to {target.Output.FullName}");
                                }
                                var targetAssets = target.TypeFilter.IsNullOrEmpty()
                                    ? exportableAssets
                                    : exportableAssets.Where(asset => target.TypeFilter.Contains(asset.Type)).ToList();
                                var result = ExportAssets(target.Output.FullName, targetAssets, o.GroupAssetsType, target.ExportType);
                                if (result.ErrorCount > 0)
                                {
                                    exportHadErrors = true;
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
                    if (isMultiOutputExport && exportHadErrors)
                    {
                        Environment.ExitCode = 1;
                    }
                }
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
    }
}
