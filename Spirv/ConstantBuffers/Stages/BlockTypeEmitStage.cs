using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Reserves the new block-struct / pointer ids, then emits the types, their
/// decorations and the debug names that drive source emission.
///
/// Reservation and emission are one stage but two steps, and the order matters:
/// the decorations emitted in step two reference the struct id by value, so
/// every id must exist before any instruction is written.
///
/// Naming uses two DIFFERENT strings for the struct type and the variable —
/// <c>type.&lt;Name&gt;</c> versus a placeholder the symbol injector later
/// overwrites with the bare name. Sharing one string would collide inside the
/// emitter's name-uniquify pass, which appends a <c>_1</c> suffix that then
/// bleeds into every member name (<c>Buffer_1_MainTex_ST</c>). Keeping them
/// distinct is the whole reason the placeholder exists.
///
/// The <c>(set, binding) → name</c> record is written here rather than at the
/// end so symbol injection can name a binding even when its rewrite was later
/// abandoned.
/// </summary>
internal static class BlockTypeEmitStage
{
    public static void Run(StructuringContext context)
    {
        foreach (BlockRewritePlan plan in context.Plans)
        {
            plan.NewStructTypeId = context.Module.AllocateId();
            plan.NewPointerTypeId = context.Module.AllocateId();

            context.ResolvedBlockNames[(plan.Block.Binding.Set, plan.Block.Binding.Binding)] = plan.Name;
            context.Note(plan, $"rewrite planned with {plan.Layout.Members.Count} members");
        }

        foreach (BlockRewritePlan plan in context.Plans)
        {
            EmitBlockType(context.Module, plan);
            EmitDebugNames(context.Module, plan);
        }
    }

    private static void EmitBlockType(SpirvModule module, BlockRewritePlan plan)
    {
        var decorations = new List<SpirvInstruction>(1 + (plan.Layout.Members.Count * 3))
        {
            module.CreateInstruction(SpvOpCode.OpDecorate,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 3),
                plan.NewStructTypeId, Decoration.Block,
            ]),
        };

        for (int memberIndex = 0; memberIndex < plan.Layout.Members.Count; memberIndex++)
        {
            BlockMemberLayout member = plan.Layout.Members[memberIndex];

            decorations.Add(module.CreateInstruction(SpvOpCode.OpMemberDecorate,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                plan.NewStructTypeId, (uint)memberIndex, Decoration.Offset, (uint)member.ByteOffset,
            ]));

            if (member.Shape.Kind != MemberShapeKind.Matrix)
            {
                continue;
            }

            decorations.Add(module.CreateInstruction(SpvOpCode.OpMemberDecorate,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 4),
                plan.NewStructTypeId, (uint)memberIndex, Decoration.RowMajor,
            ]));
            decorations.Add(module.CreateInstruction(SpvOpCode.OpMemberDecorate,
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                plan.NewStructTypeId, (uint)memberIndex, Decoration.MatrixStride, 16,
            ]));
        }

        module.PrependDecorations(decorations);

        int structWordCount = 2 + plan.MemberTypeIds.Count;
        Span<uint> structWords = structWordCount <= 512 ? stackalloc uint[structWordCount] : new uint[structWordCount];
        structWords[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)structWordCount);
        structWords[1] = plan.NewStructTypeId;
        for (int i = 0; i < plan.MemberTypeIds.Count; i++)
        {
            structWords[2 + i] = plan.MemberTypeIds[i];
        }
        module.AppendType(module.CreateInstruction(SpvOpCode.OpTypeStruct, structWords));

        module.AppendType(module.CreateInstruction(SpvOpCode.OpTypePointer,
        [
            SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4),
            plan.NewPointerTypeId, StorageClass.Uniform, plan.NewStructTypeId,
        ]));
    }

    private static void EmitDebugNames(SpirvModule module, BlockRewritePlan plan)
    {
        module.InsertDebugName(plan.NewStructTypeId, StructTypeAlias(plan.Name));
        module.InsertDebugName(plan.Block.VariableId, VariablePlaceholder(plan.Name));

        for (int memberIndex = 0; memberIndex < plan.Layout.Members.Count; memberIndex++)
        {
            string name = plan.Layout.Members[memberIndex].Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                module.InsertDebugMemberName(plan.NewStructTypeId, (uint)memberIndex, name);
            }
        }
    }

    /// <summary>Struct-type alias. The dot is sanitised to <c>_</c> on emit, so
    /// the block reads as <c>cbuffer type_&lt;Name&gt;</c>.</summary>
    public static string StructTypeAlias(string blockName) => "type." + blockName;

    /// <summary>Temporary variable name, overwritten by symbol injection with the
    /// bare block name. Must never equal <see cref="StructTypeAlias"/>.</summary>
    public static string VariablePlaceholder(string blockName) => $"__ruri_{blockName}_var";
}
