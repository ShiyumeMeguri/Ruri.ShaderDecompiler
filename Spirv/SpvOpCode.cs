namespace Ruri.ShaderTools.Spirv;

/// <summary>
/// SPIR-V binary instruction encoding: the opcode numbers this toolchain
/// touches, plus the word-header pack/unpack helpers.
///
/// Scope is deliberately just "opcodes and instruction words". Decoration ids
/// and storage classes are different SPIR-V concepts and live in
/// <see cref="Decoration"/> / <see cref="StorageClass"/>; module-header
/// constants live on <see cref="SpirvModule"/>.
/// </summary>
internal static class SpvOpCode
{
    // --- miscellaneous / debug -------------------------------------------
    public const ushort OpNop = 0;
    public const ushort OpUndef = 1;
    public const ushort OpSourceContinued = 2;
    public const ushort OpSource = 3;
    public const ushort OpSourceExtension = 4;
    public const ushort OpName = 5;
    public const ushort OpMemberName = 6;
    public const ushort OpString = 7;
    public const ushort OpLine = 8;
    public const ushort OpExtension = 10;
    public const ushort OpExtInstImport = 11;
    public const ushort OpExtInst = 12;
    public const ushort OpMemoryModel = 14;
    public const ushort OpEntryPoint = 15;
    public const ushort OpExecutionMode = 16;
    public const ushort OpCapability = 17;

    // --- types (contiguous range: see IsTypeDeclaration) ------------------
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

    // --- constants --------------------------------------------------------
    public const ushort OpConstantTrue = 41;
    public const ushort OpConstantFalse = 42;
    public const ushort OpConstant = 43;
    public const ushort OpConstantComposite = 44;
    public const ushort OpConstantSampler = 45;
    public const ushort OpConstantNull = 46;
    public const ushort OpSpecConstantTrue = 48;
    public const ushort OpSpecConstantFalse = 49;
    public const ushort OpSpecConstant = 50;

    // --- memory -----------------------------------------------------------
    public const ushort OpVariable = 59;
    public const ushort OpLoad = 61;
    public const ushort OpStore = 62;
    public const ushort OpAccessChain = 65;
    public const ushort OpInBoundsAccessChain = 66;

    // --- annotation -------------------------------------------------------
    public const ushort OpDecorate = 71;
    public const ushort OpMemberDecorate = 72;
    public const ushort OpDecorationGroup = 73;
    public const ushort OpGroupDecorate = 74;
    public const ushort OpGroupMemberDecorate = 75;

    // --- composite --------------------------------------------------------
    public const ushort OpVectorShuffle = 79;
    public const ushort OpCompositeExtract = 81;
    public const ushort OpCompositeInsert = 82;

    // --- image ------------------------------------------------------------
    public const ushort OpSampledImage = 86;

    // --- conversion / arithmetic / bit ------------------------------------
    public const ushort OpBitcast = 124;
    public const ushort OpIAdd = 128;
    public const ushort OpISub = 130;
    public const ushort OpIMul = 132;
    public const ushort OpShiftRightLogical = 194;
    public const ushort OpShiftLeftLogical = 196;
    public const ushort OpBitwiseOr = 197;
    public const ushort OpBitwiseAnd = 199;

    // --- control flow -----------------------------------------------------
    public const ushort OpPhi = 245;
    public const ushort OpBranchConditional = 250;
    public const ushort OpSwitch = 251;

    // --- extended debug ---------------------------------------------------
    public const ushort OpNoLine = 317;
    public const ushort OpModuleProcessed = 330;
    public const ushort OpExecutionModeId = 331;

    /// <summary>
    /// True for the contiguous <c>OpTypeVoid..OpTypePointer</c> block. The
    /// module's type-section anchors key on this range, which is why the
    /// aggregate/opaque type opcodes above must stay in numeric order.
    /// </summary>
    public static bool IsTypeDeclaration(ushort opCode) => opCode >= OpTypeVoid && opCode <= OpTypePointer;

    /// <summary>Low 16 bits of an instruction's first word.</summary>
    public static ushort GetOpCode(uint instructionWord) => (ushort)(instructionWord & 0xFFFF);

    /// <summary>High 16 bits of an instruction's first word.</summary>
    public static ushort GetWordCount(uint instructionWord) => (ushort)(instructionWord >> 16);

    public static uint MakeInstructionWord(ushort opCode, ushort wordCount) => opCode | ((uint)wordCount << 16);
}
