Ruri.ShaderDecompiler 约束(用法见 README.md):引擎无关的着色器反编译库,符号由宿主从引擎元数据喂入。
1. 路线固定且全程进程内:二进制到 SPIR-V、标量布局归一、跨阶段槽位分配、常量缓冲结构化、宿主绑定器、符号注入、SPIRV-Cross 出 HLSL;引擎知识只经调用方构造的符号表(SerializedProgramData)与 IModuleSymbolBinder 进入,库内禁引擎分支,库自己不带任何 binder。
2. 符号只能追溯到 cooked 数据里真实存在的字节,或 layoutHash 与 cook 中 ResourceTableLayoutHashes 严格匹配的外部元数据;查不到就保持匿名,禁猜名、禁硬编码引擎 UB 成员表、禁用寄存器类加下标编成员名。
3. 库自己发明的名字必须一眼读成陈述而不是作者名:Stripped_{字节偏移}(名字没熬过编译)、Unstructured(没能拆成员)、Unmapped(没有符号覆盖的字节区间),三个标记只在 GeneratedNames 声明一处,布局层、命名层与源码适配器共用。
4. 引擎 UB 成员名只能来自 Ruri.UEShaderTpkDumper 从引擎源码导出的种子(Ruri.FModelHook/EngineUbMetadata),缺失只降级为匿名,绝不失败。
5. UE closed-world 上限:UB 名('u' 可选块)、材质资源 typed 名(需 .uasset 重放 CreateBufferStruct)、材质参数用户名、SRT 的绑定点到 UB 资源下标、材质 CB 的 preshader 偏移(UniformPreshaderFields 是绝对权威)可恢复;引擎 UB 成员名、不经 UB 的散绑资源名、编译期反射名、着色器源文件名与入口串不可恢复。
6. native 只来自 NuGet(dxil-spirv 经 AssetRipper.Bindings.DxilSpirV,spirv-cross 经 Silk.NET),NativeLibraryResolver 先探本程序集旁的 runtimes/{rid}/native 再探宿主目录与 Tools/;改 csproj 的 PackageReference 后须 clean rebuild(删 obj 与 bin),否则 dxil_spv_parse_dxil_blob failed (-4) 且整库只剩寥寥几个能反编译。
7. DecompilePipeline 非线程安全,每个工作线程自持一份(构造不解析资源,廉价);IModuleSymbolBinder 会被并发调用。
8. 反编译失败先判是哪一阶段坏:--debug-dump {dir} 写出 .01.after-frontend、.02.after-structuring、.03.after-symbol-injection 三份 .spv。
9. 待办:Unity 直出 ShaderLab;SPIR-V 经 SPIRV-Cross 出 HLSL 后重编 DXBC 以优化指令数,再反编译成更可读的 HLSL。
