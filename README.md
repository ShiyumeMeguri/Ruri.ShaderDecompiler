**Ruri.ShaderDecompiler** 是一个通用的 Shader 反编译库，用于将编译后的 Shader 二进制还原为**高可读性的 HLSL 代码**。

项目核心目标是解决 Shader 反编译中 **变量名丢失** 的问题，通过**跨引擎通用方案**，重建 **符号信息（Symbols）** 与 **字节码逻辑（Bytecode）** 之间的关联。

> ✅️ **项目状态**：基本中间层和符号注入已完成 只需在引擎端构建metadata传入即可带符号反编译

---

## 🚀 使用方法（Usage）

*   **对于 Unity：**
    操作体验与 AssetRipper 一致，只需在工具中开启 `ShaderDecompilerHook` 即可完成导出和反编译。

*   **对于 Unreal Engine：**
    操作体验与 FModel 一致，需在工具中开启 `ShaderDecompilerHook` 。
    1. 首先需要 Dump 游戏的 Mapings 类型树，并在 FModel 设置中加载。
    2. 找到并选择 `[GameName]/Contents` 文件夹下的 `ShaderArchive-*`名字的Shader二进制。
    3. 右键点击并选择 Export Raw Data (.ushaderbytecode)。
    4. 工具会自动抓取所有材质球和 Shader 符号的 JSON 数据，随后自动执行反编译。 (如果没有加载Mapings将会丢失所有材质球提供的符号)

---

## ✅ 待办事项（Roadmap）

* [ ] 将 SPIRV-Cross 反编译的狗屎hlsl代码重新编译到DXBC 让编译器优化指令数量 然后重新反编译回更可读的hlsl
* [ ] 由于UE符号剔除很严重 并且运行时不需要符号的直接就是二进制丢失了一切结构信息 比如引擎内置的Global的cb 据AI所说没有提供编译后可逆的符号 我能想到的解决方法是提供一个分版本的引擎shader结构定义 ~~但这个要分版本手动填写 我没那么多精力 还是靠后人的智慧看看有没有我分析漏的直接dump符号吧~~ 最近Token翻倍让AI实现了一个dumper 彻底解决了所有符号的问题 (~~后人竟是我自己~~)

---

## 🎯 核心原理（Core Philosophy）

### 1. Shader 二进制并未“销毁”语义信息

GPU 侧的 Shader Binary（DXBC DXIL/ SPIR-V）通常会移除符号信息，仅保留寄存器与槽位绑定。这是**性能优化行为**，而非数据不可逆丢失。

## 原理上来说 DXBC也支持符号注入 但是没有一个稳定偏官方性质的反编译器所以放弃

### 2. 引擎运行时必然保留符号映射

无论 Unity 还是 Unreal Engine，为了支持 CPU 侧参数设置（如按变量名设置材质参数），引擎运行时必须保留：

```
变量名 <-> 绑定槽位
```

的映射关系。

---

### 3. Ruri.ShaderDecompiler 的工作本质

本库不依赖猜测或模式匹配，其核心行为是：

* 解析引擎侧元数据（Unity Bindings / UE SRT）
* 将符号信息重新注入 Shader 的中间表示（SPIR-V）
* 重建可读、可维护的高级 Shader 代码

本质上是一次**符号与逻辑的重组过程**。

---

## ✨ 当前特性（Features）

### 1. 统一中间层（SPIR-V）

无论输入为 **DXBC / SPIR-V / DXIL**，都会统一转换为 **SPIR-V** 进行处理，从而保证反编译逻辑的统一性与可扩展性。 

Unity 是由AssetRipper解析并填充metadata符号到此工具即可完美还原符号反编译 已在私有仓库完成

UE 是由CUE4Parse解析并填充metadata符号到此工具即可完美还原符号反编译 ~~UE剔除过于严重并且屎山代码过于恶心已被气炸优先级已降低~~ 已完成
