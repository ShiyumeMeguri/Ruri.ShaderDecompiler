using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ruri.ShaderDecompiler.Unreal
{
    /// <summary>
    /// Utility class for parsing UE shader binding names.
    /// 
    /// Based on UE 5.4.4 source: MaterialUniformExpressions.cpp:416-437
    /// 
    /// Naming conventions:
    /// - Texture2D_{i} + Texture2D_{i}Sampler       → Standard2D
    /// - TextureCube_{i} + TextureCube_{i}Sampler   → Cube
    /// - Texture2DArray_{i}                         → Array2D
    /// - TextureCubeArray_{i}                       → ArrayCube
    /// - VolumeTexture_{i}                          → Volume
    /// - ExternalTexture_{i}                        → External
    /// - VirtualTexturePhysical_{i}                 → Virtual (RVT/SVT)
    /// - SparseVolumeTexturePageTable_{i}           → SparseVolume
    /// 
    /// NOTE: For precise parameter name mapping, use PreciseParameterMapper instead.
    /// </summary>
    public static class TextureBindingParser
    {
        /// <summary>
        /// Maps texture type prefixes to their EMaterialTextureParameterType
        /// </summary>
        public enum TextureSlotType
        {
            Standard2D,
            Cube,
            Array2D,
            ArrayCube,
            Volume,
            External,
            Virtual,
            SparseVolume,
            Unknown
        }

        private static readonly Regex TextureBindingPattern = new Regex(
            @"^(?:Material[_\.])?(?<prefix>Texture2D|TextureCube|Texture2DArray|TextureCubeArray|VolumeTexture|ExternalTexture|VirtualTexturePhysical|SparseVolumeTexture\w*)_(?<index>\d+)(?<sampler>Sampler)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parses a UE material texture binding name into its components.
        /// </summary>
        /// <param name="bindingName">e.g., "Material.Texture2D_0" or "Texture2D_0"</param>
        /// <returns>Tuple of (type, index, isSampler) or null if not a texture binding</returns>
        public static (TextureSlotType Type, int Index, bool IsSampler)? ParseTextureBinding(string bindingName)
        {
            if (string.IsNullOrEmpty(bindingName))
                return null;

            var match = TextureBindingPattern.Match(bindingName);
            if (!match.Success)
                return null;

            var prefix = match.Groups["prefix"].Value;
            var index = int.Parse(match.Groups["index"].Value);
            var isSampler = match.Groups["sampler"].Success;

            var type = prefix switch
            {
                "Texture2D" => TextureSlotType.Standard2D,
                "TextureCube" => TextureSlotType.Cube,
                "Texture2DArray" => TextureSlotType.Array2D,
                "TextureCubeArray" => TextureSlotType.ArrayCube,
                "VolumeTexture" => TextureSlotType.Volume,
                "ExternalTexture" => TextureSlotType.External,
                "VirtualTexturePhysical" => TextureSlotType.Virtual,
                _ when prefix.StartsWith("SparseVolumeTexture", StringComparison.OrdinalIgnoreCase) => TextureSlotType.SparseVolume,
                _ => TextureSlotType.Unknown
            };

            return (type, index, isSampler);
        }

        /// <summary>
        /// Gets the shader binding prefix for a texture type.
        /// </summary>
        public static string GetBindingPrefix(TextureSlotType type) => type switch
        {
            TextureSlotType.Standard2D => "Texture2D",
            TextureSlotType.Cube => "TextureCube",
            TextureSlotType.Array2D => "Texture2DArray",
            TextureSlotType.ArrayCube => "TextureCubeArray",
            TextureSlotType.Volume => "VolumeTexture",
            TextureSlotType.External => "ExternalTexture",
            TextureSlotType.Virtual => "VirtualTexturePhysical",
            TextureSlotType.SparseVolume => "SparseVolumeTexturePageTable",
            _ => "Unknown"
        };
    }
}
