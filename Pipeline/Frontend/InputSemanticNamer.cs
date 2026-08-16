using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Pipeline.Frontend;

/// <summary>
/// Names a vertex shader's SPIR-V input variables after the semantics recovered
/// from its input signature.
///
/// The join is <c>Location</c> ↔ signature <c>Register</c>: the translator
/// assigns one location per input register, so the two index the same thing.
///
/// Names are written in the form the downstream semantic mapper already
/// understands — <c>POSITION</c>, <c>NORMAL</c>, <c>TEXCOORD_3</c> — rather than
/// inventing a parallel convention. That keeps a single place responsible for
/// deciding what an attribute is called, and means this stage only has to supply
/// the fact the bytecode lost.
///
/// VERTEX ONLY. A fragment shader's input signature describes interstage
/// varyings whose semantics are already just <c>TEXCOORD&lt;n&gt;</c>; renaming
/// those buys nothing and risks perturbing the vertex-output ↔ fragment-input
/// matching the emitter relies on.
/// </summary>
internal static class InputSemanticNamer
{
    /// <summary>SPIR-V execution model for a vertex shader.</summary>
    private const uint VertexExecutionModel = 0;

    /// <summary>
    /// Inject an <c>OpName</c> per located input variable. Returns the module
    /// unchanged when the shader is not a vertex shader, when no signature was
    /// recovered, or when nothing matched.
    /// </summary>
    public static byte[] Apply(byte[] spirv, IReadOnlyList<InputSignatureElement> signature)
    {
        if (signature.Count == 0)
        {
            return spirv;
        }

        SpirvModule module;
        try
        {
            module = SpirvModule.Parse(spirv);
        }
        catch
        {
            return spirv;
        }

        if (!IsVertexShader(module))
        {
            return spirv;
        }

        Dictionary<uint, string> nameByRegister = BuildNameIndex(signature);
        if (nameByRegister.Count == 0)
        {
            return spirv;
        }

        // Location decorations and the variables they sit on are collected in one
        // pass; a variable is only renamed when it is BOTH an Input and carries a
        // location the signature knows.
        var locationById = new Dictionary<uint, uint>();
        var inputVariables = new List<uint>();

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpDecorate
                && instruction.WordCount >= 4
                && instruction[2] == Decoration.Location)
            {
                locationById[instruction[1]] = instruction[3];
            }
            else if (instruction.OpCode == SpvOpCode.OpVariable
                     && instruction.WordCount >= 4
                     && instruction[3] == StorageClass.Input)
            {
                inputVariables.Add(instruction[2]);
            }
        }

        var renames = new List<(uint Id, string Name)>();
        foreach (uint variableId in inputVariables)
        {
            if (locationById.TryGetValue(variableId, out uint location)
                && nameByRegister.TryGetValue(location, out string? name))
            {
                renames.Add((variableId, name));
            }
        }

        if (renames.Count == 0)
        {
            return spirv;
        }

        return Rename(module, renames);
    }

    private static bool IsVertexShader(SpirvModule module)
    {
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpEntryPoint && instruction.WordCount >= 3)
            {
                return instruction[1] == VertexExecutionModel;
            }
        }
        return false;
    }

    /// <summary>
    /// Register → the name the semantic mapper expects.
    ///
    /// Index 0 is spelled bare (<c>TEXCOORD</c>, <c>COLOR</c>) and higher indices
    /// carry an underscore (<c>TEXCOORD_3</c>) — the exact convention the mapper
    /// keys on. A system-value input such as <c>SV_InstanceID</c> is skipped: the
    /// emitter already gives those their real built-in semantic, and renaming one
    /// would only confuse it.
    /// </summary>
    private static Dictionary<uint, string> BuildNameIndex(IReadOnlyList<InputSignatureElement> signature)
    {
        var nameByRegister = new Dictionary<uint, string>(signature.Count);

        foreach (InputSignatureElement element in signature)
        {
            if (string.IsNullOrEmpty(element.SemanticName)
                || element.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = element.SemanticIndex == 0
                ? element.SemanticName
                : $"{element.SemanticName}_{element.SemanticIndex}";

            nameByRegister.TryAdd(element.Register, name);
        }

        return nameByRegister;
    }

    // Drop whatever debug name the variable already had, then splice the new one
    // in — the same replace-don't-append rule symbol injection follows, and for
    // the same reason: an emitter is free to prefer the original otherwise.
    private static byte[] Rename(SpirvModule module, List<(uint Id, string Name)> renames)
    {
        var replaced = new HashSet<uint>(renames.Count);
        foreach ((uint id, _) in renames)
        {
            replaced.Add(id);
        }

        module.Instructions.RemoveAll(instruction =>
            instruction.OpCode == SpvOpCode.OpName
            && instruction.WordCount >= 2
            && replaced.Contains(instruction[1]));

        foreach ((uint id, string name) in renames)
        {
            module.InsertDebugName(id, name);
        }

        return module.ToBytes();
    }
}
