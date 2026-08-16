# SPIR-V Rewriter — Debug & Fix Playbook

> **目标读者**: 换上下文/换人后,只看这一份能从零接手 SPIR-V 阶段的 bug 修复。
>
> 不讲项目背景,只讲"shader 反编译失败 → 定位 → 修复"的具体方法论。
> 项目背景在 [CLAUDE.md](CLAUDE.md)。当前的存量 bug 与历史迭代见
> [CLAUDE.md §9](CLAUDE.md)。

---

## 0. TL;DR 三条铁律

1. **永远先看 spirv-cross 的真实 stderr,再去推断 rewriter 哪里错。**
   异常 "SPIR-V emission failed after patch" 是**包装层**信息,真正的原因
   在 `Run()` 把 spirv-cross 子进程的 stderr 写到 `Console.Error` 那一行
   (例如 `Cannot subdivide a scalar value!` 或 `Unsupported builtin in HLSL: N`)。
2. **永远用 `spirv-dis` 看 patched 后的模块,而不是猜。**
   rewriter 改了什么、留下什么死指令、result type 和实际访问深度对不对,
   汇编一眼能看出来。
3. **Use-count 推断"指令死活"会被字面量污染。** SPIR-V 里很多 op 的操作
   数槽是字面量(byte 偏移、扩展指令编号、组件索引……),数值上和真实
   SSA id 撞车时通用计数器会失效 —— 用**结构性判定**(谁引用谁)代替。

---

## 1. 反编译管线一览(失败定位时心里要有的图)

```
.dxbc / .dxil  (内存字节,无落盘)
   │  DxilSpirvNative.Convert            (Utils/DxilSpirvNative.cs — dxil-spirv-c-shared.dll P/Invoke)
   │      DXBC(SM5) 由捆绑的 dxbc-spirv 直译;DXIL 走常规路。无 dxbc2dxil / dxilconv。
   ▼
.spv  (raw — 未打名字、未拆 cbuffer)
   │  ScalarCbufferVectorizer.Vectorize  (Spirv/ScalarCbufferVectorizer.cs)
   │      - dxbc-spirv 出的 scalar cbuffer float[4N] → float4[N](否则 spirv-cross HLSL 拒,见 §5.G)
   │      - DXIL 路本就 float4 → no-op
   │  StructuredCBufferRewriter.Rewrite   (Spirv/StructuredCBufferRewriter.cs)
   │      - 把 cb._m0[N] 重写为命名结构成员访问
   │  SpirvPatcher.PatchByIds             (Spirv/SpirvPatcher.cs)
   │      - 注入 OpName / OpMemberName
   ▼
.spv  (patched)
   │  SpirvCrossNative.EmitHlsl           (Utils/SpirvCrossNative.cs — spirv-cross.dll spvc_* P/Invoke)
   │      └ tess/geom/RT builtin 失败时回退 GLSL(--vulkan-semantics)
   ▼
.hlsl 或 .glsl  (string,无落盘)
```

每一步都可能失败,且失败信号不同:
- `dxbc2dxil` / `dxil-spirv` 报错 → 输入二进制本身有问题。极少见。
- `Rewrite()` 抛异常 → rewriter 内部逻辑错误,看 stack trace。
- `Rewrite()` 不抛但 spirv-cross 拒掉 → **本 playbook 的主战场**。
- `spirv-cross` 报"Unsupported builtin in HLSL: N" → 是 spirv-cross 的硬限制
  (HS/DS 用的 `InvocationId`/`TessCoord` 等),不是 rewriter 错;
  Decompile 会自动 `--vulkan-glsl` 回退,产物落到 `SourceCode`。

---

## 2. 工具箱

### 2.1 native(全部 NuGet,in-process,无 exe / 无 Tools/)

反编译三件套已全部 in-process 化,无独立 exe、无落盘、无 `Tools/` 目录:

| 库 | 来源 | 用途 |
| --- | --- | --- |
| `dxil-spirv-c-shared.dll` | NuGet `AssetRipper.Bindings.DxilSpirV` | DXBC(dxbc-spirv 直译)+ DXIL → SPIR-V |
| `spirv-cross.dll` | NuGet `Silk.NET.SPIRV.Cross.Native` | SPIR-V → HLSL/GLSL |

⚠ 没有 `spirv-dis.exe` 了 —— 调试 patched/raw SPV 用 §6 的 Python 裸字节读法。
⚠ 想拿"未经 rewriter 改动"的对照 SPV(原 §3 的手工三步),改用 `--debug-dump <dir>`,它落
`.01.pre-rewrite.spv`(= dxil-spirv 输出,已含 vectorizer 归一)/ `.02.post-rewrite` / `.03.post-patch`。

### 2.2 反编译器自身的诊断产物

`Decompile()` 在失败时也会 attach `IntermediateSpirv` 与 `LastRewriteSummary`
(见 `ShaderDecompiler.cs:Decompile()` 的 catch 分支)。`Program.cs` 的
unity-binary session 会把这些写到输出目录:

```
<输出目录>/
  unitybinary.error.txt   ← 异常堆栈 + patch plan + builtin decorations
  unitybinary.spv         ← patched SPV(失败时也保留)— 关键
  unitybinary.rewrite.txt ← rewriter 每个 CB 的成功/失败摘要
```

**第一时间永远先看 `unitybinary.spv`,用 spirv-dis 反汇编出来对比。**

### 2.3 仓库里现成的回归 fixture

每次改 SPIR-V 阶段必须保证这些不退化:

| Fixture | 关键考点 |
| --- | --- |
| `Test/UnityBinary/EndField/litpoly.shader.sub0.pass0.blob1.HGBuffer.dxbc.bin` (vertex) | 部分命名 + 动态索引 `cb[tmp+5]` 数组 |
| `…blob2.HGBuffer.dxbc.bin` (fragment) | `UnityPerMaterial` 带 `c0.z`/`c0.w`/`c8` 等空洞 packoffset |
| `Test/UnityBinary/Ruri/Hidden_Ruri Render Pipeline_ClusterDeferred…blob27.dxbc.bin` | `AdditionalLights` 4 个并列 256-长 float4 数组,动态索引 + `+const` 偏移 |
| `Test/UnityBinary/Ruri/TextMeshPro_Distance Field.shader.sub0.pass0.blob1..dxbc.bin` | `$Globals` 23 命名成员混 scalar/vec4/matrix/int + 多处空洞 |

---

## 3. 失败-到-修复工作流

### Step 1 — 抓真实错误信息

```bash
"…/Ruri.ShaderDecompiler.exe" "<failing fixture>.dxbc.bin" 2>&1 | grep -i "spirv-cross\|error\|UnityBinary"
```

`spirv-cross failed: SPIRV-Cross threw an exception: <message>` 这一行才是
**真正的根因**。剩下的 `InvalidOperationException: SPIR-V emission failed
after patch.` 只是包装。

常见错误信息和对应方向:

| spirv-cross 报错 | 含义 | 通常根因 |
| --- | --- | --- |
| `Cannot subdivide a scalar value!` | 某条 OpLoad 用了 ptr-scalar 但声明 v4float 类型,或访问链 result type 与实际下钻深度不一致 | rewriter 类型注入错位(Bug B/C/D 类) |
| `Unsupported builtin in HLSL: N` | spirv-cross HLSL 后端不支持 builtin N(8=InvocationId, 11=TessLevelOuter, 12=TessLevelInner, 13=TessCoord, 14=PatchVertices) | tess/geom 阶段固有限制,不是 rewriter 错。GLSL fallback 接住即可 |
| `Variable type cannot have any decoration` | OpDecorate / OpMemberDecorate 加在了 Variable 而不是它的类型 | patcher 错挂装饰 |
| `Module needs OpCapability X` | 缺 capability | patcher 引入了某个 op 但没添 capability,极少见 |

### Step 2 — 取出 patched SPV 反汇编

```bash
# 反编译失败后,unitybinary.spv 已经被 Program.cs 保留下来
"/c/Program Files/RenderDoc/plugins/spirv/spirv-dis.exe" \
    "<output_dir>/unitybinary.spv" > /tmp/patched.txt
```

### Step 3 — 同方法对原始 SPV 反汇编一份做对照

用同样三步手工跑通 dxbc → dxil → spv,得到**未经 rewriter 改动**的 SPV:

```bash
TOOLS=".../bin/Debug/Tools"
"$TOOLS/dxbc2dxil.exe" "<fixture>.dxbc.bin" -o /tmp/raw.dxil -emit-bc
"$TOOLS/dxil-spirv.exe" /tmp/raw.dxil --output /tmp/raw.spv --raw-llvm
"/c/Program Files/RenderDoc/plugins/spirv/spirv-dis.exe" /tmp/raw.spv > /tmp/raw.txt
```

`raw.txt` vs `patched.txt` diff 出来的就是 rewriter 干了什么、没干什么。

### Step 4 — 类型一致性快速扫描

```bash
# 列出所有 OpLoad 的目标类型(应当与喂它的 access chain 的 ptr-target 一致)
grep -oE "OpLoad %[A-Za-z_0-9]+" /tmp/patched.txt | sort | uniq -c | sort -rn

# 列出所有 OpAccessChain 的 result type 计数
grep -oE "OpAccessChain %[A-Za-z_0-9]+" /tmp/patched.txt | sort | uniq -c | sort -rn

# 关键检查: 能否找到 "AccessChain ptr-X 之后接 OpLoad %Y" 但 X != Y 的成对
grep -B1 "OpLoad %v4float" /tmp/patched.txt | head -40
grep -B1 "OpLoad %mat4v4float" /tmp/patched.txt | head -40
grep -B1 "OpLoad %float" /tmp/patched.txt | head -40
```

任何 `%_ptr_Uniform_float` 接 `OpLoad %v4float` 都是已确认的 bug 形态。

### Step 5 — 缩小到具体 result id

从错误推断或访问扫描定位嫌疑 SSA id(假设是 `%N`):

```bash
# 看 N 在 patched 中出现的所有位置
grep -nE "\b%N\b" /tmp/patched.txt

# 看 N 是哪类 op、操作数都是什么
grep -E "%N = " /tmp/patched.txt
```

如果 `%N` **没有任何用户**(只在自身定义那一行出现),那它是死指令,
应该被 cleanup pass NOP 掉 —— 没 NOP 是 bug。

如果 `%N` **有 N 用户但 result type 与下游 OpLoad 不匹配**,是注入层的
类型错位 —— 翻 `TranslateMemberAccess` / `TranslateDynamicArrayMemberAccess`
里对应的 `LogicalTypeKind` 分支。

### Step 6 — 字节级核查 raw SPV(必要时)

`spirv-dis` 默认会替换 OpName 描述给的友好名,导致同 id 不同名混淆。
直接用 Python 读 raw words 最干净:

```python
import struct
spv_path = r"D:\path\to\unitybinary.spv"
with open(spv_path, "rb") as f: data = f.read()
words = list(struct.unpack(f"{len(data)//4}I", data))
assert words[0] == 0x07230203
i = 5
op_names = {65:"OpAccessChain", 61:"OpLoad", 81:"OpCompositeExtract",
            128:"OpIAdd", 132:"OpIMul", 196:"OpShiftLeftLogical",
            43:"OpConstant", 46:"OpConstantNull",
            71:"OpDecorate", 72:"OpMemberDecorate",
            12:"OpExtInst", 245:"OpPhi", 109:"OpBitcast"}
target = {81, 416, 496}     # ids you care about
while i < len(words):
    w0 = words[i]; op = w0 & 0xFFFF; wc = w0 >> 16
    if wc == 0: break
    inst = words[i:i+wc]
    # for ops with TypeAndId result layout, result-id is at index 2
    if op in (43,46,59,61,65,66,128,130,132,196,81,109,124,12) and len(inst) >= 3:
        rid = inst[2]
        if rid in target:
            print(f"id={rid:>4} op={op}({op_names.get(op, '?')}) words={inst}")
    i += wc
```

这是 v8/v9 修复时多次救命的脚本 —— spirv-dis 看不到的指令(被 OpName 重命名掉的、被 OpNop 抹掉的)在这里是裸字节。

### Step 7 — 改完后强制全 fixture 跑一遍

任何 SPIR-V 阶段的修改都必须三个 Unity fixture(EndField blob1/blob2、
Deferred Clustered blob27、TextMeshPro 的几个变体)+ 一个 UE fixture
(M_Bamboo_tree `--shader-index 1904`)全跑通且 HLSL/GLSL 文件**字节级
等长**(或更长 — 不能丢符号)才算数。

---

## 4. SPIR-V 操作数布局速查(本仓库 rewriter 实际用到的)

> 这些不是 spirv-cross 文档,是**操作数槽哪些是 id、哪些是字面量**的最小心智模型。
> rewriter 写 use-count、删指令、加新 access chain 时频繁用到。

### 4.1 数据流类(操作数全是 id,可以无脑当 id 处理)

| Op | Code | 布局 |
| --- | --- | --- |
| `OpVariable` | 59 | `[h, ptr-type, result, storage-class, [initializer]]` storage-class 是字面量 |
| `OpLoad` | 61 | `[h, type, result, ptr, [memory-access]]` |
| `OpStore` | 62 | `[h, ptr, value, [memory-access]]` |
| `OpAccessChain` | 65 | `[h, ptr-type, result, base, idx0, idx1, ...]` indices 是 id(指向常量定义) |
| `OpInBoundsAccessChain` | 66 | 同上 |
| `OpIAdd` / `OpISub` | 128/130 | `[h, type, result, lhs, rhs]` |
| `OpIMul` | 132 | 同 IAdd |
| `OpShiftLeftLogical` | 196 | `[h, type, result, base, shift]` |
| `OpBitcast` | 124 | `[h, type, result, operand]` |
| `OpPhi` | 245 | `[h, type, result, val0, lbl0, val1, lbl1, ...]` |

### 4.2 字面量混杂类(use-count 危险区)

| Op | Code | 字面量在哪 |
| --- | --- | --- |
| `OpDecorate` | 71 | `[h, target, decoration-enum, lit, lit, …]` 装饰枚举之后**全是字面量** |
| `OpMemberDecorate` | 72 | `[h, struct, member-idx-lit, decoration-enum, lit, …]` |
| `OpName` | 5 | `[h, target, name-string-words…]` |
| `OpMemberName` | 6 | `[h, struct, member-idx-lit, name-string-words…]` |
| `OpEntryPoint` | 15 | `[h, exec-model-lit, entry-fn-id, name-string-words, interface-ids…]` |
| `OpExecutionMode` | 16 | `[h, entry-fn, mode-enum-lit, lit, lit, …]` |
| `OpExtInst` | 12 | `[h, type, result, set-id, instruction-enum-lit, operand0, operand1, …]` ⚠ 第 4 槽是字面量 |
| `OpCompositeExtract` | 81 | `[h, type, result, composite, idx0-lit, idx1-lit, …]` ⚠ 索引是字面量 |
| `OpCompositeInsert` | 82 | `[h, type, result, value, composite, idx0-lit, …]` |
| `OpVectorShuffle` | 79 | `[h, type, result, v1, v2, comp0-lit, comp1-lit, …]` |
| `OpSwitch` | 250 | `[h, selector, default-lbl, lit-target-pair…]` 字面量与 id 交替 |
| `OpConstant` | 43 | `[h, type, result, value-literal-words…]` |
| `OpString` | 7 | `[h, result, string-words…]` |
| `OpSource` | 3 | 全字面量 |
| `OpLine` / `OpNoLine` | 8 / 317 | 字面量 |

**结论**: 用 SSA id 频次做"指令死活"判定时,要么严格按上表的 id 槽位
取值,要么干脆**不要算 use-count**,改成观察"是否还有 live OpLoad
取它"这种结构性判据(参见 `RewriteLoadsAndCompositeExtracts` 末尾的
`aliveAccessChainConsumers`)。

### 4.3 BuiltIn 数值常用映射(`OpDecorate target BuiltIn N`)

| N | 含义 | spirv-cross HLSL 后端是否支持 |
| --- | --- | --- |
| 0 | Position (SV_Position 输出端) | ✅ |
| 1 | PointSize | ✅(忽略) |
| 5/6 | VertexIndex / InstanceIndex (SV_VertexID/SV_InstanceID) | ✅ |
| 7 | PrimitiveId | ✅ |
| 8 | InvocationId(HS/GS) | ❌ → 触发 `Unsupported builtin in HLSL: 8`,GLSL fallback |
| 11 | TessLevelOuter | ❌ |
| 12 | TessLevelInner | ❌ |
| 13 | TessCoord | ❌ |
| 14 | PatchVertices | ❌ |
| 15 | FragCoord (SV_Position 输入端) | ✅ |
| 17 | FrontFacing (SV_IsFrontFace) | ✅ |

如果错误信息是 8/11/12/13/14,**不要试图改 rewriter** —— 这是 spirv-cross
HLSL 的硬限制。让 GLSL fallback 接住,`SourceCode` 里就是 GLSL 文本。

---

## 5. Bug archetype 速查 — 出现 → 根因 → 修法

按出现频率倒序。每一条都对应历史上真实修过的 bug,详见 [CLAUDE.md §9](CLAUDE.md)。

### A. `Cannot subdivide a scalar value!` + ptr-vec4 接 OpLoad scalar

**症状**: 反汇编里出现
```
%X = OpAccessChain %_ptr_Uniform_v4float ... %memberIdx %componentIdx
%Y = OpLoad %v4float %X
```
但实际 access chain 下钻到了 vec4 的某个分量(scalar)。

**根因**: `TranslateMemberAccess` 在 vec4/matrix 成员加 component 索引时,
`MemberTypeId` 还是用了父类型(vec4 或 matrix)而不是子类型(scalar)。

**修法**: `StructuredMemberLayout` 维护 `ResolvedTypeId`(全类型)、
`ScalarTypeId`(分量标量)、`ColumnVectorTypeId`(矩阵列向量),按访问深度
选择正确的 `MemberTypeId`。**任何 `CreateTranslation(member.ResolvedTypeId, ...)`
带索引下钻时都要复核类型。**

### B. 死 access chain 没被 cleanup,spirv-cross 验证失败

**症状**: 反汇编里某个 OpAccessChain `%N` 完全没用户但 result type 是
`ptr-vec4 var %0 %register`,而新 struct 根本没那个布局。

**根因**: rewriter 走 `CanRewriteViaCompositeExtracts` 路径(裸取无法直
译,但每个 CompositeExtract 都能直译)时,**保留**裸 access chain 不动,
只重写下游的 extracts。但 OpVariable 的指针类型已经换成新 struct → 旧
access chain 的索引序列不再有效。

**修法**: `RewriteLoadsAndCompositeExtracts` 末尾加最终清理 —
**不算 use-count**,直接看模块里还有没有 live OpLoad 用这个 access chain。
没 live 用户就 NOP 掉。已实现于
`Spirv/StructuredCBufferRewriter.cs::RewriteLoadsAndCompositeExtracts` 末段。

### C. `IsValidFlatUniformBuffer` / `TryParseFlatAccessChain` 早返回 false 让整 CB 退化

**症状**: rewrite.txt 里 `[CB_Name] rewrite validation failed: unsupported
access chain parse for resultId=N op=65 words=[...]`,该 CB 在 HLSL 里输出
为 `float4 _m0[N]` 单数组。

**根因**: `TryDecomposeLinearIndexExpression` 对 `OpIAdd dyn dyn` /
`OpIMul dyn dyn` 这种二元都动态的表达式硬 `return false`。但下游普通未
识别 op 反倒走默认 fallback `dynamicIndexId=valueId, stride=1, offset=0`
返回 true —— 不一致导致一类常见的 `m0[base + offset]` 访问全部走不通。

**修法**: 二元都动态时去掉 hard fail,落到末尾默认 fallback,把整个
表达式当作不透明 dynamic id。已实现。

### D. CountResultUses 把字面量当 id 算

**症状**: 某 load 应被 NOP 但没被;调试发现 `compositeUsers != totalUsers`
就是因为 totalUsers 多算了 1 或 2;实际原因是某条 OpDecorate / OpExtInst
里的字面量数值碰巧 == 该 load 的 result id。

**根因**: 见 §4.2。OpExtInst 的扩展指令编号、OpMemberDecorate 的字节
偏移、OpCompositeExtract 的字面索引等都不是 id 但被通用计数器一视同仁。

**修法**:
1. `IsLiteralBearingMetadataOp()` 跳过纯 metadata/装饰类(`OpName`/
   `OpMemberName`/`OpDecorate`/`OpMemberDecorate`/`OpString`/`OpSource*`/
   `OpLine*`/`OpExecutionMode*`/`OpEntryPoint`/`OpCapability`/`OpExtension`/
   `OpExtInstImport`/`OpMemoryModel`/`OpDecorationGroup`/`OpGroupDecorate*`)。
2. **更优**: 关键判定不依赖通用 use-count,改成结构性观察(谁是 OpLoad
   的 pointer,谁是 OpCompositeExtract 的 composite……)。

### E. `FirstOrDefault().Key` 把 default(KeyValuePair) 当成功命中

**症状**: 多个名字争同一个 struct 成员,**最后写的胜出**。比如
M_Bamboo_tree 的 12 个 CBParam 全部覆盖到 member 0,最后一个名字
`Tree_sway_softness` 进了输出。

**根因**: C# 语言陷阱 —
```csharp
binding.MemberOffsets.FirstOrDefault(p => p.Value == byteOffset).Key
```
KeyValuePair 是 struct,不命中时返回 `(0, 0)`,`.Key == 0` 隐式转 `int?`
不为 null,`if (... is int i)` 模式匹配通过,`i == 0` 错命中第一个成员。

**修法**: 显式 foreach + return null。已修于 `ShaderDecompiler.cs:Member()`。

### F. List.Insert 让缓存的 Index 失效(隐患)

**症状**: rewriter 在迭代中 `module.Instructions.Insert(...)` 后,先前
缓存的 `loadInfo.InstructionIndex = X` 可能不再指向那个 Load。

**根因**: List.Insert 把 X 之后的所有元素后移,但缓存的索引没更新。

**修法**: 永远存 `SpirvInstruction` 引用(class,引用稳定),不存 index。
已修于 `RewrittenLoadInfo.Instruction`。

### G. dxbc-spirv 的 scalar cbuffer → spirv-cross HLSL 拒(DXBC-direct 专属)

**症状**: `cbuffer ID N (name: _T_V), member index 0 (name: _m0) cannot be expressed
with either HLSL packing layout or packoffset.` → 退 GLSL、丢符号。GLSL 产物里 cbuffer
长这样: `layout(..., scalar) uniform { float _m0[436]; }`。

**根因**: legacy DXBC 走 dxil-spirv 捆绑的 dxbc-spirv 直译时,cbuffer 出成 **scalar `float[4N]`**
(ArrayStride 4 + `GL_EXT_scalar_block_layout`)。HLSL cbuffer 是 float4 对齐的,表达不了这种
标量紧打包数组。DXIL 路出的是 `float4[N]`(stride 16)所以没这问题。

**修法**: `Spirv/ScalarCbufferVectorizer.cs`(在 rewriter 之前跑)把 Uniform cbuffer 的 scalar
`float[4N]`(stride 4)归一成 `float4[N]`(stride 16),access chain `_m0[j]` 改 `_m0[j>>2][j&3]`
(常量直接折叠成 `_m0[k][c]`,动态插 `OpShiftRightLogical`/`OpBitwiseAnd`)。底层 buffer 字节布局
不变,只改 SPIR-V 类型/索引表示。归一后下游(rewriter / patcher / spirv-cross)与 DXIL 路完全
一致,符号注入照常。**别靠 GLSL fallback 躲**(§0/§6 第二条铁律)。

---

## 6. 不要做的事(踩过的坑)

- ❌ **看到 `Cannot subdivide a scalar` 就猜是 patcher bug**。先 spirv-dis,
  几乎全是 rewriter 的类型注入错位或死 access chain。
- ❌ **靠 GLSL fallback 当万能解药**。GLSL fallback 是 tess/geom builtin
  限制的最后兜底,不是 rewriter bug 的躲避。HLSL 后端能修的就修,别让"反
  正 GLSL 也行"掩盖类型错位。
- ❌ **看到字面量数值和某个 id 撞车就硬编码 skip 那个 id**。对每个
  literal-bearing op 加一条 `IsLiteralBearingMetadataOp()` 才是正解;或干
  脆别用通用 use-count。
- ❌ **直接删除"看起来没用的"OpAccessChain**。先确认 rewrittenAccessChains
  里有它(说明是 rewriter 自己的产物),再 NOP。第三方 access chain 不要
  动。
- ❌ **改 rewriter 不跑 §2.3 全 fixture**。最常见回归: 修 vec4 case 让
  matrix case 退化、修 access chain 类型让 dynamic indexing 失效。

---

## 7. 调试小技巧

### 7.1 临时打印 + 自我消除

调试期间往 rewriter 里加 `Console.Error.WriteLine($"DBG ...")` 是合法的,
但**修复落地前必须全部删掉**。grep 验收:

```bash
grep -nE "DBG |Console\.Error\.WriteLine" "Source/Ruri.ShaderDecompiler/Spirv/" 
# 期望: 没有输出
```

### 7.2 失败产物保留

`ShaderDecompiler.cs:Decompile()` 的 catch 分支会把最后一版 SPV 与
rewrite summary 挂到失败的 `DecompileResult` 上,`Program.cs` 在 unity-binary
session 里会保存到磁盘。**不要随便改这个流程** —— 它是脱机调试的命脉。

### 7.3 `--shader-index` 单点反编译(UE 端)

```bash
Ruri.ShaderDecompiler.exe <lib.ushaderlib> <outDir> \
    --mapping <UnifiedShaderMetadata.json> \
    --shader-index <N>
```

UE 端测试不要全反编译(4000+ shader,很慢)。`--shader-index` 接 shader
编号(从文件名 `*_PS_<N>.hlsl` 取),几秒出结果,适合 fixture 单测。

### 7.4 双向核对 metadata vs 访问形态

**符号回填**正确性的判据是: 给每个命名成员的 `(ByteOffset, Type, Rows, Columns, IsMatrix, ArraySize)` 必须能解释 shader 在该寄存器/分量上的所有访问。

```bash
# 列出 shader 实际访问 cb._m0 的所有形态
grep "OpAccessChain.*%CB" /tmp/raw.txt | head -50

# 把 `%uint_N` 提出来做寄存器频次直方图
grep -oE "OpAccessChain.*%uint_[0-9]+" /tmp/raw.txt | grep -oE "%uint_[0-9]+$" | sort | uniq -c | sort -rn | head
```

如果 metadata 里 byte offset 对应的成员**类型/宽度**与 shader 实际访问形
态对不上 —— 类似 M_Bamboo_tree 里 register 2(byte 32)被 shader 读但
metadata 该位置无成员 —— 那是**上游 reader bug**(UE 侧
`UeShaderSymbolInputsReader` 或 Unity 侧 `ShaderRuriDecompileExporter`),
**不是 rewriter 的责任**。

---

## 8. 文件索引(改的时候要去看的)

| 文件 | 改什么时候去 |
| --- | --- |
| `Spirv/StructuredCBufferRewriter.cs` | 任何 access chain / load / cbuffer 结构重写 bug |
| `Spirv/SpirvPatcher.cs` | OpName / OpMemberName 注入相关(很少改)|
| `Spirv/SpirvModule.cs` | SPV 解析/序列化(几乎不改)|
| `Spirv/SpvOpCode.cs` | 缺常量(罕见)|
| `Spirv/SpvInstructionTraits.cs` | 缺 result-id 布局信息(罕见)|
| `ShaderDecompiler.cs` | 主管线 + 失败时 SPV 保留逻辑 |
| `Program.cs` | CLI 命令行参数、unity-binary session 输出布局 |
| `Unreal/UeShaderSymbolInputsReader.cs` | UE 端 metadata byte offset / 类型回填 |
| `Source/Ruri.RipperHook/.../ShaderRuriDecompileExporter.cs` | Unity 端 metadata 回填(项目规则:别动)|

---

## 9. 一句话方法论

**"看 stderr → spirv-dis 反汇编 → 比对原始与 patched → 缩到具体 SSA
id → 看类型/use 是否一致 → 改最小化的修复点 → 全 fixture 跑回归。"**

不绕路、不猜、不靠 fallback 当膏药。
