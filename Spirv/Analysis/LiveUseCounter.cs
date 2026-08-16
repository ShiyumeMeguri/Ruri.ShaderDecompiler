namespace Ruri.ShaderTools.Spirv.Analysis;

// "Is anything still consuming this id?" — answered in O(1) from a reference
// count, and kept correct as instructions are retired.
//
// Why a counter and not a use-count map over raw words: several SPIR-V ops
// carry LITERAL values in operand slots — OpConstant's payload, OpExtInst's
// instruction enum, OpCompositeExtract's component indices, OpTypeInt's bit
// width, OpDecorate's arguments. When such a literal happens to equal a real
// SSA id, a naive counter inflates that id's use count and a genuinely dead
// instruction survives the prune. Surviving dead access chains are fatal here:
// their index sequences address the OLD flat-array layout, and spirv-cross
// rejects the module with "Cannot subdivide a scalar value".
//
// So the slot rules below are load-bearing, not defensive. They are a verbatim
// port of the structural scanner they replace; the only change is that the
// answer is precomputed once instead of rescanning the whole module per query.
//
// Maintenance: <see cref="Retire"/> is the single mutation entry point. It
// removes the instruction's contributions and collapses it to OpNop, which is
// exactly the "skip OpNop instructions" clause of the original scan.
internal sealed class LiveUseCounter
{
    private readonly int[] _counts;

    private LiveUseCounter(int[] counts) => _counts = counts;

    public static LiveUseCounter Build(SpirvModule module)
    {
        var counts = new int[module.IdBound];

        List<SpirvInstruction> instructions = module.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            Accumulate(counts, instructions[i], delta: 1);
        }

        return new LiveUseCounter(counts);
    }

    public bool HasLiveConsumer(uint id) => id < (uint)_counts.Length && _counts[id] > 0;

    /// Drop <paramref name="instruction"/> from the live graph and collapse it
    /// to OpNop. Callers prune in cascades (bitcasts, then loads, then access
    /// chains); each retirement immediately frees whatever it was holding alive,
    /// so the next cascade sees the updated answer without a rescan.
    public void Retire(SpirvInstruction instruction)
    {
        if (instruction.OpCode == SpvOpCode.OpNop)
        {
            return;
        }

        Accumulate(_counts, instruction, delta: -1);
        instruction.MakeNop();
    }

    private static void Accumulate(int[] counts, SpirvInstruction instruction, int delta)
    {
        ushort opCode = instruction.OpCode;
        if (opCode == SpvOpCode.OpNop || IsLiteralBearingMetadataOp(opCode) || IsLiteralValueConstantOp(opCode))
        {
            return;
        }

        int? resultIdIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
        int? resultTypeIndex = SpvInstructionTraits.GetResultTypeIdIndex(instruction);

        Span<uint> words = instruction.Words;
        for (int slot = 1; slot < words.Length; slot++)
        {
            if ((resultIdIndex.HasValue && slot == resultIdIndex.Value)
                || (resultTypeIndex.HasValue && slot == resultTypeIndex.Value)
                || IsLiteralOperandSlot(opCode, slot))
            {
                continue;
            }

            uint id = words[slot];
            if (id < (uint)counts.Length)
            {
                counts[id] += delta;
            }
        }
    }

    // Names, decorations, source/string blocks, entry points, execution modes.
    // None of these participate in data flow, so an id appearing inside one does
    // not keep the definition alive.
    private static bool IsLiteralBearingMetadataOp(ushort opCode)
    {
        return opCode == SpvOpCode.OpName                   // 5
            || opCode == SpvOpCode.OpMemberName             // 6
            || opCode == SpvOpCode.OpDecorate               // 71
            || opCode == SpvOpCode.OpMemberDecorate         // 72
            || opCode == SpvOpCode.OpDecorationGroup        // 73
            || opCode == SpvOpCode.OpGroupDecorate          // 74
            || opCode == SpvOpCode.OpGroupMemberDecorate    // 75
            || opCode == SpvOpCode.OpString                 // 7
            || opCode == SpvOpCode.OpSource                 // 3
            || opCode == SpvOpCode.OpSourceContinued        // 2
            || opCode == SpvOpCode.OpSourceExtension        // 4
            || opCode == SpvOpCode.OpModuleProcessed        // 330
            || opCode == SpvOpCode.OpLine                   // 8
            || opCode == SpvOpCode.OpNoLine                 // 317
            || opCode == SpvOpCode.OpExecutionMode          // 16
            || opCode == SpvOpCode.OpExecutionModeId        // 331
            || opCode == SpvOpCode.OpEntryPoint             // 15
            || opCode == SpvOpCode.OpCapability             // 17
            || opCode == SpvOpCode.OpExtension              // 10
            || opCode == SpvOpCode.OpExtInstImport          // 11
            || opCode == SpvOpCode.OpMemoryModel;           // 14
    }

    // Constant definitions whose every post-result word is a literal payload.
    // OpConstantComposite / OpSpecConstantComposite are deliberately NOT here —
    // their constituents are real id references and real data-flow edges.
    private static bool IsLiteralValueConstantOp(ushort opCode)
    {
        return opCode == SpvOpCode.OpConstantTrue        // 41
            || opCode == SpvOpCode.OpConstantFalse       // 42
            || opCode == SpvOpCode.OpConstant            // 43
            || opCode == SpvOpCode.OpConstantSampler     // 45
            || opCode == SpvOpCode.OpConstantNull        // 46
            || opCode == SpvOpCode.OpSpecConstantTrue    // 48
            || opCode == SpvOpCode.OpSpecConstantFalse   // 49
            || opCode == SpvOpCode.OpSpecConstant;       // 50
    }

    // Literal slots inside ops that otherwise mix ids and literals. The
    // whole-instruction lists above cover pure metadata; this covers the
    // data-flow ops where only SOME post-result slots are literal.
    //
    // Layouts cross-checked against the SPIR-V 1.x core grammar.
    private static bool IsLiteralOperandSlot(ushort opCode, int slot)
    {
        return opCode switch
        {
            // OpExtInst: [h, type, result, set-id, instruction-enum-lit, ids…]
            SpvOpCode.OpExtInst => slot == 4,

            // OpVectorShuffle: [h, type, result, v1, v2, comp-lits…]
            SpvOpCode.OpVectorShuffle => slot >= 5,

            // OpCompositeExtract: [h, type, result, composite, idx-lits…]
            SpvOpCode.OpCompositeExtract => slot >= 4,

            // OpCompositeInsert: [h, type, result, value, composite, idx-lits…]
            SpvOpCode.OpCompositeInsert => slot >= 5,

            // ⚠ PRESERVED QUIRK — behaviour-compatible, deliberately not "fixed".
            // This entry is labelled OpSwitch in the original scanner but the
            // number it matches is 250 = OpBranchConditional
            // ([h, condition, true-lbl, false-lbl, weight-lits…]), not 251.
            // Correcting it would change which ids count as live and therefore
            // which instructions the prune retires — an output change, which the
            // byte-equivalence contract forbids. It is provably inert either way:
            // the prune only ever asks about pointer-typed access-chain / load /
            // bitcast results, which can never appear as a branch label or a
            // switch case literal. Fix it behind a fixture diff, not here.
            SpvOpCode.OpBranchConditional => slot >= 3,

            // OpTypeInt: [h, result, width-lit, signedness-lit]. A 64-bit type's
            // width literal collides with any SSA id that happens to be 64 —
            // this exact collision once kept a dead access-chain chain alive.
            SpvOpCode.OpTypeInt => slot is 2 or 3,

            // OpTypeFloat: [h, result, width-lit, (encoding-lit)]
            SpvOpCode.OpTypeFloat => slot >= 2,

            // OpTypeVector / OpTypeMatrix: [h, result, component-type-id, count-lit]
            SpvOpCode.OpTypeVector => slot == 3,
            SpvOpCode.OpTypeMatrix => slot == 3,

            // OpTypePointer: [h, result, storage-class-lit, pointee-type-id]
            SpvOpCode.OpTypePointer => slot == 2,

            // OpTypeImage: [h, result, sampled-type-id, Dim, Depth, Arrayed, MS,
            //               Sampled, Format, (AccessQualifier)] — all literal
            // enums from slot 3 on.
            SpvOpCode.OpTypeImage => slot >= 3,

            _ => false,
        };
    }
}
