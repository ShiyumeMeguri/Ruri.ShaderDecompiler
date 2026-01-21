
**Ruri.ShaderDecompiler** 是一个通用的 Shader 反编译库，旨在将编译后的 Shader 二进制还原为高可读性的 HLSL 代码。

本项目致力于解决 Shader 反编译中“变量名丢失”的痛点，通过跨引擎的通用方案，实现符号（Symbols）与逻辑（Bytecode）的重组。

> ⚠️ **项目状态**：目前处于快速迭代开发阶段 (Work in Progress)。

## ✅ 待办事项 (Roadmap)

*   [ ] **完善 Unity 支持**：虽然核心架构已支持，但目前主要实现了 UE 的元数据解析，需补充 Unity ShaderLab/AssetBundle 的元数据提取器。
*   [ ] **统一反编译到ShaderLab** 

## 🎯 核心原理 (Core Philosophy)

本库的可行性建立在游戏引擎**“开发必然性”与“运行时机制”**的客观事实之上：
二进制剔除是性能选择，而非数据销毁：
发送到 GPU 的 Shader Binary（如 DXBC/DXIL/SPIR-V）通常会被剔除符号信息（Reflection Data），但这纯粹是为了节省显存带宽和提升 GPU 执行效率，因为 GPU 只需要知道“槽位 (Slot/Register)”，并不关心变量名。
引擎运行时必然保留符号表：
无论是 Unity 还是 Unreal Engine，为了支持开发者的 API 调用（例如 Unity 的 material.SetFloat("Color", ...) 或 UE 的参数设置），引擎的 CPU 运行时必然保留了一份“变量名 <-> 绑定槽位”的映射表。如果没有这份数据，CPU 就无法知道该往哪个寄存器塞数据。
Ruri.ShaderDecompiler 的本质工作就是**“数据重组”**：
它通过解析引擎侧保留的元数据（Unity Bindings / UE SRT），找回那些为了 GPU 性能而被剥离的符号，并将其重新“缝合”回 Shader 的中间表示（SPIR-V）中，从而实现高保真的反编译。
由于这份元数据的存在，Shader 的反编译具有了**可逆性**。**Ruri.ShaderDecompiler** 的工作流并非单纯的翻译指令，而是作为一个**“连接器”**：它提取引擎侧保留的符号名（如 `MainTexture`, `SunDirection`），通过修改中间层字节码（SPIR-V），将其重新注入到 Shader 逻辑中，从而生成带有原始变量名的高级代码，而非难以阅读的 `Texture_0` 或 `cbuffer_1`。

## ✨ 当前特性 (Features)

基于目前的代码实现，库包含以下功能：

### 1. 统一的中间层架构
无论输入源是古老的 **DXBC (DirectX 11)**，现代的 **DXIL (DirectX 12)** 还是 **SPIR-V (Vulkan)**，本库都会将其归一化为标准的 **SPIR-V** 格式进行处理。这确保了反编译逻辑的统一性。
