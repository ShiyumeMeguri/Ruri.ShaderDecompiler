namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// Writes the <c>HLSLPROGRAM</c> block of a pass: pragmas, the keyword universe,
/// and one preprocessor branch per shader variant.
///
/// The problem being solved: a compiled shader is N separate bytecode blobs, one
/// per keyword combination per stage, but ShaderLab wants ONE source file. So
/// every variant's body goes in, each guarded by its own keyword condition, and
/// the pass declares the keywords so the compiler regenerates the permutations.
///
/// Two guard layers:
///   * OUTER, per stage — <c>#ifdef SHADER_STAGE_*</c>, so a stage's file-scope
///     declarations (input/output structs, static globals, its own dead-code-
///     eliminated constant buffers) never collide with another stage's.
///   * INNER, per variant — an <c>#if / #elif / #else</c> chain, so exactly one
///     body is visible per permutation.
///
/// The chain always ENDS IN <c>#else</c>, never a bare <c>#endif</c>. Every
/// permutation the compiler generates must reach some body; one that reaches none
/// fails with "Did not find shader kernel 'main'".
/// </summary>
internal static class VariantChainWriter
{
    public static void WriteProgram(
        IndentedWriter writer,
        List<string> keywordNames,
        UnitySerializedPass pass,
        int subShaderIndex,
        int passIndex,
        VariantFileSet variants)
    {
        writer.Line("HLSLPROGRAM");

        // The emitted HLSL uses SM5.1+ constructs (register spaces, templated
        // buffer loads) that the legacy compiler path rejects. DXC accepts them
        // and lifts the target to the maximum the D3D11 backend allows.
        writer.Line("#pragma target 5.0");
        writer.Line("#pragma use_dxc");

        // HLSLSupport.cginc is what DEFINES the SHADER_STAGE_* macros. Without it
        // Unity does not set them for a plain HLSLPROGRAM block, so every
        // stage-guarded block below would be invisible and the compiler would
        // report a missing kernel. The include is preprocessor symbols and a few
        // helpers — negligible cost, load-bearing effect.
        writer.Line("#include \"HLSLSupport.cginc\"");

        foreach ((string stage, _) in pass.EnumerateProgramSlots())
        {
            if (TryGetStagePragma(stage, out string pragma))
            {
                writer.Line($"{pragma} {EntryPointName}");
            }
        }

        // One multi_compile_local per keyword, deliberately NOT bundled. Bundling
        // makes a set mutually exclusive, which is wrong for independent toggles.
        List<string> passKeywords = KeywordSymbols.ForPass(keywordNames, pass);
        foreach (string keyword in passKeywords)
        {
            writer.Line($"#pragma multi_compile_local _ {keyword}");
        }

        writer.Blank();

        foreach ((string stage, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            WriteStage(writer, keywordNames, stage, program.SubPrograms, subShaderIndex, passIndex, variants);
        }

        writer.Line("ENDHLSL");
    }

    private static void WriteStage(
        IndentedWriter writer,
        List<string> keywordNames,
        string stage,
        List<UnitySerializedSubProgram> subPrograms,
        int subShaderIndex,
        int passIndex,
        VariantFileSet variants)
    {
        if (subPrograms.Count == 0)
        {
            return;
        }

        writer.Line($"// Stage: {stage}");

        string? stageMacro = GetStageMacro(stage);
        if (stageMacro is not null)
        {
            writer.Line($"#ifdef {stageMacro}");
        }

        // Only the keywords THIS stage uses, so a condition never mentions a
        // keyword the stage is indifferent to.
        List<ushort> stageKeywords = KeywordSymbols.DistinctIndices(subPrograms);

        // Variants with identical keyword sets are platform variants of one
        // logical compile; keep the first.
        var groups = subPrograms
            .GroupBy(subProgram => string.Join(",", subProgram.KeywordIndices.OrderBy(static index => index)))
            .ToList();

        if (groups.Count <= 1)
        {
            // A single variant needs no chain, so its body is inlined — unless it
            // is one of the bodies that must be split whatever the chain looks like.
            UnitySerializedSubProgram only = groups[0].First();
            bool onlySplit = WillSplit(only, variants, chainSplit: false);

            WriteVariantHeader(writer, stage, only, collapsed: 0, isCatchAll: false, split: onlySplit);
            WriteBody(writer, stage, only, keywordNames, subShaderIndex, passIndex, variants, onlySplit);
            writer.Blank();

            if (stageMacro is not null)
            {
                writer.Line("#endif");
                writer.Blank();
            }
            return;
        }

        // The catch-all goes LAST, as `#else`. Preference order:
        //   1. the variant with no keywords — the shader's own default state
        //   2. otherwise the first listed
        // Runtime keyword state decides which branch actually executes, so a
        // suboptimal catch-all only affects permutations nothing selects.
        int defaultIndex = groups.FindIndex(static group => group.First().KeywordIndices.Count == 0);
        if (defaultIndex < 0)
        {
            defaultIndex = 0;
        }

        var emitOrder = new List<int>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            if (i != defaultIndex)
            {
                emitOrder.Add(i);
            }
        }
        emitOrder.Add(defaultIndex);

        for (int position = 0; position < emitOrder.Count; position++)
        {
            IGrouping<string, UnitySerializedSubProgram> group = groups[emitOrder[position]];
            UnitySerializedSubProgram primary = group.First();
            bool isLast = position == emitOrder.Count - 1;

            string directive = position switch
            {
                0 => "#if " + (KeywordSymbols.BuildCondition(keywordNames, stageKeywords, primary.KeywordIndices.ToList()) ?? "1"),
                _ when isLast => "#else",
                _ => "#elif " + (KeywordSymbols.BuildCondition(keywordNames, stageKeywords, primary.KeywordIndices.ToList()) ?? "1"),
            };
            writer.Line(directive);

            bool bodySplit = WillSplit(primary, variants, chainSplit: variants.IsSplitting);
            WriteVariantHeader(writer, stage, primary, group.Count() - 1, isLast, bodySplit);
            WriteBody(writer, stage, primary, keywordNames, subShaderIndex, passIndex, variants, bodySplit);
        }

        writer.Line("#endif");

        if (stageMacro is not null)
        {
            writer.Line("#endif");
        }

        writer.Blank();
    }

    /// <summary>
    /// Identify the variant about to be emitted — but only with what the
    /// <c>#include</c> line below it does not already say.
    ///
    /// When splitting, the file name carries subshader, pass, stage and blob, and
    /// the file's own header carries the rest, so a header here would repeat
    /// itself once per variant across tens of thousands of them. Only the two
    /// facts that have no other home are written: that several platform variants
    /// collapsed onto this one, and that this is the chain's catch-all.
    /// </summary>
    private static void WriteVariantHeader(IndentedWriter writer, string stage, UnitySerializedSubProgram subProgram, int collapsed, bool isCatchAll, bool split)
    {
        if (!split)
        {
            string parameterBlob = subProgram.ParameterBlobIndex.HasValue
                ? subProgram.ParameterBlobIndex.Value.ToString()
                : "<none>";

            writer.Line($"// Stage: {stage}, Blob: {subProgram.BlobIndex}, ParamBlob: {parameterBlob}, Language: {subProgram.SourceLanguage}");
        }

        if (collapsed > 0)
        {
            writer.Line($"// {collapsed} platform variant(s) collapsed here.");
        }

        if (isCatchAll)
        {
            writer.Line("// Catch-all.");
        }
    }

    private static void WriteBody(
        IndentedWriter writer,
        string stage,
        UnitySerializedSubProgram subProgram,
        List<string> keywordNames,
        int subShaderIndex,
        int passIndex,
        VariantFileSet variants,
        bool split)
    {
        if (!subProgram.Success || string.IsNullOrWhiteSpace(subProgram.SourceCode))
        {
            writer.Line("// Decompile failed.");
            if (!string.IsNullOrWhiteSpace(subProgram.ErrorMessage))
            {
                foreach (string line in TextLines.Split(subProgram.ErrorMessage!))
                {
                    writer.Line($"// {line}");
                }
            }

            return;
        }

        string source = TextLines.TrimTrailingWhitespace(subProgram.SourceCode!);
        bool hlsl = IsHlsl(subProgram);

        // The Unity adaptations are HLSL rewrites; a foreign body passes through
        // untouched. Everything after this point treats the two alike.
        string body = hlsl ? UnityHlslAdapter.Adapt(source, stage) : source;
        string? legend = hlsl ? UnityHlslAdapter.RecoveryLegend(body) : null;

        if (split)
        {
            string keywords = KeywordSymbols.DescribeVariant(subProgram, keywordNames);
            string includePath = variants.Add(subShaderIndex, passIndex, stage, subProgram, keywords, body, legend);
            writer.Line($"#include \"{includePath}\"");
            return;
        }

        if (legend is not null)
        {
            writer.Line(legend);
        }

        writer.Raw(body);
    }

    private static bool IsHlsl(UnitySerializedSubProgram subProgram)
        => string.Equals(subProgram.SourceLanguage, "hlsl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Will this variant's body become an <c>#include</c> rather than sit inline?
    ///
    /// Normally that is the chain's decision — a lone variant is cheaper left in
    /// place. A FOREIGN body overrides it, because "is the chain long enough to be
    /// worth splitting" is the wrong question for one: these are the largest
    /// bodies in the archive, and a single shader inlining 468 of them reached
    /// 75 MB across a million lines. One shared answer, because the emitted header
    /// and the emitted body have to agree about it — a header describing a body
    /// that then arrives as an include is the same fact stated twice.
    /// </summary>
    private static bool WillSplit(UnitySerializedSubProgram subProgram, VariantFileSet variants, bool chainSplit)
        => chainSplit || (!IsHlsl(subProgram) && variants.IsSplitting);

    /// <summary>
    /// Every stage keeps the entry name <c>main</c>. The stage guards make only
    /// one visible per compile, so there is nothing to disambiguate — and the
    /// D3D11 compiler rejects some non-<c>main</c> entry names in a plain
    /// HLSLPROGRAM block, which made renaming a net loss.
    /// </summary>
    private const string EntryPointName = "main";

    private static string? GetStageMacro(string stage) => stage switch
    {
        "Vertex" => "SHADER_STAGE_VERTEX",
        "Fragment" => "SHADER_STAGE_FRAGMENT",
        "Geometry" => "SHADER_STAGE_GEOMETRY",
        "Hull" => "SHADER_STAGE_HULL",
        "Domain" => "SHADER_STAGE_DOMAIN",
        "RayTracing" => "SHADER_STAGE_RAY_TRACING",
        _ => null,
    };

    private static bool TryGetStagePragma(string stage, out string pragma)
    {
        pragma = stage switch
        {
            "Vertex" => "#pragma vertex",
            "Fragment" => "#pragma fragment",
            "Geometry" => "#pragma geometry",
            "Hull" => "#pragma hull",
            "Domain" => "#pragma domain",
            _ => string.Empty,
        };

        return pragma.Length > 0;
    }
}
