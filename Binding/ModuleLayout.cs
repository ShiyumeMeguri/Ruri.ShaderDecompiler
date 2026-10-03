using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Binding;

/// <summary>One descriptor-bound block: where the engine binds it, its storage class and the struct that lays it out.</summary>
public readonly record struct ModuleBlock(int Set, int Binding, uint StorageClass, uint StructType);

/// <summary>
/// One member of a struct as the module lays it out. <see cref="ElementStruct"/> is the struct its type reaches through any
/// arrays, 0 when it reaches none; <see cref="Name"/> is the name the module already gives it, if any.
/// </summary>
public readonly record struct ModuleStructMember(uint Index, uint? Offset, uint Type, uint ElementStruct, string? Name);

/// <summary>
/// A module's buffer layout as a binder reads it: every descriptor-bound block and, for every struct, its members. Read-only,
/// built from one walk of the module.
/// </summary>
public sealed class ModuleLayout
{
    private readonly Dictionary<uint, ModuleStructMember[]> _members;

    private ModuleLayout(List<ModuleBlock> blocks, Dictionary<uint, ModuleStructMember[]> members)
    {
        Blocks = blocks;
        _members = members;
    }

    public IReadOnlyList<ModuleBlock> Blocks { get; }

    public IReadOnlyList<ModuleStructMember> MembersOf(uint structType)
        => _members.TryGetValue(structType, out ModuleStructMember[]? members) ? members : Array.Empty<ModuleStructMember>();

    internal static ModuleLayout Read(SpirvModule module)
    {
        var descriptorSet = new Dictionary<uint, int>();
        var binding = new Dictionary<uint, int>();
        var variables = new List<(uint Variable, uint PointerType, uint StorageClass)>();
        var pointee = new Dictionary<uint, uint>();
        var structMembers = new Dictionary<uint, uint[]>();
        var arrayElement = new Dictionary<uint, uint>();
        var offsets = new Dictionary<(uint Struct, uint Member), uint>();
        var names = new Dictionary<(uint Struct, uint Member), string>();

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            ReadOnlySpan<uint> words = instruction.Words;
            switch (instruction.OpCode)
            {
                case SpvOpCode.OpDecorate when words.Length >= 4 && words[2] == Decoration.DescriptorSet:
                    descriptorSet[words[1]] = (int)words[3];
                    break;
                case SpvOpCode.OpDecorate when words.Length >= 4 && words[2] == Decoration.Binding:
                    binding[words[1]] = (int)words[3];
                    break;
                case SpvOpCode.OpMemberDecorate when words.Length >= 5 && words[3] == Decoration.Offset:
                    offsets[(words[1], words[2])] = words[4];
                    break;
                case SpvOpCode.OpMemberName when words.Length >= 4:
                    string name = SpirvLiteral.ReadString(words, 3);
                    if (name.Length > 0)
                    {
                        names[(words[1], words[2])] = name;
                    }
                    break;
                case SpvOpCode.OpVariable when words.Length >= 4:
                    variables.Add((words[2], words[1], words[3]));
                    break;
                case SpvOpCode.OpTypePointer when words.Length >= 4:
                    pointee[words[1]] = words[3];
                    break;
                case SpvOpCode.OpTypeStruct when words.Length >= 2:
                    structMembers[words[1]] = words[2..].ToArray();
                    break;
                case SpvOpCode.OpTypeArray when words.Length >= 3:
                case SpvOpCode.OpTypeRuntimeArray when words.Length >= 3:
                    arrayElement[words[1]] = words[2];
                    break;
            }
        }

        uint StructReached(uint type)
        {
            while (!structMembers.ContainsKey(type))
            {
                if (!arrayElement.TryGetValue(type, out type))
                {
                    return 0;
                }
            }
            return type;
        }

        var blocks = new List<ModuleBlock>();
        foreach ((uint variable, uint pointerType, uint storageClass) in variables)
        {
            if (!descriptorSet.TryGetValue(variable, out int set)
                || !binding.TryGetValue(variable, out int slot)
                || !pointee.TryGetValue(pointerType, out uint pointed))
            {
                continue;
            }
            uint block = StructReached(pointed);
            if (block != 0)
            {
                blocks.Add(new ModuleBlock(set, slot, storageClass, block));
            }
        }
        blocks.Sort(static (left, right) => left.Set != right.Set ? left.Set.CompareTo(right.Set) : left.Binding.CompareTo(right.Binding));

        var members = new Dictionary<uint, ModuleStructMember[]>(structMembers.Count);
        foreach ((uint structType, uint[] memberTypes) in structMembers)
        {
            var laid = new ModuleStructMember[memberTypes.Length];
            for (uint index = 0; index < memberTypes.Length; index++)
            {
                laid[index] = new ModuleStructMember(
                    index,
                    offsets.TryGetValue((structType, index), out uint offset) ? offset : null,
                    memberTypes[index],
                    StructReached(memberTypes[index]),
                    names.GetValueOrDefault((structType, index)));
            }
            members[structType] = laid;
        }

        return new ModuleLayout(blocks, members);
    }
}
