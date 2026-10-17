Ruri.ShaderDecompiler:把编译后的 DXBC/DXIL/SPIR-V 着色器还原成高可读 HLSL 的库,核心是把引擎侧元数据(Unity Bindings、UE SRT)里的符号重新注入 SPIR-V,不靠猜测或模式匹配。
1. 着色器二进制并未销毁语义:引擎运行时必须保留变量名到绑定槽位的映射才能按名设参,符号只是被剥掉了。
2. Unity:AssetRipper 里开 ShaderDecompiler 特性即导出并反编译。
3. UE:走 FModelHook 无头 CLI(--game-config),材质符号以加载 .usmap 为前提,没有就丢光材质提供的全部符号。
4. 命令行:Ruri.ShaderDecompiler.exe {shader} [{out}] [--symbols {json}] [--format dxbc|dxil|spv|auto] [--shader-model N] [--debug-dump {dir}];批量 --batch {dir} [--max-concurrency N]。
5. 约束见 CLAUDE.md。
