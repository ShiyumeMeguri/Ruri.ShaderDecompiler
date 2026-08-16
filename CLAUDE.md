# Ruri.ShaderDecompiler — 当前任务、问题、目标

> 单一事实来源。`Source/Ruri.ShaderDecompiler/` 下的 `README.md` /
> `CURRENT_LIMITATIONS.md` / `SHADER_MAPPING_RESEARCH.md` 已合并至本
> 文件。后续修改只更新这里。
>
> **SPIR-V 阶段调试方法论 → [SPIRV_DEBUG_PLAYBOOK.md](SPIRV_DEBUG_PLAYBOOK.md)**。
> 反编译失败类 bug 先过一遍这份 playbook。
>
> **UE 符号来源矩阵(closed-world + 外部 metadata 方案)→
> [UE_SYMBOL_SOURCES.md](UE_SYMBOL_SOURCES.md)**。覆盖 UE 5.1.1 + 5.4.4
> 双引擎版本、optional block 持久化裁定、Material UB 重放、
> `ResourceTableLayoutHash` 外部 metadata key、UE 5.4 SM6 反射数据残留路径。

---

## 0. 项目路径(本机)

| Item | Path |
| --- | --- |
| 仓库根 | `D:\Ruri\Github\FractalTools\Ruri-RipperHook` |
| 反编译器源码 | `Source\Ruri.ShaderDecompiler\` |
| FModel 钩子源码 | `Source\Ruri.FModelHook\` |
| AssetRipper 钩子(Unity 端,参考) | `Source\Ruri.RipperHook\` |
| 反编译器二进制 | `Source\Ruri.ShaderDecompiler\bin\Debug\Ruri.ShaderDecompiler.exe` |
| 无头导出 CLI(唯一入口) | `Source\Ruri.FModelHook.CLI\` → `Ruri.FModelHook.CLI.exe --game-config <AppSettings.json>` |
| UE 5.1 源码 | `D:\GameStudy\UnrealEngine-5.1.1-release` |

---

## 1. 当前状态 / 待办

Material UB(贴图 + CB + 用户面命名)、Unity UB、动态数组、matrix 数组、
cbuffer 16-byte 对齐、LWC 类型、tess/geom GLSL 回退,均已闭合(见 §9 历史)。

剩余待办:

1. **EndField blob1 `ShaderVariablesGlobal` 6→5 layout 缩水** — blob2
   同 metadata 是 6 个,差异疑似 `BuildStructuredLayout` 用了
   `flatBuffer.ArrayLength` 而 vertex shader 的 SPIR-V flat 数组比
   fragment 短,导致 `maxAvailableByteOffset` 把 byte 1728 排除。
2. **EndField blob1 `UnityInstancing_SRP_UnityPerDraw` rewrite 仍失败** —
   `unsupported access translation ... slotConst=1 slotDynamic=248`。需确认
   历史 baseline 是否从未真正成功过(HLSL 退化成单数组被误认为"对")。
3. **Multi-field preshader 未支持** — `NumFields > 1` 的 struct 输出,
   reader 目前跳过。Material CB 少见,其它 UB 可能用到。

---

## 2. 文件分工

### 2.1 本任务可改动

- `Spirv/StructuredCBufferRewriter.cs` —— 核心:SPIR-V 中 CB 成员拆分/命名注入。
- `Unreal/UeShaderSymbolInputsReader.cs` —— metadata `VectorParams` /
  `CBParams` / `AllNumericParams` 填充来源。
- `Unreal/UeShaderSymbolBuilder.cs` —— UE 侧 symbol 合流入口。
- `Unreal/MaterialUniformBufferLayout.cs`、`UeShaderResourceTableDecoder.cs`、
  `UeShaderResourceTableSymbolizer.cs`、`UeMaterialTextureNameInferrer.cs`
  —— UB layout 重放、SRT 解码、贴图名字反推。

### 2.2 不许动

- `Source/Ruri.RipperHook/...` —— Unity 侧参考实现,不动它的 metadata 导出格式。
- `FModel/CUE4Parse/...` —— 第三方 fork,只读不改。

### 2.3 项目硬规则

- **绝不硬编码引擎 UB 成员表** —— 不同引擎版本会偏移,硬编码会静默捏造
  名字。所有名字必须能追到 cooked 数据中真实存在的字节,或追到外部
  metadata 文件且其 `layoutHash` 与 cook 中的 `ResourceTableLayoutHashes[i]`
  严格匹配。
- 加新名字来源前,先在 [UE_SYMBOL_SOURCES.md](UE_SYMBOL_SOURCES.md) 矩阵里
  加一条带源码引用的条目;查不到就留 anonymous,不许猜。
- placeholder 一定带 `_SRV`/`_Sampler`/`_UAV`/`_Resource` 中缀,一眼看出
  "未完全恢复"。
- 引擎 UB 名字通过 `<exeDir>/EngineUbMetadata/<UBName>_<Hash:08x>_MetaData.json`
  外部文件供给;不存在时降级 placeholder;hash 不匹配时不命中。详见
  [UE_SYMBOL_SOURCES.md](UE_SYMBOL_SOURCES.md) §6。

---

## 3. 符号可恢复性(closed-world 矩阵)

> 判据来自 UE 5.1.1 源码逐行核对(D3D shipping cook, `STRIP_REFLECTION_DATA`
> 默认开)。矩阵和源码引用详见 [UE_SYMBOL_SOURCES.md](UE_SYMBOL_SOURCES.md);
> 这里只列结论。

### 3.1 可恢复

- UB 名字(`'u'` optional block,D3D 路径无条件写出)。
- Material 资源 typed flat 名(`CreateBufferStruct()` 重放,需 `.uasset`)。
- 材质数值/贴图参数用户名(`FUniformExpressionSet.Uniform*Parameters[].ParameterInfo.Name`)。
- Bind-point ↔ UB-resource-index(SRT token streams)。
- Material CB 数值成员的 preshader byte offset(`UniformPreshaderFields[i].BufferOffset/Type`
  是绝对权威,无论 opcode 多复杂)。

### 3.2 不可恢复(closed-world 上限)

- 引擎 UB 成员名(`View`/`OpaqueBasePass` 等,C++ 宏展开,只在引擎二进制里)
  → placeholder `<UB>_SRV<i>` / `<UB>_Sampler<i>` / `<UB>_UAV<i>`。
- 不依赖 UB 的 `FShaderParameter*` 散绑资源 —— frozen 后只剩
  `(ByteOffset, BaseIndex, BaseType)`,无名字 → `T<n>` / `sampler_<n>`。
- `Material_Texture2D_0` 编译期 reflection 名(RDEF strip 时丢失,只能从
  `.uasset` 重建 typed 名而非原编译期名)。
- Shader 源文件名 / entry-point / vertex factory 字符串(未序列化或 hash-only)。

### 3.3 Anti-patterns(违规)

- 硬编码引擎 UB 成员表。
- 用 register class + index 猜成员名。
- placeholder 不带 infix,让人误以为是真名。

---

## 4. Pipeline(端到端)

### 4.1 UE 端导出

无头模式(唯一入口,无 WPF):
```
Ruri.FModelHook.CLI.exe --game-config <AppSettings(_Debug).json>
    [--archive-filter <名字子串,逗号分隔>] [--skip-global]
    [--split-variants | --no-split-variants] [--export-only]
```
从 `--game-config` 读 AES 动态键 + mappings + EGame 版本,挂 CUE4Parse
provider,跑完整 export + decompile。产出 `UnifiedShaderMetadata.json` +
每个 library 的 `.assetinfo.json` / `.ushaderlib` / `.ushaderbytecode`。

### 4.2 离线反编译

```
Ruri.ShaderDecompiler.exe <lib.ushaderlib> <outDir> --mapping <UnifiedShaderMetadata.json>
```

每 shader 流程:
1. 解析 SRT(`UnrealShaderParser` + `UeShaderResourceTableDecoder`)。
2. `UnifiedShaderMetadataResolver` 找到 shader 所属材质的 `UniformExpressionSet`。
3. `UeShaderSymbolInputsReader` 解析材质数据为 `ShaderSymbolData`。
4. `UeShaderResourceTableSymbolizer` 把 SRT 解码的绑定信息翻成命名 binding。
5. `UeShaderSymbolBuilder` 合流为最终输入。
6. 核心 pipeline:`dxbc/dxil → dxil-spirv → ScalarCbufferVectorizer →
   SpirvPatcher` 注入符号 → `StructuredCBufferRewriter` 重写 cbuffer →
   `spirv-cross` → HLSL。全程 in-process,无 exe 调用、无落盘中间文件:
   - `dxil-spirv` → `Utils/DxilSpirvNative.cs`(P/Invoke,内建 SRV/UAV
     remapper,DXBC/SM5 直接喂 dxil-spirv,不需要额外转换步骤)。
   - `spirv-cross` → `Utils/SpirvCrossNative.cs`(P/Invoke)。
   - native 库全来自 NuGet 还原,`Utils/NativeToolsLoader.cs` 按路径加载。
   - `Spirv/ScalarCbufferVectorizer.cs` 是 DXBC 直喂路径的关键:
     dxbc-spirv 把 cbuffer 出成 scalar `float[4N]`,spirv-cross HLSL 后端
     拒绝这种布局;vectorizer 在 rewriter 前归一成 `float4[N]`,access
     chain 索引同步改写(常量折叠或插入 shr/and)。
   - 热路径 Span/pin/stackalloc/`delegate* unmanaged`,0-GC。
   - ⚠ 改 csproj 的 PackageReference 后必须 clean rebuild(删 obj+bin),
     否则表现为 `dxil_spv_parse_dxil_blob failed (-4)`。

---

## 5. 验证 — 看一个 HLSL 是否变好了

```bash
DIR=".../Decompiled/<lib>"
ls "$DIR"/*.hlsl | wc -l                                    # total
grep -l "_RegisterSpace" "$DIR"/*.hlsl 2>/dev/null | wc -l  # ↓ better
grep -l "Material_"      "$DIR"/*.hlsl 2>/dev/null | wc -l  # ↑ better
grep -l "View_"          "$DIR"/*.hlsl 2>/dev/null | wc -l  # ↑ better
```

回归 fixture(必须不退化,清单见 `Test/UnityBinary/{EndField,Ruri}/`):
- EndField `litpoly` blob1/blob2(vertex/fragment,动态索引 + 空洞 packoffset)。
- Ruri `ClusterDeferred`(`$Globals`/`LightShadows`/`urp_*`/`AdditionalLights`)。
- Ruri `TextMeshPro`(23 命名成员 + 多处空洞)。
- UE `M_Bamboo_tree_PS_1904` / `M_Alpha_grass_PS_4000`(Material UB 全链路)。

跑单 shader:`Ruri.ShaderDecompiler.exe <lib> <outDir> --mapping <meta.json> --shader-index <N>`。

---

## 7. Loop discipline

- 每轮 ONE focused improvement,别 refactor。
- 每轮跑 §5 验证,回归 fixture 不许退化。
- 失败时把失败摘要记到本文件 §1,转方向,不要静默扩大范围。
- 不动 Unity 端导出器或别的游戏 hook。
- 破坏性操作(改 SPIR-V patcher、删导出、force-push)必须先问。

---

## 8. README(项目概览)

**Ruri.ShaderDecompiler** 是一个通用的 Shader 反编译库,将编译后的 Shader
二进制还原为高可读性的 HLSL 代码。核心目标是解决反编译中变量名丢失的问题,
通过跨引擎通用方案重建符号信息与字节码逻辑之间的关联。

### 核心原理

1. Shader 二进制没有销毁语义信息 —— DXBC/DXIL/SPIR-V 只是为性能移除了
   符号信息,留下寄存器与槽位绑定。
2. 引擎运行时必然保留符号映射(CPU 侧按变量名设置参数)。
3. 本库不依赖猜测或模式匹配:解析引擎侧元数据(Unity Bindings / UE
   SRT)→ 把符号信息重新注入 SPIR-V → 重建可读的高级 Shader 代码。

### 当前特性

- 统一中间层(SPIR-V),DXBC/DXIL/SPIR-V 输入都转到 SPIR-V 再处理。
- Unity 端由 AssetRipper 解析并填充 metadata,完美还原符号反编译。
- UE 端由 CUE4Parse 解析并填充 metadata,受 closed-world 上限约束(§3)。

### Roadmap

- [ ] Unity 直接生成 ShaderLab(不只是 per-pass HLSL)。
- [ ] 统一 UE / Unity 反编译输出为 ShaderLab。
- [ ] SPIR-V → spirv-cross HLSL → 重新编译到 DXBC 优化指令数 → 重新反编译
  为更可读 HLSL。
