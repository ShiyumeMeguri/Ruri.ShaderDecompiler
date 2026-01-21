namespace Ruri.ShaderDecompiler.Spirv;

/// <summary>
/// SPIR-V OpCodes needed for parsing and patching.
/// </summary>
public static class SpvOpCode
{
    // Header constants
    public const uint MagicNumber = 0x07230203;
    public const int HeaderWordCount = 5; // Magic, Version, Generator, Bound, Reserved

    // Instruction OpCodes (subset needed for patching)
    public const ushort OpNop = 0;
    public const ushort OpName = 5;
    public const ushort OpMemberName = 6;
    public const ushort OpExtInstImport = 11;
    public const ushort OpMemoryModel = 14;
    public const ushort OpEntryPoint = 15;
    public const ushort OpExecutionMode = 16;
    public const ushort OpCapability = 17;
    public const ushort OpTypeVoid = 19;
    public const ushort OpTypeBool = 20;
    public const ushort OpTypeInt = 21;
    public const ushort OpTypeFloat = 22;
    public const ushort OpTypeVector = 23;
    public const ushort OpTypeMatrix = 24;
    public const ushort OpTypeImage = 25;
    public const ushort OpTypeSampler = 26;
    public const ushort OpTypeSampledImage = 27;
    public const ushort OpTypeArray = 28;
    public const ushort OpTypeRuntimeArray = 29;
    public const ushort OpTypeStruct = 30;
    public const ushort OpTypeOpaque = 31;
    public const ushort OpTypePointer = 32;
    public const ushort OpConstant = 43;
    public const ushort OpVariable = 59;
    public const ushort OpDecorate = 71;
    public const ushort OpMemberDecorate = 72;

    // Decoration values
    public const uint DecorationBinding = 33;
    public const uint DecorationDescriptorSet = 34;
    public const uint DecorationLocation = 30;

    /// <summary>
    /// Extracts OpCode from an instruction word.
    /// </summary>
    public static ushort GetOpCode(uint instructionWord) => (ushort)(instructionWord & 0xFFFF);

    /// <summary>
    /// Extracts word count from an instruction word.
    /// </summary>
    public static ushort GetWordCount(uint instructionWord) => (ushort)(instructionWord >> 16);

    /// <summary>
    /// Creates an instruction word from opcode and word count.
    /// </summary>
    public static uint MakeInstructionWord(ushort opCode, ushort wordCount) =>
        (uint)opCode | ((uint)wordCount << 16);
}
