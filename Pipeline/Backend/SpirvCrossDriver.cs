using System.Runtime.InteropServices;
using System.Text;
using Ruri.ShaderTools.Pipeline.Naming;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using CrossBackend = Silk.NET.SPIRV.Cross.Backend;

namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>
/// SPIR-V → source through the spirv-cross C ABI, configured from an
/// <see cref="EmissionPlan"/> instead of patched afterwards.
///
/// Names are not set here: they already live in the module as <c>OpName</c> and
/// spirv-cross reads them from there. This driver only supplies what the module
/// cannot express — vertex input semantics and backend options — and undoes the
/// one spelling the HLSL backend imposes that Unity property binding cannot
/// accept.
///
/// The language is the plan's decision, made from what the module declares. A
/// module the chosen backend refuses is a failure with the backend's own
/// message, not a retry in another language.
/// </summary>
internal sealed unsafe class SpirvCrossDriver
{
    private static readonly Cross Api = Cross.GetApi();

    /// <summary>
    /// Lowest GLSL version that accepts every feature a GLSL-only module can
    /// declare — ray tracing, ray query, buffer device address — with Vulkan
    /// semantics always on, because the input is Vulkan-flavoured.
    /// </summary>
    private const uint VulkanGlslVersion = 460;

    public string? LastFailure { get; private set; }

    public string? Emit(ReadOnlySpan<byte> spirv, EmissionPlan plan)
    {
        LastFailure = null;

        if (spirv.Length < 4 || (spirv.Length & 3) != 0)
        {
            LastFailure = $"SPIR-V byte length {spirv.Length} is not a positive multiple of 4.";
            return null;
        }

        bool hlsl = plan.Language == EmitLanguage.Hlsl;

        Context* context = null;
        try
        {
            if (Api.ContextCreate(&context) != Result.Success || context == null)
            {
                LastFailure = "spvc_context_create failed.";
                return null;
            }

            ParsedIr* parsed;
            fixed (byte* words = spirv)
            {
                if (Api.ContextParseSpirv(context, (uint*)words, (nuint)(spirv.Length >> 2), &parsed) != Result.Success)
                {
                    return Fail(context);
                }
            }

            Compiler* compiler;
            if (Api.ContextCreateCompiler(context, hlsl ? CrossBackend.Hlsl : CrossBackend.Glsl, parsed, CaptureMode.TakeOwnership, &compiler) != Result.Success)
            {
                return Fail(context);
            }

            if (hlsl)
            {
                foreach (VertexAttributeSemantic attribute in plan.VertexAttributes)
                {
                    AddVertexAttributeRemap(compiler, attribute);
                }
            }

            CompilerOptions* options;
            if (Api.CompilerCreateCompilerOptions(compiler, &options) != Result.Success)
            {
                return Fail(context);
            }

            if (hlsl)
            {
                Api.CompilerOptionsSetUint(options, CompilerOption.HlslShaderModel, plan.ShaderModel);
                Api.CompilerOptionsSetBool(options, CompilerOption.ForceZeroInitializedVariables, 1);
            }
            else
            {
                Api.CompilerOptionsSetUint(options, CompilerOption.GlslVersion, VulkanGlslVersion);
                Api.CompilerOptionsSetBool(options, CompilerOption.GlslVulkanSemantics, 1);
            }

            if (Api.CompilerInstallCompilerOptions(compiler, options) != Result.Success)
            {
                return Fail(context);
            }

            if (!string.IsNullOrEmpty(plan.EntryPoint.Name))
            {
                Api.CompilerSetEntryPoint(compiler, plan.EntryPoint.Name, (ExecutionModel)plan.EntryPoint.ExecutionModel);
            }

            byte* source;
            if (Api.CompilerCompile(compiler, &source) != Result.Success || source == null)
            {
                return Fail(context);
            }

            string text = Marshal.PtrToStringUTF8((IntPtr)source) ?? string.Empty;

            if (hlsl)
            {
                string? unlanded = RestoreAuthoredMembers(compiler, ref text, plan.FlattenedBlocks);
                if (unlanded is not null)
                {
                    LastFailure = unlanded;
                    return null;
                }
            }

            return text;
        }
        finally
        {
            if (context != null)
            {
                Api.ContextDestroy(context);
            }
        }
    }

    private static void AddVertexAttributeRemap(Compiler* compiler, VertexAttributeSemantic attribute)
    {
        int byteCount = Encoding.UTF8.GetByteCount(attribute.Semantic) + 1;
        Span<byte> semantic = byteCount <= 64 ? stackalloc byte[64] : new byte[byteCount];
        int written = Encoding.UTF8.GetBytes(attribute.Semantic, semantic);
        semantic[written] = 0;

        fixed (byte* name = semantic)
        {
            HlslVertexAttributeRemap entry = new() { Location = attribute.Location, Semantic = name };
            Api.CompilerHlslAddVertexAttributeRemap(compiler, &entry, 1);
        }
    }

    private string? Fail(Context* context)
    {
        byte* message = Api.ContextGetLastErrorString(context);
        LastFailure = message != null
            ? "spirv-cross: " + Marshal.PtrToStringUTF8((IntPtr)message)
            : "spirv-cross failed without an error string.";
        return null;
    }

    /// <summary>
    /// The HLSL backend flattens every constant buffer into
    /// <c>&lt;variable&gt;_&lt;member&gt;</c> identifiers and offers no option to
    /// stop. Its spelling of both halves is read back from it after compilation —
    /// that is the spelling it uniquified and wrote, including any respelling of
    /// a name it would not accept — and joined the way it joins them: one
    /// underscore, runs collapsed. Each joined identifier is then replaced, as a
    /// whole token, by the member's own name.
    ///
    /// A planned member whose joined identifier is not in the text is a member
    /// Unity will never bind. Refusing here turns a renders-wrong shader into a
    /// failure that names the exact member and the spelling that was looked for,
    /// so the naming path that lost it can be found instead of guessed at.
    ///
    /// The members are renamed in plan order, each rename acting on the text the
    /// ones before it left -- a member's own name can be what a later member's
    /// joined identifier spells. Every spelling involved is an identifier, so a
    /// rename changes what a token says and never where tokens begin and end: the
    /// text is split into tokens once, each rename moves every token currently
    /// saying one spelling over to the next, and the text is written once at the
    /// end. Rewriting the whole text for every member was a copy of it per member,
    /// hundreds of members over a shader of a hundred kilobytes, and the largest
    /// single cost of emitting source.
    /// </summary>
    private static string? RestoreAuthoredMembers(Compiler* compiler, ref string text, IReadOnlyList<FlattenedBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return null;
        }

        List<Rename> renames = new();
        string? unnamed = null;
        foreach (FlattenedBlock block in blocks)
        {
            string variable = ReadName(Api.CompilerGetName(compiler, block.VariableId));
            if (variable.Length == 0)
            {
                unnamed = $"Planned constant-buffer variable {block.VariableId} lost its name in the backend.";
                break;
            }

            foreach (uint member in block.AuthoredMembers)
            {
                string name = ReadName(Api.CompilerGetMemberName(compiler, block.StructTypeId, member));
                if (name.Length == 0)
                {
                    unnamed = $"Planned member {member} of constant buffer '{variable}' lost its name in the backend.";
                    break;
                }

                string flattened = HlslIdentifier.CollapseUnderscores(variable + "_" + name);
                if (!IsIdentifier(flattened) || !IsIdentifier(name))
                {
                    unnamed = $"Planned constant-buffer member is spelled as no identifier by the backend: {variable}.{name} (joined '{flattened}').";
                    break;
                }

                renames.Add(new Rename(variable, name, flattened));
            }

            if (unnamed is not null)
            {
                break;
            }
        }

        TokenGroups groups = TokenGroups.Of(text, renames);
        foreach (Rename rename in renames)
        {
            if (!groups.Move(rename.Flattened, rename.Name))
            {
                return $"Planned constant-buffer member did not land in the emitted HLSL: {rename.Variable}.{rename.Name} (looked for '{rename.Flattened}').";
            }
        }

        if (unnamed is not null)
        {
            return unnamed;
        }

        text = groups.Write(text);
        return null;
    }

    private static string ReadName(byte* utf8)
        => utf8 == null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)utf8) ?? string.Empty;

    private static bool IsIdentifier(string spelling)
    {
        foreach (char c in spelling)
        {
            if (!IsIdentifierCharacter(c))
            {
                return false;
            }
        }

        return spelling.Length > 0;
    }

    private static bool IsIdentifierCharacter(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

    private readonly record struct Rename(string Variable, string Name, string Flattened);

    /// <summary>
    /// The tokens of a text that a list of renames can reach, grouped by what they
    /// first said, and what each group says now. A token no rename's joined
    /// identifier spells is never reached: a rename only acts on a token that
    /// currently says its joined identifier, and a token starts saying something
    /// else only by being renamed.
    /// </summary>
    private sealed class TokenGroups
    {
        private readonly List<(int Start, int Length, int Group)> occurrences = new();
        private readonly List<string> said = new();
        private readonly Dictionary<string, List<int>> groupsSaying = new(StringComparer.Ordinal);

        public static TokenGroups Of(string text, List<Rename> renames)
        {
            TokenGroups groups = new();
            Dictionary<string, int> groupOf = new(StringComparer.Ordinal);
            foreach (Rename rename in renames)
            {
                groupOf.TryAdd(rename.Flattened, -1);
            }

            Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup = groupOf.GetAlternateLookup<ReadOnlySpan<char>>();
            ReadOnlySpan<char> characters = text.AsSpan();
            int cursor = 0;
            while (cursor < characters.Length)
            {
                if (!IsIdentifierCharacter(characters[cursor]))
                {
                    cursor++;
                    continue;
                }

                int start = cursor;
                while (cursor < characters.Length && IsIdentifierCharacter(characters[cursor]))
                {
                    cursor++;
                }

                ReadOnlySpan<char> token = characters[start..cursor];
                if (!lookup.TryGetValue(token, out int group))
                {
                    continue;
                }

                if (group < 0)
                {
                    group = groups.said.Count;
                    string spelling = token.ToString();
                    groupOf[spelling] = group;
                    groups.said.Add(spelling);
                    groups.groupsSaying.Add(spelling, [group]);
                }

                groups.occurrences.Add((start, cursor - start, group));
            }

            return groups;
        }

        /// <summary>Every token saying <paramref name="from"/> now says <paramref name="to"/>; false when none says it.</summary>
        public bool Move(string from, string to)
        {
            if (!groupsSaying.TryGetValue(from, out List<int>? moving))
            {
                return false;
            }

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                return true;
            }

            groupsSaying.Remove(from);
            foreach (int group in moving)
            {
                said[group] = to;
            }

            if (groupsSaying.TryGetValue(to, out List<int>? already))
            {
                already.AddRange(moving);
            }
            else
            {
                groupsSaying.Add(to, moving);
            }

            return true;
        }

        public string Write(string text)
        {
            if (occurrences.Count == 0)
            {
                return text;
            }

            StringBuilder builder = new(text.Length);
            int cursor = 0;
            foreach ((int start, int length, int group) in occurrences)
            {
                builder.Append(text, cursor, start - cursor);
                builder.Append(said[group]);
                cursor = start + length;
            }

            builder.Append(text, cursor, text.Length - cursor);
            return builder.ToString();
        }
    }
}
