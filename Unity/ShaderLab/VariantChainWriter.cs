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

        // ONE table for this pass, resolved before anything is written with it. What a pragma
        // declares and what a condition tests have to be the same symbol: resolving them
        // separately declared `KEYWORD_28` and then tested `_MRT`, so no branch of the chain
        // could ever be taken and every variant fell through to the catch-all.
        List<string> names = EffectiveKeywordNames(keywordNames, pass);

        // One multi_compile_local per keyword, deliberately NOT bundled. Bundling
        // makes a set mutually exclusive, which is wrong for independent toggles.
        foreach (string keyword in KeywordSymbols.ForPass(names, pass))
        {
            writer.Line($"#pragma multi_compile_local _ {keyword}");
        }

        writer.Blank();

        foreach ((string stage, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            WriteStage(writer, names, stage, program.SubPrograms, subShaderIndex, passIndex, variants);
        }

        writer.Line("ENDHLSL");
    }

    /// <summary>
    /// The table a keyword INDEX is a name in.
    ///
    /// A build states the names once for the whole shader; a build from before that states them
    /// per PASS, as the pass's own name-index map, and leaves the shader-level list empty.
    /// Reading only the shader-level one left every keyword of such a build unnamed, so its
    /// conditions read <c>KEYWORD_7</c> where the shader said <c>_NORMALMAP</c> -- and a variant
    /// whose only identity is a keyword it cannot name is a variant nobody can tell apart.
    /// </summary>
    private static List<string> EffectiveKeywordNames(List<string> shaderNames, UnitySerializedPass pass)
    {
        if (shaderNames.Count > 0)
        {
            return shaderNames;
        }

        List<string> names = [];
        foreach (UnitySerializedNameIndex entry in pass.NameIndices)
        {
            while (names.Count <= entry.Second)
            {
                names.Add(string.Empty);
            }
            names[entry.Second] = entry.First;
        }
        return names;
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

        // One body is one body: it reads better where it stands than behind an #include into a
        // folder holding a single file. More than one and they all become files -- what tells
        // them apart is a keyword condition where there is one and the file name where there is
        // not, but every one of them is written either way.
        bool split = variants.IsSplitting && subPrograms.Count > 1;

        string? stageMacro = GetStageMacro(stage);
        if (stageMacro is not null)
        {
            writer.Line($"#ifdef {stageMacro}");
        }

        // Only the keywords THIS stage uses, so a condition never mentions a
        // keyword the stage is indifferent to.
        List<ushort> stageKeywords = KeywordSymbols.DistinctIndices(subPrograms);

        // Grouped by keyword set because that is what the #if chain branches on -- NOT to
        // pick one of each. Every subprogram in a group is its own compile with its own
        // bytecode blob and its own decompiled body, and all of them are emitted: a pass with
        // sixteen programs writes sixteen. Keeping the first of each group threw fourteen of
        // those sixteen away and reported "done (16/16 passes)" while doing it.
        var groups = subPrograms
            .GroupBy(subProgram => string.Join(",", subProgram.KeywordIndices.OrderBy(static index => index)))
            .ToList();

        if (groups.Count <= 1)
        {
            // One keyword set needs no #if chain around it -- but it can still hold many
            // compiles, and every one of them is written.
            foreach (UnitySerializedSubProgram only in groups[0])
            {
                WriteVariantHeader(writer, stage, only, isCatchAll: false, split: split);
                WriteBody(writer, stage, only, keywordNames, subShaderIndex, passIndex, variants, split);
            }
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
            bool isLast = position == emitOrder.Count - 1;

            // The condition is the GROUP's -- every compile in it was built for the same
            // keyword set, which is exactly why they share a branch.
            List<ushort> condition = group.First().KeywordIndices.ToList();
            string directive = position switch
            {
                0 => "#if " + (KeywordSymbols.BuildCondition(keywordNames, stageKeywords, condition) ?? "1"),
                _ when isLast => "#else",
                _ => "#elif " + (KeywordSymbols.BuildCondition(keywordNames, stageKeywords, condition) ?? "1"),
            };
            writer.Line(directive);

            bool firstOfBranch = true;
            foreach (UnitySerializedSubProgram compile in group)
            {
                WriteVariantHeader(writer, stage, compile, isLast && firstOfBranch, split);
                WriteBody(writer, stage, compile, keywordNames, subShaderIndex, passIndex, variants, split);
                firstOfBranch = false;
            }
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
    private static void WriteVariantHeader(IndentedWriter writer, string stage, UnitySerializedSubProgram subProgram, bool isCatchAll, bool split)
    {
        if (!split)
        {
            string parameterBlob = subProgram.ParameterBlobIndex.HasValue
                ? subProgram.ParameterBlobIndex.Value.ToString()
                : "<none>";

            writer.Line($"// Stage: {stage}, Blob: {subProgram.BlobIndex}, ParamBlob: {parameterBlob}, Language: {subProgram.SourceLanguage}");
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

        string body = TextLines.TrimTrailingWhitespace(subProgram.SourceCode!);
        string? legend = GeneratedNameLegend.For(body);

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
        "Compute" => "SHADER_STAGE_COMPUTE",
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
            "Compute" => "#pragma kernel",
            _ => string.Empty,
        };

        return pragma.Length > 0;
    }
}
