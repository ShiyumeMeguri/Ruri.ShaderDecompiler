# UE5 Shader-Material Mapping 研究笔记

## 目标
从打包后的UE5游戏数据中恢复 **材质名 → Shader** 的映射关系，用于给反编译后的shader命名。

---

## 🎉 核心发现：材质与Shader的关联机制

### 关联数据存储位置
**`.utoc` 文件的 IoStore 容器头里！**

```cpp
// IoContainerHeader.h Line 43-47
struct FFilePackageStoreEntry
{
    TFilePackageStoreEntryCArrayView<FPackageId> ImportedPackages;
    TFilePackageStoreEntryCArrayView<FSHAHash> ShaderMapHashes;  // ← 材质→Shader映射！
};
```

每个材质包在`.utoc`里都有对应的`FFilePackageStoreEntry`，其中`ShaderMapHashes`数组直接指向`ShaderArchive.json`里的`ShaderMapHashes`！

### 运行时加载流程
```
加载材质uasset
    ↓
从IoStore获取PackageStoreEntry.ShaderMapHashes
    ↓
FCoreDelegates::PreloadPackageShaderMaps.ExecuteIfBound(Data.ShaderMapHashes, ...)
    ↓
在ShaderCodeLibrary用hash查找shader
    ↓
注册到GIdToMaterialShaderMap[Platform]
```

参考代码：`AsyncLoading2.cpp` Line 5400-5422

---

## ✅ CUE4Parse已支持！

**CUE4Parse已经能解析 `FFilePackageStoreEntry.ShaderMapHashes`！**

文件：`CUE4Parse\UE4\IO\Objects\FFilePackageStoreEntry.cs`

```csharp
public class FFilePackageStoreEntry
{
    public int ExportCount;
    public int ExportBundleCount;
    public FPackageId[] ImportedPackages;
    public FSHAHash[] ShaderMapHashes;  // ← 已解析！

    public FFilePackageStoreEntry(FArchive Ar, EIoContainerHeaderVersion version)
    {
        // ...
        ImportedPackages = ReadCArrayView<FPackageId>(Ar);
        ShaderMapHashes = ReadCArrayView(Ar, () => new FSHAHash(Ar));
    }
}
```

---

## 实现方案

1. 通过FModel/CUE4Parse加载`.utoc`
2. 获取每个材质包的`FFilePackageStoreEntry`
3. 读取`ShaderMapHashes`数组
4. 在`ShaderArchive.json`的`ShaderMapHashes`中查找匹配index
5. 建立`PackageName → ShaderIndex`映射表

### Pseudocode
```csharp
var mapping = new Dictionary<string, List<int>>();

// 遍历IoStore容器头
foreach (var (packageId, storeEntry) in utocHeader.Entries)
{
    if (storeEntry.ShaderMapHashes.Length == 0) continue;
    
    var packageName = ResolvePackageName(packageId);
    var shaderIndices = new List<int>();
    
    foreach (var hash in storeEntry.ShaderMapHashes)
    {
        int idx = Array.IndexOf(shaderArchive.ShaderMapHashes, hash);
        if (idx >= 0) shaderIndices.Add(idx);
    }
    
    if (shaderIndices.Count > 0)
        mapping[packageName] = shaderIndices;
}
```

---

## 之前尝试失败的原因

### 尝试：从材质JSON重建Hash
**失败**：`FMaterialShaderMapId::GetMaterialHash`的输入参数大部分是`#if WITH_EDITOR`，打包后不存在。

### 尝试：读取材质内联ShaderMap
**失败**：UE5使用全局shader库，`LoadedMaterialResources`为空。

---

## 关键数据结构总结

| 文件 | 结构 | 内容 |
|------|------|------|
| `.utoc` | `FIoContainerHeader.StoreEntries` | 所有包的`FFilePackageStoreEntry` |
| `.utoc` | `FFilePackageStoreEntry.ShaderMapHashes` | **材质→Shader的Hash映射** |
| `.ushaderbytecode` | `FSerializedShaderArchive.ShaderMapHashes` | ShaderMap的Hash数组 |
| `.ushaderbytecode` | `FSerializedShaderArchive.ShaderMapEntries` | 每个ShaderMap引用哪些Shader |

---

## UE源码参考

| 文件 | 内容 |
|------|------|
| `Core/Public/IO/IoContainerHeader.h:43-47` | `FFilePackageStoreEntry`定义 |
| `CoreUObject/Private/Serialization/AsyncLoading2.cpp:5400-5422` | 加载shader的调用点 |
| `RenderCore/Public/ShaderCodeArchive.h` | `FSerializedShaderArchive`定义 |
| `Engine/Private/Materials/MaterialShader.cpp` | `GIdToMaterialShaderMap`全局映射 |

## CUE4Parse参考

| 文件 | 内容 |
|------|------|
| `CUE4Parse\UE4\IO\Objects\FFilePackageStoreEntry.cs` | utoc包Entry解析 |
| `CUE4Parse\UE4\Shaders\FSerializedShaderArchive.cs` | ShaderArchive解析 |

---

*最后更新: 2026-02-02*
