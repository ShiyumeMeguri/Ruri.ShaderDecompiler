namespace Ruri.ShaderTools.Spirv.Analysis;

// id → defining instruction, as a flat array indexed by result id.
//
// Replaces the "walk every instruction looking for the one whose result id is
// N" scans that were sprinkled through the transforms (dead-code pruning,
// bitcast resolution, constant lookup). Those were O(instructions) per lookup
// and ran inside per-chain loops, which is where the quadratic behaviour on
// large shaders came from.
//
// Lifetime: a snapshot. Build it inside the stage that queries it. Instructions
// inserted afterwards are not indexed — deliberate, so a stale table can never
// silently answer for something it never saw. Every stage that both mutates and
// queries builds the table before its mutation loop and only ever looks up ids
// that predate it.
internal sealed class ResultIdTable
{
    private readonly SpirvInstruction?[] _definitions;

    private ResultIdTable(SpirvInstruction?[] definitions) => _definitions = definitions;

    public static ResultIdTable Build(SpirvModule module)
    {
        // The module bound is the exclusive upper limit for every legal result
        // id. Ids at or past it cannot be defined, so the array is exact.
        var definitions = new SpirvInstruction?[module.IdBound];

        List<SpirvInstruction> instructions = module.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            if (instruction.OpCode == SpvOpCode.OpNop)
            {
                continue;
            }

            int? resultIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
            if (!resultIndex.HasValue)
            {
                continue;
            }

            uint id = instruction[resultIndex.Value];
            if (id < (uint)definitions.Length)
            {
                definitions[id] = instruction;
            }
        }

        return new ResultIdTable(definitions);
    }

    public SpirvInstruction? DefinitionOf(uint id)
        => id < (uint)_definitions.Length ? _definitions[id] : null;

    public bool TryGetDefinition(uint id, out SpirvInstruction definition)
    {
        SpirvInstruction? found = DefinitionOf(id);
        definition = found!;
        return found is not null;
    }
}
