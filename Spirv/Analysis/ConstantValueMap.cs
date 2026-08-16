namespace Ruri.ShaderTools.Spirv.Analysis;

// Constant literal ↔ result id, both directions.
//
// The access translator speaks in plain integers ("member 3, column 1,
// component 2") and has to turn each one back into the id of a constant the
// module actually declares. This map is that bridge.
//
// Two asymmetric rules, both preserved verbatim because the id they select ends
// up inside emitted access chains:
//
//   * <see cref="ValueToId"/> is keyed by VALUE ALONE, not (type, value). A
//     float whose bit pattern is 4 and a uint 4 collide, and the LAST one
//     declared wins. Downstream this only ever feeds integer index slots, and
//     the materialisation stage overwrites every index it needs with a genuine
//     uint constant, so the collision is benign — but it is observable, so it
//     stays.
//   * <c>OpConstantNull</c> contributes value 0 only if nothing has claimed 0
//     yet (first wins), while <c>OpConstant</c> overwrites (last wins).
internal sealed class ConstantValueMap
{
    /// <summary>Constant result id → its literal value.</summary>
    public Dictionary<uint, uint> IdToValue { get; } = new();

    /// <summary>Literal value → a constant result id carrying it.</summary>
    public Dictionary<uint, uint> ValueToId { get; } = new();

    public static ConstantValueMap Build(SpirvModule module)
    {
        var map = new ConstantValueMap();
        List<SpirvInstruction> instructions = module.Instructions;

        for (int i = 0; i < instructions.Count; i++)
        {
            SpirvInstruction instruction = instructions[i];
            Span<uint> words = instruction.Words;

            if (instruction.OpCode == SpvOpCode.OpConstant && words.Length >= 4)
            {
                map.IdToValue[words[2]] = words[3];
                map.ValueToId[words[3]] = words[2];
            }
            else if (instruction.OpCode == SpvOpCode.OpConstantNull && words.Length >= 3)
            {
                map.IdToValue[words[2]] = 0;
                map.ValueToId.TryAdd(0, words[2]);
            }
        }

        return map;
    }

    /// <summary>Record a constant this pipeline just materialised.</summary>
    public void Register(uint constantId, uint value)
    {
        IdToValue[constantId] = value;
        ValueToId[value] = constantId;
    }

    public bool TryGetId(uint value, out uint constantId) => ValueToId.TryGetValue(value, out constantId);

    public bool TryGetValue(uint constantId, out uint value) => IdToValue.TryGetValue(constantId, out value);
}
