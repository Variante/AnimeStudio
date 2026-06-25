using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio
{
    public class AssemblyLoader
    {
        public bool Loaded;
        public int LastLoadFileCount { get; private set; }
        public int LastLoadSuccessCount { get; private set; }
        public int LastLoadFailureCount { get; private set; }
        public int ModuleCount => moduleDic.Count;
        private Dictionary<string, ModuleDefinition> moduleDic = new Dictionary<string, ModuleDefinition>();

        public void Load(string path)
        {
            var files = Directory.GetFiles(path, "*.dll");
            LastLoadFileCount = files.Length;
            LastLoadSuccessCount = 0;
            LastLoadFailureCount = 0;
            var resolver = new MyAssemblyResolver();
            var readerParameters = new ReaderParameters();
            readerParameters.AssemblyResolver = resolver;
            foreach (var file in files)
            {
                try
                {
                    var assembly = AssemblyDefinition.ReadAssembly(file, readerParameters);
                    resolver.Register(assembly);
                    if (!moduleDic.ContainsKey(assembly.MainModule.Name))
                    {
                        moduleDic.Add(assembly.MainModule.Name, assembly.MainModule);
                        LastLoadSuccessCount++;
                    }
                    else
                    {
                        assembly.Dispose();
                        LastLoadFailureCount++;
                    }
                }
                catch
                {
                    LastLoadFailureCount++;
                }
            }
            Loaded = moduleDic.Count > 0;
        }

        public TypeDefinition GetTypeDefinition(string assemblyName, string fullName)
        {
            if (string.IsNullOrEmpty(assemblyName) || string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            if (moduleDic.TryGetValue(assemblyName, out var module)
                || (!assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && moduleDic.TryGetValue($"{assemblyName}.dll", out module)))
            {
                var typeDef = module.GetType(fullName);
                return typeDef ?? GetUniqueTypeDefinition(fullName);
            }
            return GetUniqueTypeDefinition(fullName);
        }

        private TypeDefinition GetUniqueTypeDefinition(string fullName)
        {
            TypeDefinition match = null;
            foreach (var pair in moduleDic)
            {
                var typeDef = pair.Value.GetType(fullName);
                if (typeDef == null)
                {
                    continue;
                }
                if (match != null)
                {
                    return null;
                }
                match = typeDef;
            }
            return match;
        }

        public void Clear()
        {
            foreach (var pair in moduleDic)
            {
                pair.Value.Dispose();
            }
            moduleDic.Clear();
            Loaded = false;
            LastLoadFileCount = 0;
            LastLoadSuccessCount = 0;
            LastLoadFailureCount = 0;
        }
    }
}
