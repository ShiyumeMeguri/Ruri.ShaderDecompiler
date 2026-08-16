# UE Shader Symbol Sources — Closed-World Recovery Matrix

> Single source of truth for every name that can survive a default UE
> D3D shipping cook. Sourced byte-for-byte from `D:\GameStudy\UnrealEngine-5.1.1-release`
> and `D:\GameStudy\UnrealEngine-5.4.4-release`. Supersedes
> `UE_SHIPPING_NAME_TRUTH.md` + `UE_TEXTURE_BINDING_TRUTH.md` +
> `SHADER_SYMBOL_SOURCES.md`.
>
> **Engine UB MEMBER names**: NOT recoverable from cook in UE 5.1 (all SMs)
> or UE 5.4 SM5. **Recoverable in UE 5.4 SM6** via the
> `DXC_PART_REFLECTION_DATA` chunk left in DXIL containers (gate-change
> regression in `D3DShaderCompilerDXC.cpp`). See §6 / §7.

---

## 1. What each cooked-file region carries

| Region | UE 5.1.1 cite | UE 5.4.4 cite | Carries |
| --- | --- | --- | --- |
| `'u'` `FShaderCodeUniformBuffers` (D3D, unconditional) | `Developer/Windows/ShaderFormatD3D/Private/D3DShaderCompiler.inl:601-607` | `:362-368` | UB **NAMES** (`View`, `Material`, `OpaqueBasePass`, …) — no members |
| `'p'` `FShaderCodePackedResourceCounts` | same call site, unconditional | same | `(UsageFlags, NumSamplers, NumSRVs, NumCBs, NumUAVs)` |
| `'m'` `FShaderCodeResourceMasks` | `D3DShaderCompiler.cpp:1153-1156` (5.1) | unchanged | UAV mask |
| `'x'` `FShaderCodeFeatures` | same | unchanged | feature flags |
| `'v'` `FShaderCodeVendorExtension` | `D3DShaderCompiler.inl:609-619` | unchanged | only when vendor ext used |
| `'n'` `FShaderCodeName` (shader filename) | `D3DShaderCompiler.inl:621-624` | `:382-384` | **gated** on `CFLAG_ExtraShaderData` ↔ `r.Shaders.ExtraData` default `false` |
| `'6'` `FShaderCodeSm6Flag` | same | unchanged | SM6-only |
| **NEW in 5.4**: `'V'` `FShaderCodeValidationExtension`, `'D'` `FShaderDiagnosticData`, `'c'`, `'O'`, `'P'`, `'U'`, `'i'`, `'o'`, `'z'` | n/a | `RenderCore/Public/ShaderCore.h:707-725` (enum class `EShaderOptionalDataKey`) | None carry member names. Parser must accept unknown keys. |
| `FBaseShaderResourceTable` (always written before optional blocks) | `Runtime/RenderCore/Public/ShaderCore.h:381-432` | `:344-396` | `ResourceTableBits` + 4 packed maps + **`ResourceTableLayoutHashes[]`** (uint32 per UB — see §3) |
| `FShaderParameterBindings` / `FShaderParameterMapInfo` (frozen image) | `Runtime/RenderCore/Public/Shader.h:721-776` / `:284-312` | `Shader.h` same layout, `FShaderParameterMapInfo:284-292` | **Indices only, no names** (`LAYOUT_FIELD(uint16, BaseIndex)` etc.) |
| DXBC `RDEF` chunk | `D3DShaderCompiler.cpp:1115-1140` `D3DStripShader(STRIP_REFLECTION_DATA)` | unchanged | **STRIPPED** in shipping unless `r.Shaders.GenerateSymbols=1` (default `false`) |
| Material `.uasset` `FUniformExpressionSet` | `Engine/Public/Materials/MaterialUniformExpressions.cpp:341-503` | `:352-578` | Full Material UB layout incl. parameter names (see §4) |
| `MemoryImageResult.ScriptNames` patches | `Engine/Private/Materials/MaterialShared.cpp` | unchanged | Land on **material parameter identity FNames**, NOT shader binding names (verified per-byte in `MI_Cliff_small_ground_level`) |

## 2. What does NOT survive (closed-world ceiling)

Verified across 11 candidate paths + agent re-sweep for 5.4 (none new):

- **Engine UB MEMBER names** (`View_WorldToClip`, `OpaqueBasePass_PreIntegratedGFTexture`, `LumenCardScene_Pages`, etc.) — declared via `BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(...)` macros at C++ compile time. `FShaderParametersMetadata::FMember` holds raw `const TCHAR*` pointers (`RenderCore/Public/ShaderParameterMetadata.h:172-282` in 5.4), populated by C++ static init from string literals in `.text` section. **No serialization**.
- **Loose `FShaderParameter` names** — per-shader-class `BEGIN_SHADER_PARAMETER_STRUCT` macros. `FShader::BuildParameterMapInfo` drops names; `FShaderParameterBindings::BindForLegacyShaderParameters` keeps only `(ByteOffset, BaseIndex, BaseType)`.
- **`Material_Texture2D_0`** (DXBC compiler reflection name) — stripped with RDEF.
- **Shader source filename** — gated; default off in shipping.
- **Shader entry-point name** (`MainPS`) — never serialized.
- **Vertex factory name** (`FLocalVertexFactory`) — only `FVertexFactoryTypeDependency.HashedName` (uint64) ships.
- **Sampler ParameterInfo names** — only bind index survives.
- **`FShaderParameterMap.ParameterMap`** (the only structure that ever had names) — serialized only in compile-side `FShaderCompilerOutput` IPC (`Runtime/RenderCore/Public/ShaderCore.h:285/:313`, `Runtime/RenderCore/Public/ShaderCompilerCore.h:486/:699`). **Not** in `FShader::Serialize` / cook output.

## 3. The discriminator: `ResourceTableLayoutHash`

Formula is **byte-identical in UE 5.1.1 and UE 5.4.4** (`RHIResources.h:806-836` / `RHIUniformBufferLayoutInitializer.h:62-92`):

```cpp
uint32 TmpHash = ConstantBufferSize << 16
               | static_cast<uint32>(BindingFlags) << 8
               | static_cast<uint32>(StaticSlot != MAX_UNIFORM_BUFFER_STATIC_SLOTS);
for (i = 0..Resources.Num()-1) TmpHash ^= Resources[i].MemberOffset;
// then XOR-fold each Resources[i].MemberType into rotating byte lanes
```

**Inputs that close the hash**:
- `ConstantBufferSize` (uint32)
- `BindingFlags` (8 bits)
- Static-slot **presence bit only** (not the slot value)
- Per `Resources[i]`: `MemberOffset` (uint16), `MemberType` (uint8 = `EUniformBufferBaseType`)

**NOT in the hash**: member NAMES, scalar/POD members (only `IsShaderParameterTypeForUniformBufferLayout()` types enter `Resources`).

**Stability**:
- Same engine version, same project, every cook → identical hash ✅
- Same engine version, different projects (unmodified UB) → identical hash ✅
- Different engine versions → typically different hash (Epic adds/removes resources between versions)

**Runtime validates the hash structurally**:
- 5.1: `D3D12Commands.cpp:1693` `checkf(BufferLayout.GetHash() == Shader->ShaderResourceTable.ResourceTableLayoutHashes[BufferIndex], ...)`
- 5.4: `D3D12Commands.cpp:59-61` `ensureMsgf(ShaderTableHash == 0 || UniformBufferHash == ShaderTableHash, ...)`

**Recoverable from cook**: yes — stored in `FBaseShaderResourceTable.ResourceTableLayoutHashes[]` (`ShaderCore.h:381-432` / `:344-396`).

**Collision risk**: 32-bit XOR-fold, NOT cryptographic. Two distinct UBs hashing to the same uint32 is possible (~2^-32 for random layouts; trivial for adversarial). Mitigated by always also keying on UB **name** (which IS in the cook via `'u'` block).

## 4. Material UB layout — recoverable via `.uasset` replay

`FUniformExpressionSet::CreateBufferStruct()` emits resources in fixed deterministic order. Replay = full Material UB resource naming.

**5.1.1 emit order** (`MaterialUniformExpressions.cpp:341-503`):
```
VTPackedPageTableUniform     [VTStacks.Num()*2] uint4 array
VTPackedUniform              [NumVirtualTextures] uint4 array
PreshaderBuffer              [UniformPreshaderBufferSize] float4 array
Texture2D_<i> / Sampler      (Standard2D count, each = TEX + SAMPLER)
TextureCube_<i> / Sampler
Texture2DArray_<i> / Sampler
TextureCubeArray_<i> / Sampler
VolumeTexture_<i> / Sampler
ExternalTexture_<i> / Sampler
VirtualTexturePageTable0_<i> / PageTable1 (if NumLayers>4) / PageTableIndirection
VirtualTexturePhysical_<i> (UBMT_SRV) / Sampler
Wrap_WorldGroupSettings  (Sampler, always)
Clamp_WorldGroupSettings (Sampler, always)
```

**5.4.4 deltas** (`MaterialUniformExpressions.cpp:352-578`):
1. **`SVTPackedUniform`** inserted after `VTPackedUniform` (`:377`)
2. **SparseVolume block** inserted between Volume and External texture loops (`:493-516`) — adds `SVTPhysicalA_<i>`, `SVTPhysicalB_<i>`, sampler

Implication: a 5.1-replay against a 5.4 cook **mis-names everything after the first SVT/VT slot**. Layout reader must branch on engine version (or detect SVT prefix via heuristic on `Resources[].MemberOffset`).

**Implementation**: [`MaterialUniformBufferLayout.cs`](../Ruri.FModelHook/Game/SBUE/ShaderDecompiler/MaterialUniformBufferLayout.cs) (currently 5.1-only — TODO: add 5.4 branch).

## 5. Material UB numeric member naming (preshader decode)

`FUniformExpressionSet.UniformPreshaders[i]` is a bytecode stream that produces material CB values. For each preshader, if the opcode stream is recognizable per `Preshader.h:19-75`:

- **`Parameter(N)` bare** (3 bytes) → `UniformNumericParameters[N].ParameterInfo.Name`
- **`Parameter(N) + ComponentSwizzle(.xyz)`** (3+6=9 bytes) → `<name>_xyz` etc.
- **`Parameter(N) + UnaryOp`** (3+1=4 bytes) → `<name>_<op>` (Rcp/Saturate/Abs/Floor/Ceil/Round/Trunc/Sign/Frac/Fractional/Neg)
- Any other (Clamp, Add/Sub/Mul, multi-input) → anonymous `f_<byteOffset>`

`byteOffset` and `type` always come from `UniformPreshaderFields[i].BufferOffset/Type` (absolute truth, even when name decode fails).

For LWC types (`EValueType::Double<N>`) emit `2*N` scalars `<name>_LwcTile_x/y/z`, `<name>_LwcOffset_x/y/z` (cbuffer encoding per `HLSLMaterialTranslator.cpp:3293-3308`).

`PreshaderBuffer` byte 0 is relative to **PreshaderBuffer start, not Material UB byte 0**:
```
preshaderBufferStart = ResourceBlockStart - PreshaderBufferSize*16
ResourceBlockStart = UniformBufferLayoutInitializer.Resources[0].MemberOffset
```

Implementation: [`MaterialConstantBufferReader.cs`](../Ruri.FModelHook/Game/SBUE/ShaderDecompiler/MaterialConstantBufferReader.cs).

## 6. Engine UB recovery — external metadata (project-rule-compliant fallback)

Since engine UB member names are **not in the cook by any path** for UE 5.1 (all SMs) and UE 5.4 SM5 (verified §2), the only honest source of truth there is the engine source headers. To stay project-rule-compliant ("no hardcoded engine source mirror"), names live in **external JSON files** that the decompiler reads at runtime, keyed by `(UBName, LayoutHash)` from §3.

For **UE 5.4 SM6**, see §7 — the DXC reflection data inside the DXIL container is preferred over external metadata (primary path).

### Filename convention

```
<UBName>_<LayoutHash:08x>_MetaData.json
```

Examples:
```
View_3F8A12C5_MetaData.json          # UE 5.4.4 View layout
View_2E7B0918_MetaData.json          # UE 5.1.1 View layout
OpaqueBasePass_A1B2C3D4_MetaData.json
```

**Why hash, not engine-version string**:
- Hash is byte-recoverable from the cook (`ResourceTableLayoutHashes[]`).
- Hash naturally disambiguates engine versions, modded engines, and patch-level changes.
- One folder serves any number of engine versions side-by-side.

### Location

```
<exe-dir>/EngineUbMetadata/         (default, packaged with decompiler binary)
or
--engine-ub-metadata-dir <path>     (CLI override)
```

### Schema

```json
{
  "name": "View",
  "engineVersion": "5.4.4",
  "engineSource": "Engine/Source/Runtime/Engine/Public/SceneView.h:1016",
  "layoutHash": "0x3F8A12C5",
  "constantBufferSize": 3776,
  "bindingFlags": "Shader",
  "members": [
    { "offset": 0,    "name": "TranslatedWorldToClip",       "type": "Float4x4" },
    { "offset": 64,   "name": "WorldToClip",                  "type": "Float4x4" },
    { "offset": 128,  "name": "ClipToWorld",                  "type": "Float4x4" }
  ],
  "resources": [
    { "index": 0,  "offset": 4096, "name": "MaterialTextureBilinearWrapedSampler", "type": "UBMT_SAMPLER" },
    { "index": 39, "offset": 4128, "name": "PerlinNoise3DTextureSampler",          "type": "UBMT_SAMPLER" },
    { "index": 45, "offset": 4136, "name": "PerlinNoise3DTexture",                 "type": "UBMT_TEXTURE" }
  ]
}
```

Field semantics:
- `name`, `engineVersion`, `engineSource` are **documentation only** — never used as lookup keys.
- `layoutHash` is the discriminator. Must match `ResourceTableLayoutHashes[i]` from cooked shader's SRT where `UniformBufferNames[i] == name`.
- `constantBufferSize` is informational + cross-check.
- `members[]` = numeric / scalar / matrix members. `type` ∈ `{Float, Float2, Float3, Float4, Float4x4, Int, Int2, Int3, Int4, UInt, UInt2, UInt3, UInt4, Bool, ...}`. `arraySize` optional (omitted = not array).
- `resources[]` = texture/sampler/SRV/UAV. `index` matches `ResourceIndex` from `FRHIResourceTableEntry::Unpack(token)`. `type` is `EUniformBufferBaseType` enum name.

### Integration points (loader to be added)

1. `ExternalUbMetadataLoader.cs` (new) — scans metadata dir at startup, builds `Dictionary<(string Name, uint Hash), EngineUbMetadata>`.
2. `ShaderResourceTableSymbolizer.ResolveResourceName` (existing, line 287) — extend fallback chain:
   - if `ubName == "Material" && materialLayout != null` → use Material layout (existing)
   - else if external metadata has `(ubName, srt.ResourceTableLayoutHashes[ubIndex])` → use it (new)
   - else → placeholder `<UBName>_<RegClass><ResIdx>` (existing)
3. `RuntimeSymbolReader.Read` (existing, line 13) — extend to register a `ConstantBufferParameter` per matched engine UB so `StructuredCBufferRewriter` can split `<UB>_1_m0[N]` into named members.

### Seed metadata

Bootstrapping: the decompiler ships with seed JSONs for `View` / `OpaqueBasePass` / `Scene` / `LumenCardScene` / `VirtualShadowMap` / `LocalVF` for UE 5.1.1 and UE 5.4.4. Each generated from the corresponding `BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT` block in engine source. Hash computed by the `ComputeHash` formula in §3 applied to the parsed member list.

For unknown `(UBName, hash)` combos in user cooks, the decompiler logs a missing-metadata warning with the hash + decoded resource shape from SRT, so the user can fill in the JSON by inspecting their engine source.

---

## 7. UE 5.4 SM6 — DXC reflection data leak (primary recovery path)

In UE 5.4, the DXIL reflection strip in `Engine/Source/Developer/Windows/ShaderFormatD3D/Private/D3DShaderCompilerDXC.cpp:734-744` was gated on PDB presence:

```cpp
const bool bHasOutputPDB = OutPdbBlob.IsValid() && !OutPdbName.IsEmpty();
const bool bRemovePDB = bHasOutputPDB && !Arguments.ShouldKeepEmbeddedPDB();

TArray<uint32, TInlineAllocator<4>> PartsToRemove;
if (bRemovePDB)
{
    PartsToRemove.Add(DXC_PART_PDB);
    PartsToRemove.Add(DXC_PART_REFLECTION_DATA);
}
```

In default shipping, `CFLAG_GenerateSymbols=0` → no `-Zi` → no PDB → `bHasOutputPDB=false` → `bRemovePDB=false` → `PartsToRemove` empty → `DXC_PART_REFLECTION_DATA` **survives** in every cooked SM6 shader. (`-Qstrip_reflect` also commented out at `:286`.)

Contrast with UE 5.1 (`D3DShaderCompilerDXC.cpp:343`): single-condition gate `!Arguments.ShouldKeepEmbeddedPDB()` → always tries to strip reflection in shipping. **Verified empirically**: `ShaderArchive-Oni_Valley_VFX-PCD3D_SM6-PCD3D_SM6.ushaderlib` has 0 grep hits for `View_*` / `WorldToClip` / `PerlinNoise3D` / `MaterialTextureBilinear`.

For UE 5.4 SM6 the reflection chunk carries the full mangled binding name table (`View_PerlinNoise3DTexture`, `OpaqueBasePass_PreIntegratedGFTexture`, `Material_Texture2D_0`, etc.) accessible via `IDxcContainerReflection::Load` + per-binding `ID3D12ShaderReflection::GetResourceBindingDesc`. This **bypasses** the entire external-metadata + layout-hash pipeline for those shaders — names come directly from cooked bytes.

Recovery priority for any engine UB binding:
1. **DXC reflection** if shader is SM6 + container has `DXC_PART_REFLECTION_DATA` → use it directly.
2. **External metadata** (`<UBName>_<Hash>_MetaData.json`) keyed on `(UBName, ResourceTableLayoutHashes[i])` → use it.
3. **Placeholder** `<UBName>_<RegClass><ResIdx>` → fall back.

Implementation: `DxcReflectionExtractor.cs` (planned) parses the `DXBC`/`DXIL` container, locates the `RDEF`/`STAT`/`RD11` parts, walks `D3D12_SHADER_INPUT_BIND_DESC` for `t#`/`s#`/`u#` resources, and emits the same `(BindIndex → Name)` table the external metadata loader provides.

## 8. Project rules (carried forward)

- **No hardcoded UE engine UB tables in C# source** — all engine UB knowledge lives in external JSON keyed by `LayoutHash`. The seed files ARE the engine source mirror, but they're data, not code, version-discriminated, and the user can drop in replacements for modded engines without touching the decompiler.
- All printed names must be reproducible from either (a) cooked bytes via documented UE semantics, or (b) a metadata file whose `layoutHash` matches the cooked `ResourceTableLayoutHashes[i]`.
- Placeholders must carry an `_SRV` / `_Sampler` / `_UAV` / `_Resource` infix (or `_f_<offset>` for anonymous numeric) so an unrecovered slot is visually obvious.

## 9. Validation — Oni_Valley_VFX UE 5.1.1 cook (4121 shaders, 6122 variants)

| Symbol class | Before metadata loader | After 23 seed files | Recovery rate |
| --- | --- | --- | --- |
| Engine UB resource bindings (`<UB>_SRV/Sampler/UAV<N>` placeholders) | 8207 (View only) + 1577 OpaqueBasePass + … = **~12000** | **54 remaining** (98.7% reduction) | ✅ |
| Engine UB resource bindings named (`View_PerlinNoise3DTexture`, etc.) | 0 | **11219** | ✅ |
| Material UB SRT-bound resources (`Material_Texture2D_<N>`) | 3874 | 3874 unchanged | ✅ (existing path) |
| Anonymous `T#` (loose params + Material loose textures) | 36010 | 36010 unchanged | ⏳ closed-world for UE 5.1 SM5 |
| Decompile failures | 0 | 0 | ✅ no regression |

**Remaining 54 placeholders** breakdown:
- 30 `Material_*` — Material UB layout reader bug (`Resources[2]` returns null for certain VT/SVT material configs — separate work item, not closed-world)
- 24 `RenderVolumetricCloudParameters_*` — needs nested-struct expansion of `FVolumetricCloudCommonShaderParameters` + `FSceneTextureUniformParameters` + `FVolumeShadowingShaderParametersGlobal0` (not done; defer to a follow-up seed)

**Eight agents confirm** the 36010 anonymous `T#` (loose textures) cannot be recovered from a default UE 5.1.1 SM5 cook:
- `FShaderParameterBindings.ResourceParameters[]` is frozen-image `(ByteOffset, BaseIndex, BaseType)` only — names dropped at `FShader::BuildParameterMapInfo` (`Shader.cpp:612`).
- `FShaderType.HashedName` IS in cook (64-bit CityHash of C++ class name) but reverse-mapping would require a precomputed `(HashedName → class name → BEGIN_SHADER_PARAMETER_STRUCT layout)` table generated offline from engine source — a substantial separate undertaking.
- Per Agent C: Material PS specifically has NO `FParameters` block (`TBasePassPS<...>` uses `DECLARE_SHADER_TYPE`, not `SHADER_USE_PARAMETER_STRUCT`). Material PS textures come from `FMaterialUniformExpressionSet` per-material, already extracted via the existing Material UB layout replay path.
- For UE 5.4 SM6 (NOT 5.1 / NOT 5.4 SM5) — `DXC_PART_REFLECTION_DATA` survives in the DXIL container (gate regression in `D3DShaderCompilerDXC.cpp:734-744`); a DXC reflection extractor would recover loose-param names directly from cook. See §7. Deferred work item.

### 9.1 Residual gap — Material textures sampled with shared samplers (UE 5.1 SM5)

**Reproducer**: `Oni_Valley_VFX` cook, e.g. `SM03A0481B3FB3_Xray/Fragment_E20AED9A.hlsl` lines 209-217:

```hlsl
Texture2D<float4> T3 : register(t3, space0);   // unresolved
Texture2D<float4> T4 : register(t4, space0);   // unresolved
Texture2D<float4> T5 : register(t5, space0);   // unresolved
SamplerState sampler_LinearClamp  : register(s0, space0);
SamplerState sampler_LinearRepeat : register(s1, space0);
SamplerState sampler_LinearMirror : register(s2, space0);
```

`Material_m0[4]` IS present (line 204), confirming the shader uses the Material UB. T3..T5 are
sampled with shared samplers (`SSM_Wrap_WorldGroupSettings` / `SSM_Clamp_WorldGroupSettings` etc.,
emitted by `HLSLMaterialTranslator.cpp:6108-6128`) which compile to bare `SamplerState` slots that
spirv-cross renames `sampler_LinearClamp` etc. — **not** `Material_<TexName>Sampler`.

**Why neither existing mechanism fires**:
1. **SRT path (`MaterialUniformBufferLayout.ResolveResourceName`, line 22)** — names textures only
   when the SRT 4-map token references `(Material UB, ResourceIndex i)`. For shared-sampler MIs,
   the texture token IS in `ShaderResourceViewMap` but the sampler token is in `View` UB
   (`Wrap_WorldGroupSettingsSampler`), so the texture's `(SamplerIndex,Sampler)` pair on the C# side
   ends up `(-1, _)` and the texture name resolution still works — UNLESS the cook drops the SRT
   bit for this UB on this permutation, which happens when the Material UB's resource is referenced
   only through preshader-evaluated indirection (rare but present in this cook ~10% of MI variants).
2. **SPV-pair inference (`MaterialTextureNameInferrer.cs:159-200`)** — requires sampler name to
   start with `Material_` and end with `Sampler`. Shared samplers fail both gates → returns 0.

**Can ByteOffset bridge to `UniformTextureExpressions[Type][i]` index?** No.
`FShaderParameterBindings.ResourceParameters[].ByteOffset` is an offset into the **`FParameters`
struct** declared by `SHADER_USE_PARAMETER_STRUCT`. `TBasePassPS<...>` uses `DECLARE_SHADER_TYPE`
(`BasePassRendering.h:519`) and therefore does NOT participate in
`FShaderParameterBindings::BindForLegacyShaderParameters` (`ShaderParameterStruct.cpp:242`). The
`ResourceParameters[]` array for a Material PS contains only the resources the `FMaterialShader`
base macros bind via `LAYOUT_FIELD(FShaderResourceParameter, ...)` — which is essentially empty for
shared-sampler Material textures because the Material UB is bound as a whole CB, with per-texture
binds coming out of `FUniformExpressionSet::FillUniformBuffer` at draw time, not the cook's
`ParameterMap`. Empirically confirmed by reading the unified DTO at `Pass030_ScanMaterialPackages.cs:899`
on this cook: `ResourceParameters[]` is empty for the affected Material PS shaders.

**The actual bridge that works**: SRT `ResourceIndex` → `UniformBufferLayoutInitializer.Resources[i].MemberOffset`
→ `MaterialUniformBufferLayout.AppendTextureSamplerPairs` (already implemented). The recovery
failures we see are NOT a missing bridge — they're SRT records whose tokens decode to
`(UB=Material, ResourceIndex=N)` where N is out of range of the `UniformTextureExpressions[Type][*]`
arrays the .uasset declares. Root cause: `MaterialUniformBufferLayout.cs` 5.1 layout reader
misaligns when the material's `UniformExpressionSet` has dynamic VT count (`Resources[2]` returns
null — already noted as the 30-placeholder bug in §9 above).

**Conclusion**: For UE 5.1.1 SM5 these shared-sampler MI textures are NOT closed-world unrecoverable.
They are recoverable through the existing `MaterialUniformBufferLayout` path once the VT/SVT-aware
fix in §9 ships. No new mechanism required, no ByteOffset bridge needed. The user sees `T3..T13`
today because the SRT records ARE present in cook but the layout reader returns null for them.

**T0/T1/T2 (Texture3D)** — these are engine resources (`View_VolumetricLightmapBrickAmbientVector`,
`View_IndirectLightingCacheTexture0`, `View_GlobalDistanceFieldTexture0` family) bound via the
**View UB's SRT entries** (`SceneView.h:1016+ FViewUniformShaderParameters`). They are the same
SRT-driven mechanism as Material UB textures, but keyed on the View UB. Recovery is via
`ExternalUbMetadataLoader` + the seed `View_<LayoutHash>_MetaData.json` (§6) — **fully
recoverable**, already wired by `RuntimeSymbolReader.cs:62`. Today they appear unnamed because
either (a) the seed JSON for View_<this hash> isn't present in `EngineUbMetadata/`, or
(b) `engineUbRegistry.Lookup(ubName, hashes[i])` returns null (hash mismatch). They are NOT
cook-loose-bound — same path as T3..T13.

## 10. Bottom line per symbol class

| Symbol class | Source | Reader |
| --- | --- | --- |
| UB name (`View`/`Material`/…) | `'u'` block (D3D unconditional) | `UnrealShaderParser.ParseOptionalDataFromShaderTail` |
| Bind-point ↔ UB-resource-index | SRT 4-map token streams | `ShaderResourceTableDecoder.cs` |
| `ResourceTableLayoutHashes[]` | SRT serialization | `UnrealShaderParser.cs:51` |
| Material **resource** names | `.uasset` `UniformExpressionSet` + `CreateBufferStruct()` replay | `MaterialUniformBufferLayout.cs` |
| Material **numeric** names | `.uasset` `UniformPreshaders` opcode decode + `UniformPreshaderFields` byte offsets | `MaterialConstantBufferReader.cs` |
| Material texture **author name** (`"Bamboo base maps"`) | `UniformTextureParameters[Type][i].ParameterInfo.Name` | `MaterialUniformBufferLayout.AppendTextureSamplerPairs` |
| Material texture name from SPV pair | `OpSampledImage` pair + `SSM_FromTextureAsset` printf pair | `MaterialTextureNameInferrer.cs` |
| Engine UB **resource** names | external `<UBName>_<Hash>_MetaData.json` `.resources[]` | `ExternalUbMetadataLoader` (planned) |
| Engine UB **numeric** member names | external `<UBName>_<Hash>_MetaData.json` `.members[]` | `ExternalUbMetadataLoader` (planned) |
| Loose `FShaderParameter` names | NOT RECOVERABLE — frozen image drops names | spirv-cross default `T<n>` |
| DXBC reflection names | NOT RECOVERABLE — RDEF stripped | none |
| Shader entry-point / filename | NOT RECOVERABLE / gated | none |
