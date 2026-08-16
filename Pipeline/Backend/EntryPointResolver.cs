using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>Which entry point to emit, and what it is.</summary>
internal readonly record struct EntryPointSelection(ShaderStage Stage, string? Name, uint ExecutionModel);

/// <summary>
/// Picks the entry point to hand the source backend.
///
/// Prefers the name the engine symbols asked for; falls back to the module's
/// first entry point. The RAW execution model travels with the selection because
/// the backend needs it to disambiguate entry points — the friendly stage name is
/// not enough when a module carries several.
/// </summary>
internal static class EntryPointResolver
{
    public static EntryPointSelection Resolve(byte[] spirv, string? preferredName)
    {
        SpirvModule module = SpirvModule.Parse(spirv);
        EntryPointSelection? first = null;

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode != SpvOpCode.OpEntryPoint || instruction.WordCount < 3)
            {
                continue;
            }

            // OpEntryPoint: [header, execution-model, function-id, name…, interface-ids…]
            uint executionModel = instruction[1];
            string name = SpirvLiteral.ReadString(instruction.Words, 3);
            var candidate = new EntryPointSelection(ShaderStageClassifier.FromExecutionModel(executionModel), name, executionModel);

            first ??= candidate;

            if (!string.IsNullOrWhiteSpace(preferredName) && string.Equals(name, preferredName, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return first ?? new EntryPointSelection(ShaderStage.Unknown, preferredName, 0u);
    }
}
