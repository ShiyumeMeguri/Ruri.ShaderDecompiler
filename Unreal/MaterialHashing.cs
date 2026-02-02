using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ruri.ShaderDecompiler.Unreal
{
    // Replicating UE Enums and Structs
    public enum EMaterialShaderMapUsage
    {
        Default,
        MaterialExportCustomOutput,
        // ... add others if needed
    }

    public enum EMaterialQualityLevel
    {
        Low,
        High,
        Medium,
        Epic,
        Num
    }

    public class FSHAHash
    {
        public byte[] Hash = new byte[20];

        public override string ToString()
        {
            return BitConverter.ToString(Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    public enum ERHIFeatureLevel
    {
        ES2_REMOVED = 0,
        ES3_1 = 1,
        SM4_REMOVED = 2,
        SM5 = 3,
        SM6 = 4,
        Num
    }

    public class FMaterialShaderMapId
    {
        public EMaterialShaderMapUsage Usage;
        public Guid BaseMaterialId;
        public EMaterialQualityLevel QualityLevel;
        public int FeatureLevel; // Serialized as int, corresponds to ERHIFeatureLevel
        public string UsageCustomOutput;

        public List<FStaticSwitchParameter> StaticSwitchParameters = new List<FStaticSwitchParameter>();
        public List<FStaticComponentMaskParameter> StaticComponentMaskParameters = new List<FStaticComponentMaskParameter>();
        public List<Guid> ReferencedFunctions = new List<Guid>();
        public List<Guid> ReferencedParameterCollections = new List<Guid>();
        public List<Guid> ReferencedTextures = new List<Guid>(); // For TextureReferencesHash

        public void GetMaterialHash(out byte[] OutHash)
        {
            using (var sha1 = SHA1.Create())
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // Usage
                writer.Write((int)Usage);
                
                // UsageCustomOutput
                if (Usage == EMaterialShaderMapUsage.MaterialExportCustomOutput)
                {
                   WriteUnicodeString(writer, UsageCustomOutput);
                }

                // BaseMaterialId
                writer.Write(BaseMaterialId.ToByteArray());

                // QualityLevel (String serialization)
                string qualityName = QualityLevel.ToString(); // "High", "Low", etc.
                WriteUnicodeString(writer, qualityName);

                // FeatureLevel (Int)
                writer.Write(FeatureLevel);

                // Static Parameters
                // Note: UE sorts these? No, they are usually in array order or sorted by Name.
                // We should assume the list passed here is already in the correct order or sort it if UE does.
                // In MaterialShader.cpp: "HashState.Update((const uint8*)&Usage, sizeof(Usage));" ... then iterates arrays.
                
                foreach (var p in StaticSwitchParameters) p.UpdateHash(writer);
                foreach (var p in StaticComponentMaskParameters) p.UpdateHash(writer);
                // TerrainLayerWeightParameters skipped for now (usually empty)
                
                // MaterialLayersId skipped for now

                // ReferencedFunctions
                foreach (var guid in ReferencedFunctions) writer.Write(guid.ToByteArray());

                // ReferencedParameterCollections
                foreach (var guid in ReferencedParameterCollections) writer.Write(guid.ToByteArray());

                // VertexFactoryTypeDependencies skipped for now

                // TextureReferencesHash
                // This is a SHA1 of the sorted list of Texture GUIDs
                byte[] textureHash = ComputeTextureReferencesHash();
                writer.Write(textureHash);

                // ExpressionIncludesHash
                // We assume 0 for now as it's hard to compute
                 writer.Write(new byte[20]);

                writer.Flush();
                ms.Position = 0;
                OutHash = sha1.ComputeHash(ms);
            }
        }

        private byte[] ComputeTextureReferencesHash()
        {
            // Sort GUIDs to ensure deterministic hash
            var sorted = ReferencedTextures.OrderBy(g => g).ToList();
            using (var sha1 = SHA1.Create())
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                foreach (var g in sorted)
                {
                    writer.Write(g.ToByteArray());
                }
                writer.Flush();
                ms.Position = 0;
                return sha1.ComputeHash(ms);
            }
        }

        private void WriteUnicodeString(BinaryWriter writer, string s)
        {
             // UE usually serializes string length + bytes.
             // HashState.UpdateWithString uses: Length (int32) + Bytes.
             // If string is null/empty, length is 0.
             if (string.IsNullOrEmpty(s))
             {
                 writer.Write(0);
                 return;
             }
             byte[] bytes = Encoding.Unicode.GetBytes(s); // UE uses TCHAR (UTF16) usually for Strings unless specified otherwise.
             // Wait, UpdateWithString in UE:
             // HashState.Update((const uint8*)Text, Len * sizeof(TCHAR));
             writer.Write(s.Length); // Length in TCHARs
             writer.Write(bytes);
        }
    }

    public class FStaticSwitchParameter
    {
        public string Name;
        public bool Value;
        public bool bOverride;
        public Guid ExpressionGuid;

        public void UpdateHash(BinaryWriter writer)
        {
             // FStaticSwitchParameter::UpdateHash
             // HashState.Update((const uint8*)&ParameterInfo, sizeof(ParameterInfo)); // ParameterInfo includes Name (FName)
             // HashState.Update((const uint8*)&Value, sizeof(Value));
             // HashState.Update((const uint8*)&bOverride, sizeof(bOverride));
             // HashState.Update((const uint8*)&ExpressionGuid, sizeof(ExpressionGuid));

             // FName serialization in Hash: usually Name.ToString()
             WriteFName(writer, Name);
             writer.Write(Value);
             writer.Write(bOverride);
             writer.Write(ExpressionGuid.ToByteArray());
        }

        private void WriteFName(BinaryWriter writer, string name)
        {
            // Replicate FName hashing/serialization
            if (string.IsNullOrEmpty(name))
            {
                writer.Write(0);
                return;
            }
            // Often FName is hashed by string content.
             byte[] bytes = Encoding.Unicode.GetBytes(name);
             writer.Write(name.Length);
             writer.Write(bytes);
        }
    }

    public class FStaticComponentMaskParameter
    {
        public string Name;
        public bool R, G, B, A;
        public bool bOverride;
        public Guid ExpressionGuid;

        public void UpdateHash(BinaryWriter writer)
        {
            WriteFName(writer, Name);
            writer.Write(R);
            writer.Write(G);
            writer.Write(B);
            writer.Write(A);
            writer.Write(bOverride);
            writer.Write(ExpressionGuid.ToByteArray());
        }
         private void WriteFName(BinaryWriter writer, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                writer.Write(0);
                return;
            }
             byte[] bytes = Encoding.Unicode.GetBytes(name);
             writer.Write(name.Length);
             writer.Write(bytes);
        }
    }
}
