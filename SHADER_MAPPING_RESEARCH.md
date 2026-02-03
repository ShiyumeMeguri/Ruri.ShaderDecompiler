# UE5 Shader Mapping 恢复方案白皮书

## 1. 概述
在UE5中，Shader代码通常被打包在全局的 `.ushaderbytecode` (ShaderArchive) 中，而材质 (`.uasset`) 只存储了对这些Shader的引用。反编译时，我们需要通过这些引用将 Shader 与 材质名 (e.g. `M_Hero`) 关联起来，以便正确命名反编译后的 HLSL 文件。

本项目实现了从游戏数据 (`.utoc`) 中提取 **材质 -> ShaderMapHash** 的映射关系，并将其导出为 JSON 文件。

---

## 2. 原理机制

### 2.1 核心发现
**`.utoc` 文件的 IoStore 容器头 (IoContainerHeader)** 包含了所有打包资源的元数据。其中，`FFilePackageStoreEntry` 结构体直接存储了材质包引用的 ShaderMapHashes。

```cpp
// UE Source: IoContainerHeader.h
struct FFilePackageStoreEntry
{
    TFilePackageStoreEntryCArrayView<FPackageId> ImportedPackages;
    TFilePackageStoreEntryCArrayView<FSHAHash> ShaderMapHashes;  // <--- 关键数据
};
```

### 2.2 运行时流程
当游戏加载一个材质时，它会从 IoStore 读取该材质对应的 `StoreEntry`，获取 `ShaderMapHashes`，然后使用这些 Hash 去全局 ShaderLibrary (`.ushaderbytecode`) 中查找并加载对应的 Shader 代码。

我们利用这一机制，扫描所有 IoStore 条目，提取所有拥有 ShaderMapHashes 的包（即材质），建立全局映射表。

---

## 3. 实现细节 (Ruri.FModelHook)

### 3.1 Hook 逻辑
我们 Hook 了 `CUE4ParseViewModel.ExportData` 方法。只要用户通过 FModel 导出任意 `.ushaderbytecode` (Shader Library) 文件，Hook 就会自动触发。

**代码位置**: `Source/Ruri.FModelHook/Game/SBUE/ShaderDecompiler/UE_ShaderDecompiler_Hook.cs`

### 3.2 执行流程
1.  **检测导出类型**: 如果导出的文件扩展名是 `ushaderbytecode`，则进入 Shader 处理流程。
2.  **导出 Library**: 首先调用 `ShaderArchiveExporter` 导出 `.ushaderlib` 文件（供反编译器使用）。
3.  **提取全局映射**:
    *   遍历 FModel 中所有已挂载的 `IoStoreReader`。
    *   读取每个 Reader 的 `ContainerHeader` -> `StoreEntries`。
    *   检查每个 Entry 是否包含 `ShaderMapHashes`。
    *   如果包含，利用 `PackageIdIndex` 反查出包名 (e.g. `/Game/Characters/M_Skin`)。
    *   将 `包名 -> Hashes` 存入字典。
4.  **保存结果**:
    *   获取当前游戏的导出根目录 (`Output/Exports/{ProjectName}`).
    *   将映射表序列化为 `ShaderMappings.json` 并保存到该目录。

### 3.3 输出格式
`ShaderMappings.json`:
```json
{
  "Game/Unreal/Materials/M_Hero": [
    "HASH_STRING_1...",
    "HASH_STRING_2..."
  ]
}
```

---

## 4. 使用指南

1.  **编译**: 构建 `Ruri.FModelHook` 项目。
2.  **启动**: 运行生成的 `.exe` (会自动启动 FModel)。
3.  **加载**: 在 FModel 中加载目标游戏的 IoStore 包 (`.utoc`).
4.  **导出**: 找到 Shader Library 资产（通常在 `Engine/Content` 下，如 `GlobalShaderCache-PCD3D_SM5`），右键点击 **Export**。
5.  **验证**: 检查你的游戏导出目录 (e.g. `Output/Exports/MyGame/`)，应能看到 `ShaderMappings.json`。

---

## 5. 参考资料

| 关键类/文件 | 作用 |
|------|------|
| `UE_ShaderDecompiler_Hook.cs` | 核心 Hook 逻辑，控制导出流程 |
| `CUE4ParseViewModel.cs` | FModel 的主视图模型，提供 `Provider` (VFS) 和 `ProjectName` |
| `IoStoreReader.cs` (CUE4Parse) | 解析 `.utoc` 文件，提供 `ContainerHeader` |
| `FFilePackageStoreEntry.cs` | 数据结构，包含 `ShaderMapHashes` |
| `ShaderArchiveExporter.cs` | 处理 `.ushaderlib` 的二进制导出 |

---

## 6. 材质参数符号解析 (Material Parameter Symbol Resolution)

> **基于 UE 5.4.4 源码分析**
> 源文件位置: `UnrealEngine-5.4.4-release/Engine/Source/Runtime/Engine/`

### 6.1 EMaterialParameterType 枚举

**源文件**: `Public/MaterialTypes.h:186-204`

```cpp
enum class EMaterialParameterType : uint8
{
    Scalar = 0u,              // float
    Vector,                   // FLinearColor
    DoubleVector,             // FVector4d (LWC)
    Texture,                  // UTexture*
    Font,                     // UFont* + int32 FontPage
    RuntimeVirtualTexture,    // URuntimeVirtualTexture*
    SparseVolumeTexture,      // USparseVolumeTexture*
    StaticSwitch,             // bool (静态参数,编译时)

    NumRuntime,               // 运行时参数类型上限

    StaticComponentMask = NumRuntime, // bool R/G/B/A (静态参数,编译时)

    Num,
    None = 0xff,
};
```

### 6.2 参数存储数组

**源文件**: `Classes/Materials/MaterialInstance.h:617-643`

| 参数类型 | 存储数组 | 值类型结构 |
|---------|---------|-----------|
| Scalar | `ScalarParameterValues` | `FScalarParameterValue` |
| Vector | `VectorParameterValues` | `FVectorParameterValue` |
| DoubleVector | `DoubleVectorParameterValues` | `FDoubleVectorParameterValue` |
| Texture | `TextureParameterValues` | `FTextureParameterValue` |
| RuntimeVirtualTexture | `RuntimeVirtualTextureParameterValues` | `FRuntimeVirtualTextureParameterValue` |
| SparseVolumeTexture | `SparseVolumeTextureParameterValues` | `FSparseVolumeTextureParameterValue` |
| Font | `FontParameterValues` | `FFontParameterValue` |

### 6.3 动态材质实例 API (UMaterialInstanceDynamic)

**源文件**: `Classes/Materials/MaterialInstanceDynamic.h`

#### 设置参数值

```cpp
// Scalar (float) - Line 22
void SetScalarParameterValue(FName ParameterName, float Value);

// Vector (FLinearColor/FVector/FVector4) - Line 91
void SetVectorParameterValue(FName ParameterName, FLinearColor Value);

// DoubleVector (FVector4d) - Line 99
void SetDoubleVectorParameterValue(FName ParameterName, FVector4 Value);

// Texture (UTexture*) - Line 63
void SetTextureParameterValue(FName ParameterName, class UTexture* Value);

// RuntimeVirtualTexture - Line 71
void SetRuntimeVirtualTextureParameterValue(FName ParameterName, class URuntimeVirtualTexture* Value);

// SparseVolumeTexture - Line 79
void SetSparseVolumeTextureParameterValue(FName ParameterName, class USparseVolumeTexture* Value);

// Font - Line 166
void SetFontParameterValue(const FMaterialParameterInfo& ParameterInfo, class UFont* FontValue, int32 FontPage);
```

#### 获取参数值

```cpp
float K2_GetScalarParameterValue(FName ParameterName);
FLinearColor K2_GetVectorParameterValue(FName ParameterName);
class UTexture* K2_GetTextureParameterValue(FName ParameterName);
```

### 6.4 参数值结构体

**源文件**: `Classes/Materials/MaterialInstance.h:70-436`

#### FScalarParameterValue (Line 70-128)
```cpp
struct FScalarParameterValue
{
    FMaterialParameterInfo ParameterInfo;
    float ParameterValue;
    FGuid ExpressionGUID;
};
```

#### FVectorParameterValue (Line 132-181)
```cpp
struct FVectorParameterValue
{
    FMaterialParameterInfo ParameterInfo;
    FLinearColor ParameterValue;
    FGuid ExpressionGUID;
};
```

#### FDoubleVectorParameterValue (Line 185-230)
```cpp
struct FDoubleVectorParameterValue
{
    FMaterialParameterInfo ParameterInfo;
    FVector4d ParameterValue;
    FGuid ExpressionGUID;
};
```

#### FTextureParameterValue (Line 234-283)
```cpp
struct FTextureParameterValue
{
    FMaterialParameterInfo ParameterInfo;
    TObjectPtr<class UTexture> ParameterValue;
    FGuid ExpressionGUID;
};
```

#### FRuntimeVirtualTextureParameterValue (Line 287-331)
```cpp
struct FRuntimeVirtualTextureParameterValue
{
    FMaterialParameterInfo ParameterInfo;
    TObjectPtr<class URuntimeVirtualTexture> ParameterValue;
    FGuid ExpressionGUID;
};
```

#### FSparseVolumeTextureParameterValue (Line 335-379)
```cpp
struct FSparseVolumeTextureParameterValue
{
    FMaterialParameterInfo ParameterInfo;
    TObjectPtr<class USparseVolumeTexture> ParameterValue;
    FGuid ExpressionGUID;
};
```

#### FFontParameterValue (Line 383-436)
```cpp
struct FFontParameterValue
{
    FMaterialParameterInfo ParameterInfo;
    TObjectPtr<class UFont> FontValue;
    int32 FontPage;
    FGuid ExpressionGUID;
};
```

### 6.5 参数信息结构 (FMaterialParameterInfo)

**源文件**: `Public/MaterialTypes.h:29-94`

```cpp
struct FMaterialParameterInfo
{
    FName Name;
    TEnumAsByte<EMaterialParameterAssociation> Association; // Layer/Blend/Global
    int32 Index; // Layer or Blend index, INDEX_NONE for global
};
```

### 6.6 本项目实现

已创建以下文件实现完整的参数解析:

| 文件 | 功能 |
|------|------|
| `Unreal/MaterialParameterInfo.cs` | 所有参数类型定义，匹配 UE 结构 |
| `Unreal/MaterialParameterExtractor.cs` | 从 FModel JSON 导出解析所有参数 |

#### 使用示例

```csharp
// 从材质 JSON 提取所有参数
var collection = MaterialParameterExtractor.ExtractFromJson("M_Hero.json");

// 获取所有参数绑定 (用于符号注入)
var bindings = collection.GetAllParameterBindings();
foreach (var (name, (type, slot)) in bindings)
{
    Console.WriteLine($"{name}: {type} @ slot {slot}");
}

// 输出示例:
// Roughness: Scalar @ slot 0
// Color: Vector @ slot 1
// Albedo: Texture @ slot 2
```

---

## 7. 纹理槽位命名与映射 (Texture Slot Naming Convention)

> **关键发现**: 基于 UE 5.4.4 源码分析
> 源文件: `MaterialUniformExpressions.cpp:416-437`

### 7.1 UE 纹理绑定命名规则

UE 编译材质时，为所有纹理参数生成**固定模式**的绑定名称：

```cpp
// MaterialUniformExpressions.cpp
for (int32 i = 0; i < 128; ++i)
{
    Texture2DNames[i] = FString::Printf(TEXT("Texture2D_%d"), i);
    Texture2DSamplerNames[i] = FString::Printf(TEXT("Texture2D_%dSampler"), i);
    TextureCubeNames[i] = FString::Printf(TEXT("TextureCube_%d"), i);
    TextureCubeSamplerNames[i] = FString::Printf(TEXT("TextureCube_%dSampler"), i);
    Texture2DArrayNames[i] = FString::Printf(TEXT("Texture2DArray_%d"), i);
    VolumeTextureNames[i] = FString::Printf(TEXT("VolumeTexture_%d"), i);
    ExternalTextureNames[i] = FString::Printf(TEXT("ExternalTexture_%d"), i);
    VirtualTexturePhysicalNames[i] = FString::Printf(TEXT("VirtualTexturePhysical_%d"), i);
    SparseVolumeTexturePageTableNames[i] = FString::Printf(TEXT("SparseVolumeTexturePageTable_%d"), i);
    // ...
}
```

### 7.2 Shader 中的绑定名称

反编译后的 HLSL 中会看到如下声明：

```hlsl
// 2D 纹理 (Standard2D)
Texture2D<float4> Material_Texture2D_0 : register(t0);
SamplerState Material_Texture2D_0Sampler : register(s0);

Texture2D<float4> Material_Texture2D_1 : register(t1);
SamplerState Material_Texture2D_1Sampler : register(s1);

// Cube 纹理
TextureCube<float4> Material_TextureCube_0 : register(t2);
SamplerState Material_TextureCube_0Sampler : register(s2);

// ...
```

### 7.3 映射恢复机制

| Shader 绑定名 | FUniformExpressionSet 数组 | 参数名来源 |
|--------------|---------------------------|-----------|
| `Material.Texture2D_0` | `UniformTextureParameters[Standard2D][0]` | `.ParameterInfo.Name` |
| `Material.Texture2D_1` | `UniformTextureParameters[Standard2D][1]` | `.ParameterInfo.Name` |
| `Material.TextureCube_0` | `UniformTextureParameters[Cube][0]` | `.ParameterInfo.Name` |
| `Material.VolumeTexture_0` | `UniformTextureParameters[Volume][0]` | `.ParameterInfo.Name` |
| `Material.VirtualTexturePhysical_0` | `UniformTextureParameters[Virtual][0]` | `.ParameterInfo.Name` |

### 7.4 局限性

`FUniformExpressionSet` 存储在编译后的 `FMaterialShaderMap` 中，**不在材质资产的 JSON 导出中**。

目前采用的**近似映射**策略：
- 假设 `TextureParameterValues` 的顺序与 `UniformTextureParameters[Standard2D]` 一致
- 这在大多数情况下有效，但复杂材质可能存在差异

### 7.5 本项目实现

新增 `TextureParameterMapper.cs` 实现映射与注释生成：

```csharp
// 构建近似映射
var mapping = TextureParameterMapper.BuildApproximateMapping(parameters);
// mapping["Material.Texture2D_0"] = "BaseColorTexture"

// 生成 HLSL 注释
var annotations = TextureParameterMapper.GenerateParameterAnnotations(parameters);
```

#### 输出示例

```hlsl
//==============================================================================
// Material Parameter Mapping (UE5 Shader Decompiler)
// WARNING: This mapping is approximate. Actual slot order may differ.
//==============================================================================

// Texture2D Parameters:
//   Material.Texture2D_0 → "BaseColorTexture" = /Game/Textures/T_Hero_BaseColor
//   Material.Texture2D_1 → "NormalMapTexture" = /Game/Textures/T_Hero_Normal

// Scalar Parameters:
//   [0] "Roughness" = 0.5
//   [1] "Metallic" = 1.0

// Vector Parameters:
//   [0] "TintColor" = (1.0, 0.8, 0.6, 1.0)

Texture2D<float4> Material_Texture2D_0 : register(t0);
SamplerState Material_Texture2D_0Sampler : register(s0);
// ...
```

---

## 8. 精确参数映射实现 (Precise Parameter Mapping)

> **方案已实现** - 无硬编码，无近似

### 8.1 数据源

CUE4Parse 已完整解析 `FUniformExpressionSet`：

```
UMaterial.LoadedMaterialResources[].LoadedShaderMap.Content
  └── MaterialCompilationOutput.UniformExpressionSet
        ├── UniformTextureParameters[7][]  ← 精确的纹理参数映射
        │     └── ParameterInfo.Name       ← 参数名
        │     └── TextureIndex             ← 纹理索引
        └── UniformNumericParameters[]     ← 数值参数
```

### 8.2 实现文件

| 文件 | 功能 |
|-----|------|
| `Ruri.FModelHook/.../UniformExpressionExporter.cs` | 从材质导出 UniformExpressionSet 到 JSON |
| `Ruri.ShaderDecompiler/.../PreciseParameterMapper.cs` | 解析映射 JSON，提供精确查询 |

### 8.3 导出格式

`MaterialParameterMappings.json`:
```json
{
  "Materials": {
    "/Game/Characters/M_Hero": {
      "ShaderMapHash": "ABC123...",
      "TextureParameters": {
        "Standard2D": [
          {"Index": 0, "Name": "BaseColorTexture", "TextureIndex": 0},
          {"Index": 1, "Name": "NormalMapTexture", "TextureIndex": 1}
        ],
        "Cube": [],
        "Volume": []
      },
      "NumericParameters": [
        {"Name": "Roughness", "Type": "Scalar", "DefaultValue": "0.5"},
        {"Name": "TintColor", "Type": "Vector", "DefaultValue": "(1.0, 0.8, 0.6, 1.0)"}
      ]
    }
  }
}
```

### 8.4 使用方法

```csharp
// 加载映射
var mapper = PreciseParameterMapper.LoadFromFile("MaterialParameterMappings.json");

// 获取材质映射
var mapping = mapper.GetByMaterialPath("/Game/Characters/M_Hero");

// 解析 shader 绑定名
string paramName = mapper.ResolveBindingName(mapping, "Material.Texture2D_0");
// paramName = "BaseColorTexture"

// 生成 HLSL 注释头
string header = mapper.GenerateHlslHeader(mapping);

// 构建完整映射表
var bindingMap = mapper.BuildBindingToNameMap(mapping);
// bindingMap["Material_Texture2D_0"] = "BaseColorTexture"
```

### 8.5 注意事项

- 需要在 FModel Provider 中启用 `ReadShaderMaps = true`
- 材质必须加载后 `LoadedMaterialResources` 才会填充
- ShaderMapHash 可用于匹配 shader 和材质

