**Ruri.ShaderDecompiler** 是一个通用的 Shader 反编译库，用于将编译后的 Shader 二进制还原为**高可读性的 HLSL 代码**。

项目核心目标是解决 Shader 反编译中 **变量名丢失** 的问题，通过**跨引擎通用方案**，重建 **符号信息（Symbols）** 与 **字节码逻辑（Bytecode）** 之间的关联。

> ⚠️ **项目状态**：Work in Progress（快速迭代中）

---

## ✅ 待办事项（Roadmap）

* [ ] **完善 Unity 支持**
  当前已实现 UE 元数据解析，需补充 Unity ShaderLab / AssetBundle 的元数据提取。

* [ ] **统一反编译输出为 ShaderLab**

* [ ] 将 SPIRV-Cross 反编译的狗屎hlsl代码重新编译到DXBC 让编译器优化指令数量 然后重新反编译回更可读的hlsl

---

## 🎯 核心原理（Core Philosophy）

### 1. Shader 二进制并未“销毁”语义信息

GPU 侧的 Shader Binary（DXBC / DXIL / SPIR-V）通常会移除符号信息，仅保留寄存器与槽位绑定。这是**性能优化行为**，而非数据不可逆丢失。

---

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

无论输入为 **DXBC / DXIL / SPIR-V**，都会统一转换为 **SPIR-V** 进行处理，从而保证反编译逻辑的统一性与可扩展性。
