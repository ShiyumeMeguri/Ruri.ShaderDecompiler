using System;
using System.Collections.Generic;

namespace Ruri.ShaderDecompiler.Unreal
{
    /// <summary>
    /// Material parameter types from EMaterialParameterType (MaterialTypes.h)
    /// Source: UnrealEngine-5.4.4-release/Engine/Source/Runtime/Engine/Public/MaterialTypes.h:186-204
    /// </summary>
    public enum EMaterialParameterType : byte
    {
        Scalar = 0,              // float, SetScalarParameterValue
        Vector = 1,              // FLinearColor, SetVectorParameterValue
        DoubleVector = 2,        // FVector4d, SetDoubleVectorParameterValue
        Texture = 3,             // UTexture*, SetTextureParameterValue
        Font = 4,                // UFont* + int32 FontPage, SetFontParameterValue
        RuntimeVirtualTexture = 5, // URuntimeVirtualTexture*, SetRuntimeVirtualTextureParameterValue
        SparseVolumeTexture = 6, // USparseVolumeTexture*, SetSparseVolumeTextureParameterValue
        StaticSwitch = 7,        // bool (static, compile-time only)

        NumRuntime = 7,          // Runtime parameter types must go above here

        StaticComponentMask = 7, // bool R/G/B/A (static, compile-time only)

        Num = 8,
        None = 0xff,
    }

    /// <summary>
    /// Material parameter association from EMaterialParameterAssociation (MaterialTypes.h)
    /// Source: UnrealEngine-5.4.4-release/Engine/Source/Runtime/Engine/Public/MaterialTypes.h:21-26
    /// </summary>
    public enum EMaterialParameterAssociation
    {
        LayerParameter = 0,
        BlendParameter = 1,
        GlobalParameter = 2,
    }

    /// <summary>
    /// Parameter info struct matching FMaterialParameterInfo (MaterialTypes.h:29-94)
    /// </summary>
    public class FMaterialParameterInfo
    {
        public string Name { get; set; } = string.Empty;
        public EMaterialParameterAssociation Association { get; set; } = EMaterialParameterAssociation.GlobalParameter;
        public int Index { get; set; } = -1; // INDEX_NONE

        public FMaterialParameterInfo() { }
        public FMaterialParameterInfo(string name, EMaterialParameterAssociation association = EMaterialParameterAssociation.GlobalParameter, int index = -1)
        {
            Name = name;
            Association = association;
            Index = index;
        }

        public override string ToString() => $"{Name}[{Association}:{Index}]";
        public override int GetHashCode() => HashCode.Combine(Name, Association, Index);
        public override bool Equals(object obj)
        {
            if (obj is FMaterialParameterInfo other)
                return Name == other.Name && Association == other.Association && Index == other.Index;
            return false;
        }
    }

    #region Parameter Value Structures
    // Source: UnrealEngine-5.4.4-release/Engine/Source/Runtime/Engine/Classes/Materials/MaterialInstance.h:70-436

    /// <summary>
    /// FScalarParameterValue (MaterialInstance.h:70-128)
    /// </summary>
    public class FScalarParameterValue
    {
        public FMaterialParameterInfo ParameterInfo { get; set; } = new();
        public float ParameterValue { get; set; }
        public Guid ExpressionGUID { get; set; }

        public override string ToString() => $"Scalar:{ParameterInfo.Name}={ParameterValue}";
    }

    /// <summary>
    /// FVectorParameterValue (MaterialInstance.h:132-181)
    /// </summary>
    public class FVectorParameterValue
    {
        public FMaterialParameterInfo ParameterInfo { get; set; } = new();
        public FLinearColor ParameterValue { get; set; } = new();
        public Guid ExpressionGUID { get; set; }

        public override string ToString() => $"Vector:{ParameterInfo.Name}=({ParameterValue})";
    }

    /// <summary>
    /// FDoubleVectorParameterValue (MaterialInstance.h:185-230)
    /// </summary>
    public class FDoubleVectorParameterValue
    {
        public FMaterialParameterInfo ParameterInfo { get; set; } = new();
        public FVector4d ParameterValue { get; set; } = new();
        public Guid ExpressionGUID { get; set; }

        public override string ToString() => $"DoubleVector:{ParameterInfo.Name}=({ParameterValue})";
    }

    /// <summary>
    /// FTextureParameterValue (MaterialInstance.h:234-283)
    /// </summary>
    public class FTextureParameterValue
    {
        public FMaterialParameterInfo ParameterInfo { get; set; } = new();
        public string TexturePath { get; set; } = string.Empty; // Object path to UTexture
        public Guid ExpressionGUID { get; set; }

        public override string ToString() => $"Texture:{ParameterInfo.Name}={TexturePath}";
    }

    /// <summary>
    /// FRuntimeVirtualTextureParameterValue (MaterialInstance.h:287-331)
    /// </summary>
    public class FRuntimeVirtualTextureParameterValue
    {
        public FMaterialParameterInfo ParameterInfo { get; set; } = new();
        public string TexturePath { get; set; } = string.Empty; // Object path to URuntimeVirtualTexture
        public Guid ExpressionGUID { get; set; }

        public override string ToString() => $"RuntimeVirtualTexture:{ParameterInfo.Name}={TexturePath}";
    }

    /// <summary>
    /// FSparseVolumeTextureParameterValue (MaterialInstance.h:335-379)
    /// </summary>
    public class FSparseVolumeTextureParameterValue
    {
        public FMaterialParameterInfo ParameterInfo { get; set; } = new();
        public string TexturePath { get; set; } = string.Empty; // Object path to USparseVolumeTexture
        public Guid ExpressionGUID { get; set; }

        public override string ToString() => $"SparseVolumeTexture:{ParameterInfo.Name}={TexturePath}";
    }

    /// <summary>
    /// FFontParameterValue (MaterialInstance.h:383-436)
    /// </summary>
    public class FFontParameterValue
    {
        public FMaterialParameterInfo ParameterInfo { get; set; } = new();
        public string FontPath { get; set; } = string.Empty; // Object path to UFont
        public int FontPage { get; set; }
        public Guid ExpressionGUID { get; set; }

        public override string ToString() => $"Font:{ParameterInfo.Name}={FontPath}[Page:{FontPage}]";
    }

    #endregion

    #region Helper Types

    /// <summary>
    /// FLinearColor matching UE's FLinearColor
    /// </summary>
    public struct FLinearColor
    {
        public float R, G, B, A;

        public FLinearColor(float r, float g, float b, float a = 1.0f)
        {
            R = r; G = g; B = b; A = a;
        }

        public override string ToString() => $"R={R:F3},G={G:F3},B={B:F3},A={A:F3}";

        public static FLinearColor Red => new(1, 0, 0, 1);
        public static FLinearColor Green => new(0, 1, 0, 1);
        public static FLinearColor Blue => new(0, 0, 1, 1);
        public static FLinearColor White => new(1, 1, 1, 1);
        public static FLinearColor Black => new(0, 0, 0, 1);
    }

    /// <summary>
    /// FVector4d matching UE's FVector4d
    /// </summary>
    public struct FVector4d
    {
        public double X, Y, Z, W;

        public FVector4d(double x, double y, double z, double w = 1.0)
        {
            X = x; Y = y; Z = z; W = w;
        }

        public override string ToString() => $"X={X:F6},Y={Y:F6},Z={Z:F6},W={W:F6}";
    }

    #endregion

    #region Material Parameter Container

    /// <summary>
    /// Container for all material parameters extracted from a material instance.
    /// Maps to UMaterialInstance parameter arrays (MaterialInstance.h:617-643)
    /// </summary>
    public class FMaterialParameterCollection
    {
        /// <summary>Scalar parameters (float). Source: MaterialInstance.h:618-619</summary>
        public List<FScalarParameterValue> ScalarParameterValues { get; } = new();

        /// <summary>Vector parameters (FLinearColor). Source: MaterialInstance.h:622-623</summary>
        public List<FVectorParameterValue> VectorParameterValues { get; } = new();

        /// <summary>DoubleVector parameters (FVector4d). Source: MaterialInstance.h:626-627</summary>
        public List<FDoubleVectorParameterValue> DoubleVectorParameterValues { get; } = new();

        /// <summary>Texture parameters (UTexture*). Source: MaterialInstance.h:630-631</summary>
        public List<FTextureParameterValue> TextureParameterValues { get; } = new();

        /// <summary>RuntimeVirtualTexture parameters. Source: MaterialInstance.h:634-635</summary>
        public List<FRuntimeVirtualTextureParameterValue> RuntimeVirtualTextureParameterValues { get; } = new();

        /// <summary>SparseVolumeTexture parameters. Source: MaterialInstance.h:638-639</summary>
        public List<FSparseVolumeTextureParameterValue> SparseVolumeTextureParameterValues { get; } = new();

        /// <summary>Font parameters (UFont* + int32 page). Source: MaterialInstance.h:642-643</summary>
        public List<FFontParameterValue> FontParameterValues { get; } = new();

        /// <summary>
        /// Get all parameter names with their types, suitable for shader symbol injection.
        /// Returns a dictionary mapping parameter name -> (type, slot hint if known)
        /// </summary>
        public Dictionary<string, (EMaterialParameterType Type, int Slot)> GetAllParameterBindings()
        {
            var result = new Dictionary<string, (EMaterialParameterType, int)>();
            int slot = 0;

            foreach (var p in ScalarParameterValues)
                result[p.ParameterInfo.Name] = (EMaterialParameterType.Scalar, slot++);

            foreach (var p in VectorParameterValues)
                result[p.ParameterInfo.Name] = (EMaterialParameterType.Vector, slot++);

            foreach (var p in DoubleVectorParameterValues)
                result[p.ParameterInfo.Name] = (EMaterialParameterType.DoubleVector, slot++);

            foreach (var p in TextureParameterValues)
                result[p.ParameterInfo.Name] = (EMaterialParameterType.Texture, slot++);

            foreach (var p in RuntimeVirtualTextureParameterValues)
                result[p.ParameterInfo.Name] = (EMaterialParameterType.RuntimeVirtualTexture, slot++);

            foreach (var p in SparseVolumeTextureParameterValues)
                result[p.ParameterInfo.Name] = (EMaterialParameterType.SparseVolumeTexture, slot++);

            foreach (var p in FontParameterValues)
                result[p.ParameterInfo.Name] = (EMaterialParameterType.Font, slot++);

            return result;
        }

        /// <summary>
        /// Get total number of parameters across all types.
        /// </summary>
        public int TotalParameterCount =>
            ScalarParameterValues.Count +
            VectorParameterValues.Count +
            DoubleVectorParameterValues.Count +
            TextureParameterValues.Count +
            RuntimeVirtualTextureParameterValues.Count +
            SparseVolumeTextureParameterValues.Count +
            FontParameterValues.Count;

        public override string ToString()
        {
            return $"MaterialParameters[Scalar:{ScalarParameterValues.Count}, Vector:{VectorParameterValues.Count}, " +
                   $"DoubleVector:{DoubleVectorParameterValues.Count}, Texture:{TextureParameterValues.Count}, " +
                   $"RVT:{RuntimeVirtualTextureParameterValues.Count}, SVT:{SparseVolumeTextureParameterValues.Count}, " +
                   $"Font:{FontParameterValues.Count}]";
        }
    }

    #endregion
}
