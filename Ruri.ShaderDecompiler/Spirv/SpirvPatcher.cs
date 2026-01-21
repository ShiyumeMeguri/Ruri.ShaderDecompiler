using System.Text;

namespace Ruri.ShaderDecompiler.Spirv;

/// <summary>
/// Detailed binding information for a SPIR-V variable.
/// </summary>
public class SpirvBindingInfo
{
    public uint Id { get; set; }
    public int Set { get; set; }
    public int Binding { get; set; }
    public string? DescriptorType { get; set; }
    public uint? StructTypeId { get; set; }
}

/// <summary>
/// Patches SPIR-V binaries to inject symbol names.
/// </summary>
public class SpirvPatcher
{
    /// <summary>
    /// Analyzes a SPIR-V binary and returns detailed binding information.
    /// </summary>
    public List<SpirvBindingInfo> AnalyzeBindingsDetailed(byte[] spirvBytes)
    {
        uint[] words = BytesToWords(spirvBytes);
        
        var idToSetBinding = new Dictionary<uint, (int? Set, int? Binding)>();
        var typePointerMap = new Dictionary<uint, (uint StorageClass, uint PointedType)>();
        var variableTypeMap = new Dictionary<uint, uint>();
        var structTypeIds = new HashSet<uint>();
        var imageTypeIds = new HashSet<uint>();
        var samplerTypeIds = new HashSet<uint>();
        var sampledImageTypeIds = new HashSet<uint>();

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint instrWord = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(instrWord);
            ushort wordCount = SpvOpCode.GetWordCount(instrWord);
            if (wordCount == 0) break;

            switch (opCode)
            {
                case SpvOpCode.OpDecorate when wordCount >= 4:
                    {
                        uint targetId = words[offset + 1];
                        uint decoration = words[offset + 2];
                        if (decoration == SpvOpCode.DecorationDescriptorSet)
                        {
                            int set = (int)words[offset + 3];
                            if (!idToSetBinding.ContainsKey(targetId))
                                idToSetBinding[targetId] = (set, null);
                            else
                                idToSetBinding[targetId] = (set, idToSetBinding[targetId].Binding);
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            int binding = (int)words[offset + 3];
                            if (!idToSetBinding.ContainsKey(targetId))
                                idToSetBinding[targetId] = (null, binding);
                            else
                                idToSetBinding[targetId] = (idToSetBinding[targetId].Set, binding);
                        }
                        break;
                    }
                case SpvOpCode.OpTypePointer when wordCount >= 4:
                    {
                        uint resultId = words[offset + 1];
                        uint storageClass = words[offset + 2];
                        uint pointedTypeId = words[offset + 3];
                        typePointerMap[resultId] = (storageClass, pointedTypeId);
                        break;
                    }
                case SpvOpCode.OpTypeStruct:
                    structTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpTypeImage:
                    imageTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpTypeSampler:
                    samplerTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpTypeSampledImage:
                    sampledImageTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpVariable when wordCount >= 4:
                    {
                        uint pointerTypeId = words[offset + 1];
                        uint resultId = words[offset + 2];
                        variableTypeMap[resultId] = pointerTypeId;
                        break;
                    }
            }

            offset += wordCount;
        }

        var result = new List<SpirvBindingInfo>();
        
        foreach (var kvp in idToSetBinding)
        {
            if (!kvp.Value.Set.HasValue || !kvp.Value.Binding.HasValue)
                continue;

            var info = new SpirvBindingInfo
            {
                Id = kvp.Key,
                Set = kvp.Value.Set.Value,
                Binding = kvp.Value.Binding.Value
            };

            // Determine type
            if (variableTypeMap.TryGetValue(kvp.Key, out uint ptrTypeId))
            {
                if (typePointerMap.TryGetValue(ptrTypeId, out var ptrInfo))
                {
                    uint pointedType = ptrInfo.PointedType;
                    
                    if (structTypeIds.Contains(pointedType))
                    {
                        info.DescriptorType = ptrInfo.StorageClass == 2 ? "UniformBuffer" : "StorageBuffer";
                        info.StructTypeId = pointedType;
                    }
                    else if (samplerTypeIds.Contains(pointedType))
                        info.DescriptorType = "Sampler";
                    else if (sampledImageTypeIds.Contains(pointedType))
                        info.DescriptorType = "SampledImage";
                    else if (imageTypeIds.Contains(pointedType))
                        info.DescriptorType = "StorageImage";
                    else
                        info.DescriptorType = "Unknown";
                }
            }

            result.Add(info);
        }

        return result.OrderBy(x => x.Set).ThenBy(x => x.Binding).ToList();
    }

    /// <summary>
    /// Patches SPIR-V by injecting OpName instructions directly using SPIR-V IDs from ResourceBinding.Tag.
    /// </summary>
    public byte[] PatchByIds(byte[] spirvBytes, List<(uint Id, string Name)> names)
    {
        if (spirvBytes.Length < SpvOpCode.HeaderWordCount * 4)
            throw new ArgumentException("Invalid SPIR-V binary");

        uint[] words = BytesToWords(spirvBytes);
        if (words[0] != SpvOpCode.MagicNumber)
            throw new ArgumentException("Invalid SPIR-V magic");

        var instructions = new List<uint[]>();
        foreach (var (id, name) in names)
        {
            instructions.Add(CreateOpName(id, name));
        }

        if (instructions.Count == 0)
            return spirvBytes;

        int insertOffset = FindDebugInsertionPoint(words);
        int additionalWords = instructions.Sum(arr => arr.Length);
        uint[] newWords = new uint[words.Length + additionalWords];

        Array.Copy(words, 0, newWords, 0, insertOffset);

        int writeOffset = insertOffset;
        foreach (var instr in instructions)
        {
            Array.Copy(instr, 0, newWords, writeOffset, instr.Length);
            writeOffset += instr.Length;
        }

        Array.Copy(words, insertOffset, newWords, writeOffset, words.Length - insertOffset);

        return WordsToBytes(newWords);
    }

    /// <summary>
    /// Legacy method for compatibility.
    /// </summary>
    public byte[] Patch(byte[] spirvBytes, ShaderSymbolData symbols)
    {
        var names = symbols.Resources
            .Where(r => r.Tag > 0)
            .Select(r => ((uint)r.Tag, r.Name))
            .ToList();
        
        if (names.Count > 0)
            return PatchByIds(spirvBytes, names);

        // Fallback to old behavior if Tags not set
        return spirvBytes;
    }

    public Dictionary<(int Set, int Binding), uint> AnalyzeBindings(byte[] spirvBytes)
    {
        var detailed = AnalyzeBindingsDetailed(spirvBytes);
        var result = new Dictionary<(int Set, int Binding), uint>();
        foreach (var b in detailed)
        {
            var key = (b.Set, b.Binding);
            if (!result.ContainsKey(key))
                result[key] = b.Id;
        }
        return result;
    }

    private int FindDebugInsertionPoint(uint[] words)
    {
        int offset = SpvOpCode.HeaderWordCount;
        int lastDebugEnd = SpvOpCode.HeaderWordCount;

        while (offset < words.Length)
        {
            uint instrWord = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(instrWord);
            ushort wordCount = SpvOpCode.GetWordCount(instrWord);
            if (wordCount == 0) break;

            if (opCode >= 3 && opCode <= 10)
                lastDebugEnd = offset + wordCount;
            else if (opCode == SpvOpCode.OpDecorate || opCode >= SpvOpCode.OpTypeVoid)
                break;

            offset += wordCount;
        }

        return lastDebugEnd;
    }

    private uint[] CreateOpName(uint id, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int paddedLength = (nameBytes.Length + 1 + 3) / 4 * 4;
        byte[] paddedName = new byte[paddedLength];
        Array.Copy(nameBytes, paddedName, nameBytes.Length);

        int wordCount = 2 + paddedLength / 4;
        uint[] instr = new uint[wordCount];

        instr[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpName, (ushort)wordCount);
        instr[1] = id;

        for (int i = 0; i < paddedLength / 4; i++)
            instr[2 + i] = BitConverter.ToUInt32(paddedName, i * 4);

        return instr;
    }

    private static uint[] BytesToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    private static byte[] WordsToBytes(uint[] words)
    {
        byte[] bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
