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
