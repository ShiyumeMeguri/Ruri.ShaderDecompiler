using System.Text;
using Ruri.ShaderTools.Pipeline.Naming;
using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.SymbolInjection;

namespace Ruri.ShaderTools.Pipeline.Diagnostics;

/// <summary>
/// Human-readable renderings of a module's state, written into failure dumps.
///
/// These exist so a failure is diagnosable OFFLINE, from the dump alone, without
/// re-running the shader. Both reports are cold-path only — they are built when
/// something has already gone wrong.
/// </summary>
internal static class ModuleReports
{
    /// <summary>How many individual patches to spell out before summarising.</summary>
    private const int MaxListedPatches = 16;

    /// <summary>
    /// What name injection WOULD have done: how many bindings matched, and the
    /// first few names it planned. Answers "did the symbols reach the module at
    /// all", which is the first question when output comes out unnamed.
    /// </summary>
    public static string DescribePatchPlan(byte[] spirv, SerializedProgramData symbols, Func<int, int, string?> resolveBlockName)
    {
        if (symbols.GetResourceBindingCount() == 0)
        {
            return "Patch plan: metadata contained no resource bindings.";
        }

        List<DescriptorBindingInfo> bindings = BindingScanner.Scan(spirv);
        List<NamePatch> names = new ResourceNamePlanner(resolveBlockName).Plan(bindings, symbols);
        List<MemberNamePatch> members = new BlockMemberNamePlanner(resolveBlockName).Plan(bindings, symbols);

        var report = new StringBuilder();
        report.Append("Patch plan: resourceBindings=").Append(symbols.GetResourceBindingCount())
              .Append(" matchedBindings=").Append(bindings.Count)
              .Append(" opNames=").Append(names.Count)
              .Append(" opMemberNames=").Append(members.Count);

        for (int i = 0; i < names.Count && i < MaxListedPatches; i++)
        {
            report.Append(Environment.NewLine)
                  .Append("  OpName Id=").Append(names[i].Id)
                  .Append(" Name=").Append(names[i].Name);
        }
        if (names.Count > MaxListedPatches)
        {
            report.Append(Environment.NewLine).Append("  ... ").Append(names.Count - MaxListedPatches).Append(" more OpName patches");
        }

        for (int i = 0; i < members.Count && i < MaxListedPatches; i++)
        {
            report.Append(Environment.NewLine)
                  .Append("  OpMemberName TypeId=").Append(members[i].StructTypeId)
                  .Append(" MemberIndex=").Append(members[i].MemberIndex)
                  .Append(" Name=").Append(members[i].Name);
        }
        if (members.Count > MaxListedPatches)
        {
            report.Append(Environment.NewLine).Append("  ... ").Append(members.Count - MaxListedPatches).Append(" more OpMemberName patches");
        }

        return report.ToString();
    }

    /// <summary>
    /// Every built-in decoration in the module, with the decorated id's name.
    ///
    /// Worth dumping because a whole class of emission failures is "the backend
    /// does not support built-in N" — tessellation and ray-tracing built-ins in
    /// particular — and that is a backend limitation, not a bug in any transform.
    /// Having the list makes the difference obvious immediately.
    /// </summary>
    public static string DescribeBuiltInDecorations(byte[] spirv)
    {
        SpirvModule module = SpirvModule.Parse(spirv);

        var namesById = new Dictionary<uint, string>();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpName && instruction.WordCount >= 3)
            {
                string name = SpirvLiteral.ReadString(instruction.Words, 2);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    namesById[instruction[1]] = name;
                }
            }
        }

        var lines = new List<string>();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpDecorate && instruction.WordCount >= 4 && instruction[2] == Decoration.BuiltIn)
            {
                uint targetId = instruction[1];
                string targetName = namesById.TryGetValue(targetId, out string? name) ? name : $"id_{targetId}";
                lines.Add($"  OpDecorate targetId={targetId} name={targetName} BuiltIn={instruction[3]} offset={instruction.Offset}");
            }
            else if (instruction.OpCode == SpvOpCode.OpMemberDecorate && instruction.WordCount >= 5 && instruction[3] == Decoration.BuiltIn)
            {
                uint typeId = instruction[1];
                string typeName = namesById.TryGetValue(typeId, out string? name) ? name : $"id_{typeId}";
                lines.Add($"  OpMemberDecorate typeId={typeId} name={typeName} memberIndex={instruction[2]} BuiltIn={instruction[4]} offset={instruction.Offset}");
            }
        }

        return lines.Count == 0
            ? "BuiltIn decorations: none"
            : "BuiltIn decorations:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
