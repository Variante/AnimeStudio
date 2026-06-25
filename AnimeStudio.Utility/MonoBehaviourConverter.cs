using System.Collections.Generic;

using System;

namespace AnimeStudio
{
    public sealed class MonoBehaviourTypeTreeConversion
    {
        public TypeTree TypeTree { get; set; }
        public bool MonoScriptResolved { get; set; }
        public bool TypeDefinitionResolved { get; set; }
        public string ScriptClassName { get; set; } = "";
        public string ScriptNamespace { get; set; } = "";
        public string ScriptFullName { get; set; } = "";
        public string ScriptAssemblyName { get; set; } = "";
        public string ScriptIdentitySource { get; set; } = "";
        public string Status { get; set; } = "notAttempted";
        public Exception Exception { get; set; }

        public int NodeCount => TypeTree?.m_Nodes?.Count ?? 0;
    }

    public static class MonoBehaviourConverter
    {
        public static TypeTree ConvertToTypeTree(this MonoBehaviour m_MonoBehaviour, AssemblyLoader assemblyLoader)
        {
            return m_MonoBehaviour.ConvertToTypeTreeWithDiagnostics(assemblyLoader).TypeTree;
        }

        public static MonoBehaviourTypeTreeConversion ConvertToTypeTreeWithDiagnostics(this MonoBehaviour m_MonoBehaviour, AssemblyLoader assemblyLoader)
        {
            var m_Type = new TypeTree();
            m_Type.m_Nodes = new List<TypeTreeNode>();
            var helper = new SerializedTypeHelper(m_MonoBehaviour.version);
            helper.AddMonoBehaviour(m_Type.m_Nodes, 0);
            var result = new MonoBehaviourTypeTreeConversion
            {
                TypeTree = m_Type,
                Status = "monoScriptUnresolved",
            };
            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
            {
                result.MonoScriptResolved = true;
                result.ScriptClassName = m_Script.m_ClassName ?? "";
                result.ScriptNamespace = m_Script.m_Namespace ?? "";
                result.ScriptFullName = string.IsNullOrEmpty(result.ScriptNamespace)
                    ? result.ScriptClassName
                    : $"{result.ScriptNamespace}.{result.ScriptClassName}";
                result.ScriptAssemblyName = m_Script.m_AssemblyName ?? "";
                result.ScriptIdentitySource = "monoScript";
            }
            else if (TryUseSerializedTypeScriptIdentity(m_MonoBehaviour.serializedType, result))
            {
                result.ScriptIdentitySource = "serializedType";
            }
            else
            {
                return result;
            }

            if (assemblyLoader == null || !assemblyLoader.Loaded)
            {
                result.Status = "dummyDllsNotLoaded";
                return result;
            }

            var typeDef = assemblyLoader.GetTypeDefinition(result.ScriptAssemblyName, result.ScriptFullName);
            if (typeDef != null)
            {
                result.TypeDefinitionResolved = true;
                try
                {
                    var typeDefinitionConverter = new TypeDefinitionConverter(typeDef, helper, 1);
                    m_Type.m_Nodes.AddRange(typeDefinitionConverter.ConvertToTypeTreeNodes());
                    result.Status = "resolved";
                }
                catch (Exception ex)
                {
                    result.Exception = ex;
                    result.Status = "typeTreeConversionFailed";
                }
            }
            else
            {
                result.Status = "typeDefinitionNotFound";
            }
            return result;
        }

        private static bool TryUseSerializedTypeScriptIdentity(SerializedType serializedType, MonoBehaviourTypeTreeConversion result)
        {
            if (serializedType == null
                || string.IsNullOrEmpty(serializedType.m_KlassName)
                || string.IsNullOrEmpty(serializedType.m_AsmName))
            {
                return false;
            }

            result.ScriptClassName = serializedType.m_KlassName ?? "";
            result.ScriptNamespace = serializedType.m_NameSpace ?? "";
            result.ScriptFullName = string.IsNullOrEmpty(result.ScriptNamespace)
                ? result.ScriptClassName
                : $"{result.ScriptNamespace}.{result.ScriptClassName}";
            result.ScriptAssemblyName = serializedType.m_AsmName ?? "";
            return true;
        }
    }
}
