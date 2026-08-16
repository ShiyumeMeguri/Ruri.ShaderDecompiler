using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Parse the module and build every analysis snapshot the run will need:
/// structural shape, constant value map, id→definition table, and the type
/// interner.
///
/// This is the ONLY stage allowed to walk the module for general facts. If a
/// later stage needs a new structural fact, teach <see cref="ModuleShape"/> to
/// record it here — do not re-walk downstream. Everything after this point is
/// either an O(1) lookup or a single targeted pass.
/// </summary>
internal static class ModuleShapeScanStage
{
    public static void Run(StructuringContext context)
    {
        context.Module = SpirvModule.Parse(context.InputSpirv);
        context.Shape = ModuleShape.Build(context.Module);
        context.Constants = ConstantValueMap.Build(context.Module);
        context.Definitions = ResultIdTable.Build(context.Module);
        context.Types = new SpirvTypeInterner(context.Module);

        context.Note($"Metadata resources={context.Symbols.GetResourceBindingCount()}, constantBuffers={context.Symbols.ConstantBufferParameters.Count}");
        context.Note(
            $"Analyzed decoratedIds={context.Shape.SetBindingById.Count}, " +
            $"variables={context.Shape.VariablePointerTypes.Count}, " +
            $"pointers={context.Shape.PointerTypes.Count}, " +
            $"structs={context.Shape.StructMembers.Count}, " +
            $"arrays={context.Shape.ArrayTypes.Count}");
    }
}
