using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnimeStudio
{
    public class StaticBatchInfo
    {
        public ushort firstSubMesh;
        public ushort subMeshCount;

        public StaticBatchInfo(ObjectReader reader)
        {
            firstSubMesh = reader.ReadUInt16();
            subMeshCount = reader.ReadUInt16();
        }
    }

    public abstract class Renderer : Component
    {
        // Keep the serialized Renderer header available to JSON consumers.
        // These values used to be read into constructor locals and discarded,
        // which made source-exact renderer reconstruction impossible even
        // though the parser had already decoded the bytes successfully.
        public bool? m_Enabled;
        public byte? m_CastShadows;
        public byte? m_ReceiveShadows;
        public byte? m_DynamicOccludee;
        public byte? m_StaticShadowCaster;
        public byte? m_RealtimeShadowCaster;
        public byte? m_SubMeshRenderMode;
        public byte? m_CharacterIndex;
        public byte? m_MotionVectors;
        public byte? m_LightProbeUsage;
        public byte? m_ReflectionProbeUsage;
        public byte? m_RayTracingMode;
        public byte? m_RayTraceProcedural;
        public byte? m_RenderFoliageOccluder;
        public uint? m_PlatformSpecificCastShadows;
        public uint? m_RenderingLayerMask;
        public int? m_RendererPriority;
        public ushort? m_LightmapIndex;
        public ushort? m_LightmapIndexDynamic;
        public Vector4? m_LightmapTilingOffset;
        public Vector4? m_LightmapTilingOffsetDynamic;
        public PPtr<Transform> m_StaticBatchRoot;
        public PPtr<Transform> m_ProbeAnchor;
        public PPtr<GameObject> m_LightProbeVolumeOverride;
        public PPtr<Mesh> m_ShadowProxyMesh;
        public uint? m_SortingLayerID;
        public short? m_SortingLayer;
        public short? m_SortingOrder;
        public bool? m_EnableCharacterOutline;
        public bool? m_EnablePerRendererLighting;
        public Vector3? m_PerRendererLightingOffset;
        public PPtr<Transform> m_PerRendererLightingAnchor;
        public uint? m_LightModeMask;
        public float? m_RendererSortingFudge;
        public List<PPtr<Material>> m_Materials;
        public StaticBatchInfo m_StaticBatchInfo;
        public uint[] m_SubsetIndices;
        private bool isNewHeader = false;

        public static bool HasPrope(SerializedType type) => type.Match("F622BC5EE0E86D7BDF8C912DD94DCBF5") || type.Match("9255FA54269ADD294011FDA525B5FCAC");
        public static bool HasCullingDistance(SerializedType type) => type.Match("BFA28DBFE9993C2ABE21B3408666CFD3");
        public static bool HasStreamingMipmapBias(SerializedType type) => type.Match("3086DE02B7269C6DE7E840C57C244649");

        protected Renderer(ObjectReader reader) : base(reader)
        {
            if (version[0] < 5) //5.0 down
            {
                var m_Enabled = reader.ReadBoolean();
                var m_CastShadows = reader.ReadBoolean();
                var m_ReceiveShadows = reader.ReadBoolean();
                var m_LightmapIndex = reader.ReadByte();
            }
            else //5.0 and up
            {
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 4)) //5.4 and up
                {
                    if (reader.Game.Type.IsGI())
                    {
                        CheckHeader(reader, 0x1A);
                    }
                    if (reader.Game.Type.IsBH3())
                    {
                        CheckHeader(reader, 0x12);
                    }
                    m_Enabled = reader.ReadBoolean();
                    m_CastShadows = reader.ReadByte();
                    m_ReceiveShadows = reader.ReadByte();
                    if (version[0] > 2017 || (version[0] == 2017 && version[1] >= 2)) //2017.2 and up
                    {
                        m_DynamicOccludee = reader.ReadByte();
                    }
                    if (reader.Game.Type.IsBH3Group())
                    {
                        var m_AllowHalfResolution = reader.ReadByte();
                        int m_EnableGpuQuery = isNewHeader ? reader.ReadByte() : 0;
                    }
                    if (reader.Game.Type.IsGIGroup())
                    {
                        var m_ReceiveDecals = reader.ReadByte();
                        var m_EnableShadowCulling = reader.ReadByte();
                        var m_EnableGpuQuery = reader.ReadByte();
                        var m_AllowHalfResolution = reader.ReadByte();
                        if (!reader.Game.Type.IsGICB1())
                        {
                            if (reader.Game.Type.IsGI())
                            {
                                var m_AllowPerMaterialProp = isNewHeader ? reader.ReadByte() : 0;
                            }
                            var m_IsRainOccluder = reader.ReadByte();
                            if (!reader.Game.Type.IsGICB2())
                            {
                                var m_IsDynamicAOOccluder = reader.ReadByte();
                                if (reader.Game.Type.IsGI())
                                {
                                    var m_IsHQDynamicAOOccluder = reader.ReadByte();
                                    var m_IsCloudObject = reader.ReadByte();
                                    var m_IsInteriorVolume = reader.ReadByte();
                                }
                            }
                            if (!reader.Game.Type.IsGIPack())
                            {
                                var m_IsDynamic = reader.ReadByte();
                            }
                            if (reader.Game.Type.IsGI())
                            {
                                var m_UseTessellation = reader.ReadByte();
                                var m_IsTerrainTessInfo = isNewHeader ? reader.ReadByte() : 0;
                                var m_UseVertexLightInForward = isNewHeader ? reader.ReadByte() : 0;
                                var m_CombineSubMeshInGeoPass = isNewHeader ? reader.ReadByte() : 0;
                            }
                        }
                    }
                    if (version[0] >= 2021) //2021.1 and up
                    {
                        m_StaticShadowCaster = reader.ReadByte();
                        if (reader.Game.Type.IsArknightsEndfieldGroup())
                        {
                            m_RealtimeShadowCaster = reader.ReadByte();
                            m_SubMeshRenderMode = reader.ReadByte();
                            m_CharacterIndex = reader.ReadByte();
                        }
                    }
                    m_MotionVectors = reader.ReadByte();
                    m_LightProbeUsage = reader.ReadByte();
                    m_ReflectionProbeUsage = reader.ReadByte();
                    if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) //2019.3 and up
                    {
                        m_RayTracingMode = reader.ReadByte();
                    }
                    if (version[0] >= 2020 || reader.Game.Type.IsZZZ()) //2020.1 and up
                    {
                        m_RayTraceProcedural = reader.ReadByte();
                    }
                    if (reader.Game.Type.IsHYGCB1())
                    {
                        var m_UseOverrideAABBForCulling = reader.ReadByte();
                    }
                    if (reader.Game.Type.IsGI() || reader.Game.Type.IsGICB3() || reader.Game.Type.IsGICB3Pre())
                    {
                        var m_MeshShowQuality = reader.ReadByte();
                    }
                    if (reader.Game.Type.IsArknightsEndfieldCB3() || reader.Game.Type.IsArknightsEndfield())
                    {
                        m_RenderFoliageOccluder = reader.ReadByte();
                    }
                    reader.AlignStream();
                }
                else
                {
                    m_Enabled = reader.ReadBoolean();
                    reader.AlignStream();
                    m_CastShadows = reader.ReadByte();
                    m_ReceiveShadows = reader.ReadBoolean() ? (byte)1 : (byte)0;
                    reader.AlignStream();
                }

                if (reader.Game.Type.IsArknightsEndfieldCB3() || reader.Game.Type.IsArknightsEndfield())
                {
                    m_PlatformSpecificCastShadows = reader.ReadUInt32();
                }

                if (version[0] >= 2018 || (reader.Game.Type.IsBH3() && isNewHeader)) //2018 and up
                {
                    m_RenderingLayerMask = reader.ReadUInt32();
                }

                if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 3)) //2018.3 and up
                {
                    m_RendererPriority = reader.ReadInt32();
                }

                m_LightmapIndex = reader.ReadUInt16();
                m_LightmapIndexDynamic = reader.ReadUInt16();
                if (reader.Game.Type.IsGIGroup() && (m_LightmapIndex != 0xFFFF || m_LightmapIndexDynamic != 0xFFFF))
                {
                    throw new Exception("Not Supported !! skipping....");
                }
            }

            if (version[0] >= 3) //3.0 and up
            {
                m_LightmapTilingOffset = reader.ReadVector4();
            }

            if (version[0] >= 5) //5.0 and up
            {
                m_LightmapTilingOffsetDynamic = reader.ReadVector4();
            }

            if (reader.Game.Type.IsGIGroup())
            {
                var m_ViewDistanceRatio = reader.ReadSingle();
                var m_ShaderLODDistanceRatio = reader.ReadSingle();
            }
            if (reader.Game.Type.IsHYGCB1())
            {
                var m_ViewDistanceRatio = reader.ReadSingle();
            }
            var m_MaterialsSize = reader.ReadInt32();
            m_Materials = new List<PPtr<Material>>();
            for (int i = 0; i < m_MaterialsSize; i++)
            {
                m_Materials.Add(new PPtr<Material>(reader));
            }

            if (version[0] < 3) //3.0 down
            {
                m_LightmapTilingOffset = reader.ReadVector4();
            }
            else //3.0 and up
            {
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 5)) //5.5 and up
                {
                    m_StaticBatchInfo = new StaticBatchInfo(reader);
                }
                else
                {
                    m_SubsetIndices = reader.ReadUInt32Array();
                }

                m_StaticBatchRoot = new PPtr<Transform>(reader);
            }

            if (reader.Game.Type.IsGIGroup())
            {
                var m_MatLayers = reader.ReadInt32();
            }

            if (!reader.Game.Type.IsSR() || !HasPrope(reader.serializedType))
            {
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 4)) //5.4 and up
                {
                    m_ProbeAnchor = new PPtr<Transform>(reader);
                    m_LightProbeVolumeOverride = new PPtr<GameObject>(reader);
                }
                else if (version[0] > 3 || (version[0] == 3 && version[1] >= 5)) //3.5 - 5.3
                {
                    var m_UseLightProbes = reader.ReadBoolean();
                    reader.AlignStream();

                    if (version[0] >= 5)//5.0 and up
                    {
                        var m_ReflectionProbeUsage = reader.ReadInt32();
                    }

                    m_ProbeAnchor = new PPtr<Transform>(reader); //5.0 and up m_ProbeAnchor
                }
            }

            if (reader.Game.Type.IsArknightsEndfieldCB3() || reader.Game.Type.IsArknightsEndfield())
            {
                m_ShadowProxyMesh = new PPtr<Mesh>(reader);
            }

            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                if (version[0] == 4 && version[1] == 3) //4.3
                {
                    var m_SortingLayer = reader.ReadInt16();
                }
                else
                {
                    m_SortingLayerID = reader.ReadUInt32();
                }

                //SInt16 m_SortingLayer 5.6 and up
                m_SortingLayer = reader.ReadInt16();
                m_SortingOrder = reader.ReadInt16();
                reader.AlignStream();
                if (reader.Game.Type.IsGIGroup() || reader.Game.Type.IsBH3())
                {
                    var m_UseHighestMip = reader.ReadBoolean();
                    reader.AlignStream();
                }
                if (reader.Game.Type.IsSR())
                {
                    var RenderFlag = reader.ReadUInt32();
                    if (HasStreamingMipmapBias(reader.serializedType))
                    {
                        var m_StreamingMipmapBias = reader.ReadSingle();
                    }
                    reader.AlignStream();
                }
                if (reader.Game.Type.IsZZZ())
                {
                    var m_NeedHizCulling = reader.ReadBoolean();
                    var m_HighShadingRate = reader.ReadBoolean();
                    var m_RayTracingLayerMask = reader.ReadBoolean();
                    reader.AlignStream();
                    if (HasCullingDistance(reader.serializedType))
                    {
                        var m_CullingDistance = reader.ReadSingle();
                    }
                }
                if (reader.Game.Type.IsArknightsEndfieldCB3() || reader.Game.Type.IsArknightsEndfield())
                {
                    m_EnableCharacterOutline = reader.ReadBoolean();
                    m_EnablePerRendererLighting = reader.ReadBoolean();
                    reader.AlignStream();
                    m_PerRendererLightingOffset = reader.ReadVector3();
                    m_PerRendererLightingAnchor = new PPtr<Transform>(reader);
                    m_LightModeMask = reader.ReadUInt32();
                    m_RendererSortingFudge = reader.ReadSingle();
                    reader.AlignStream();
                }
            }
        }

        private void CheckHeader(ObjectReader reader, int offset)
        {
            short value = 0;
            var pos = reader.Position;
            while (value != -1 && reader.Position <= pos + offset)
            {
                value = reader.ReadInt16();
            }
            isNewHeader = (reader.Position - pos) == offset;
            reader.Position = pos;
        }
    }
}
