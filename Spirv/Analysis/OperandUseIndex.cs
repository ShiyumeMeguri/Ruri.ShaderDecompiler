namespace Ruri.ShaderTools.Spirv.Analysis;

// id → the instructions that mention it in an operand slot, in module order.
//
// Stored as a CSR (compressed-sparse-row) triple rather than a
// Dictionary&lt;uint, List&lt;…&gt;&gt;: three allocations for the whole module instead of
// one List per referenced id. Iterating a use set is a contiguous span walk.
//
// Why it exists: the composite-extract fallback used to answer "which OpLoad
// reads this access chain / which OpCompositeExtract reads this load" by walking
// the entire instruction list, nested two deep, once per candidate access chain.
// That is O(instructions²) per chain and was the dominant cost on shaders with
// large constant buffers. Here it is O(uses).
//
// Order guarantee: users are appended during a single forward walk, so
// `UsersOf` yields them in module order — the same order the old nested scans
// visited them in, which keeps early-exit behaviour identical.
//
// Lifetime: a snapshot, like <see cref="ResultIdTable"/>. Build it in the stage
// that queries it, before that stage mutates anything it will query.
internal sealed class OperandUseIndex
{
    private readonly int[] _offsets;              // per id, start into _users
    private readonly int[] _counts;               // per id, run length
    private readonly SpirvInstruction[] _users;

    private OperandUseIndex(int[] offsets, int[] counts, SpirvInstruction[] users)
    {
        _offsets = offsets;
        _counts = counts;
        _users = users;
    }

    public static OperandUseIndex Build(SpirvModule module)
    {
        int bound = module.IdBound;
        List<SpirvInstruction> instructions = module.Instructions;

        // Pass 1 — count. A slot mentioning an id at or past the module bound is
        // a literal (component index, decoration argument, constant payload…),
        // never a reference, so it is dropped here exactly as a query for it
        // would have found nothing.
        var counts = new int[bound];
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            if (instruction.OpCode == SpvOpCode.OpNop)
            {
                continue;
            }

            Span<uint> words = instruction.Words;
            uint previous = uint.MaxValue;
            for (int slot = 1; slot < words.Length; slot++)
            {
                uint id = words[slot];
                if (id == previous || id >= (uint)bound)
                {
                    continue;   // collapse repeats: one entry per (id, instruction)
                }
                previous = id;
                counts[id]++;
            }
        }

        // Prefix-sum into offsets.
        var offsets = new int[bound];
        int total = 0;
        for (int id = 0; id < bound; id++)
        {
            offsets[id] = total;
            total += counts[id];
        }

        // Pass 2 — fill. `fill` tracks how many users each id has taken so far.
        var users = new SpirvInstruction[total];
        var fill = new int[bound];
        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            if (instruction.OpCode == SpvOpCode.OpNop)
            {
                continue;
            }

            Span<uint> words = instruction.Words;
            uint previous = uint.MaxValue;
            for (int slot = 1; slot < words.Length; slot++)
            {
                uint id = words[slot];
                if (id == previous || id >= (uint)bound)
                {
                    continue;
                }
                previous = id;
                users[offsets[id] + fill[id]++] = instruction;
            }
        }

        return new OperandUseIndex(offsets, counts, users);
    }

    public ReadOnlySpan<SpirvInstruction> UsersOf(uint id)
        => id < (uint)_counts.Length
            ? _users.AsSpan(_offsets[id], _counts[id])
            : ReadOnlySpan<SpirvInstruction>.Empty;
}
