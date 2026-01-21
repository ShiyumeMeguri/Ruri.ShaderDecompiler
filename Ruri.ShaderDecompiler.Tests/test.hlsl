// Comprehensive Benchmark Shader - All Resource Types
// Tests: CBs, Textures (1D/2D/3D/Cube/Array), Samplers, UAVs, SRVs, StructuredBuffers

// ============ Constant Buffers ============
cbuffer ViewConstants : register(b0)
{
    float4x4 ViewMatrix;
    float4x4 ProjectionMatrix;
    float4x4 ViewProjectionMatrix;
    float3 CameraPosition;
    float CameraNearZ;
    float3 CameraDirection;
    float CameraFarZ;
};

cbuffer MaterialConstants : register(b1)
{
    float4 BaseColor;
    float Roughness;
    float Metallic;
    float2 UVScale;
    float3 EmissiveColor;
    float EmissiveIntensity;
};

cbuffer LightConstants : register(b2)
{
    float4 LightPositions[4];
    float4 LightColors[4];
    float4 LightParams[4];  // x=radius, y=intensity, z=falloff, w=type
    int ActiveLightCount;
    float3 AmbientColor;
};

// ============ Textures ============
Texture2D<float4> DiffuseTexture : register(t0);
Texture2D<float4> NormalTexture : register(t1);
Texture2D<float4> RoughnessMetallicTexture : register(t2);
Texture2D<float4> EmissiveTexture : register(t3);
TextureCube<float4> EnvironmentMap : register(t4);
Texture2D<float> ShadowMap : register(t5);
Texture3D<float4> VolumeTexture : register(t6);
Texture2DArray<float4> TextureArray : register(t7);

// ============ Samplers ============
SamplerState LinearSampler : register(s0);
SamplerState PointSampler : register(s1);
SamplerComparisonState ShadowSampler : register(s2);

// ============ UAVs (for compute/pixel output) ============
RWTexture2D<float4> OutputTexture : register(u0);
RWStructuredBuffer<float4> OutputBuffer : register(u1);

// ============ Structured Buffers ============
StructuredBuffer<float4> VertexBuffer : register(t8);
StructuredBuffer<uint> IndexBuffer : register(t9);

// ============ Input/Output ============
struct VSOutput
{
    float4 Position : SV_POSITION;
    float3 WorldPosition : TEXCOORD0;
    float3 WorldNormal : TEXCOORD1;
    float2 TexCoord : TEXCOORD2;
    float4 ShadowCoord : TEXCOORD3;
};

struct PSOutput
{
    float4 Color : SV_Target0;
    float4 Normal : SV_Target1;
    float Depth : SV_Depth;
};

// ============ Helper Functions ============
float3 SampleNormalMap(float2 uv, float3 worldNormal)
{
    float3 normalSample = NormalTexture.Sample(LinearSampler, uv).xyz * 2.0 - 1.0;
    return normalize(worldNormal + normalSample * 0.5);
}

float CalculateShadow(float4 shadowCoord)
{
    float3 projCoord = shadowCoord.xyz / shadowCoord.w;
    projCoord.xy = projCoord.xy * 0.5 + 0.5;
    projCoord.y = 1.0 - projCoord.y;
    return ShadowMap.SampleCmpLevelZero(ShadowSampler, projCoord.xy, projCoord.z);
}

float3 SampleEnvironment(float3 reflectDir, float roughness)
{
    float mipLevel = roughness * 8.0;
    return EnvironmentMap.SampleLevel(LinearSampler, reflectDir, mipLevel).rgb;
}

// ============ Main Pixel Shader ============
PSOutput main(VSOutput input)
{
    PSOutput output;
    
    // Sample textures
    float4 diffuse = DiffuseTexture.Sample(LinearSampler, input.TexCoord * UVScale);
    float3 normal = SampleNormalMap(input.TexCoord * UVScale, input.WorldNormal);
    float4 roughMetal = RoughnessMetallicTexture.Sample(LinearSampler, input.TexCoord * UVScale);
    float4 emissive = EmissiveTexture.Sample(LinearSampler, input.TexCoord * UVScale);
    
    // Material properties
    float roughness = roughMetal.r * Roughness;
    float metallic = roughMetal.g * Metallic;
    
    // View direction
    float3 viewDir = normalize(CameraPosition - input.WorldPosition);
    float3 reflectDir = reflect(-viewDir, normal);
    
    // Lighting
    float3 lighting = AmbientColor * diffuse.rgb;
    
    for (int i = 0; i < min(ActiveLightCount, 4); i++)
    {
        float3 lightDir = normalize(LightPositions[i].xyz - input.WorldPosition);
        float dist = length(LightPositions[i].xyz - input.WorldPosition);
        float attenuation = 1.0 / (1.0 + dist * dist * LightParams[i].z);
        
        float NdotL = max(dot(normal, lightDir), 0.0);
        lighting += LightColors[i].rgb * LightParams[i].y * NdotL * attenuation * diffuse.rgb;
    }
    
    // Shadow
    float shadow = CalculateShadow(input.ShadowCoord);
    lighting *= shadow;
    
    // Environment reflection
    float3 envColor = SampleEnvironment(reflectDir, roughness);
    lighting = lerp(lighting, envColor, metallic);
    
    // Emissive
    lighting += emissive.rgb * EmissiveColor * EmissiveIntensity;
    
    // Apply base color tint
    output.Color = float4(lighting * BaseColor.rgb, diffuse.a * BaseColor.a);
    output.Normal = float4(normal * 0.5 + 0.5, 1.0);
    output.Depth = input.Position.z;
    
    // Write to UAV (for debugging/compute)
    uint2 pixelCoord = uint2(input.Position.xy);
    OutputTexture[pixelCoord] = output.Color;
    
    return output;
}
