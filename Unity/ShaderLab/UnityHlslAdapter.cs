using System.Text.RegularExpressions;

namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// Rewrites emitted HLSL so a Unity ShaderLab import accepts it without hand
/// edits.
///
/// Each pass below fixes a specific, reproducible way the raw emitter output is
/// WRONG for Unity — not stylistic preferences. In order:
///
///  1. Sampler declarations Unity's importer rejects outright.
///  2. Texture names that must match the Properties block to auto-bind.
///  3. Duplicate buffer declarations at one register.
///  4. Overlapping interstage semantics the D3D compiler refuses.
///  5. Vertex input semantics — the one that silently renders garbage.
///  6. Constant-buffer member prefixes that break material property binding.
///
/// Passes 5 and 6 are the dangerous ones: both produce a shader that COMPILES and
/// then renders wrongly, which is far harder to notice than a compile error.
/// </summary>
internal static class UnityHlslAdapter
{
    public static string Adapt(string body, string? stage)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        // Stage isolation is handled by the `#ifdef SHADER_STAGE_*` guards the
        // variant writer puts around each stage block, so emitter-generated
        // file-scope declarations never share a translation unit and need no
        // per-stage renaming. An earlier design DID rename them per stage, which
        // then forced cross-stage cbuffer merging, which then hit variants that
        // legitimately declare the same member at different packoffsets. The
        // guards sidestep that whole class of problem.

        // Each pass is gated on a literal its pattern cannot match without. The
        // gate is an ordinal substring scan; the pass it skips is a regex walk of
        // the whole body, and most bodies do not contain most of these.
        if (body.Contains("Material_", StringComparison.Ordinal))
        {
            body = AdaptSamplerNames(body);
            body = AdaptTextureNames(body);
        }

        body = CollapseAliasedBuffers(body);

        if (body.Contains("SPIRV_Cross_", StringComparison.Ordinal))
        {
            body = DeduplicateInterstageSemantics(body);

            if (string.Equals(stage, "Vertex", StringComparison.Ordinal))
            {
                body = FixVertexInputSemantics(body);
            }
        }

        return StripBlockMemberPrefix(body);
    }

    // --- 7. legend for names that are not author names ----------------------

    /// <summary>
    /// One line naming the generated markers this body actually contains, or null
    /// when it contains none.
    ///
    /// A reader arriving with just this file has to be able to tell a recovered
    /// name from an authored one — quoting <c>_Stripped_64</c> as a material
    /// property and hunting for it in CPU-side code is a real cost. The marker
    /// names are already self-describing, so saying what they mean once, in one
    /// line, carries the same information the old fifteen-line block did.
    ///
    /// RETURNED RATHER THAN PREPENDED, which is the point. Prepending meant
    /// <c>legend + body</c> — a full copy of every body, and the bodies are the
    /// largest strings the pipeline handles. The caller already builds the file
    /// around the body, so handing it a line to append costs nothing.
    /// </summary>
    public static string? RecoveryLegend(string body)
    {
        bool stripped = body.Contains(GeneratedNames.StrippedSymbol, StringComparison.Ordinal);
        bool unstructured = body.Contains(GeneratedNames.UnstructuredBlock, StringComparison.Ordinal);
        bool unmapped = body.Contains(GeneratedNames.UnmappedRegion, StringComparison.Ordinal);

        if (!stripped && !unstructured && !unmapped)
        {
            return null;
        }

        var legend = new System.Text.StringBuilder(160);
        legend.Append("// Generated names (not authored, no CPU-side property matches them):");

        if (stripped)
        {
            legend.Append("  <Buffer>_").Append(GeneratedNames.StrippedSymbol).Append("_<byteOffset> = unnamed member at that offset;");
        }

        if (unstructured)
        {
            legend.Append("  <Buffer>_").Append(GeneratedNames.UnstructuredBlock).Append(" = one member spanning the whole buffer;");
        }

        if (unmapped)
        {
            legend.Append("  <Buffer>_").Append(GeneratedNames.UnmappedRegion).Append(" = byte range no symbol describes;");
        }

        return legend.ToString();
    }


    // --- 1. samplers --------------------------------------------------------

    private static readonly Regex MaterialSamplerDecl =
        new(@"\bMaterial_(?<n>[A-Za-z0-9_]+)Sampler\b", RegexOptions.Compiled);

    /// <summary>
    /// <c>Material_XSampler</c> → <c>sampler_X</c>, which Unity's importer
    /// recognises and pairs with the matching texture.
    /// </summary>
    private static string AdaptSamplerNames(string body) => MaterialSamplerDecl.Replace(body, "sampler_${n}");

    // --- 2. textures --------------------------------------------------------

    private static readonly Regex MaterialTextureDecl =
        new(@"(?<t>Texture(?:2D|2DArray|Cube|CubeArray|3D)(?:<[^>]+>)?)\s+Material_(?<n>[A-Za-z0-9_]+)\s*:\s*register",
            RegexOptions.Compiled);

    /// <summary>
    /// <c>Material_X</c> → <c>_X</c> for TEXTURES ONLY, so the Properties block's
    /// <c>_X</c> auto-binds.
    ///
    /// Anchored on the texture type token, and the body rewrite is limited to
    /// roots that actually matched a declaration. A blanket
    /// <c>Material_* → _*</c> would also rename constant-buffer members that share
    /// the prefix, which have no corresponding property and would simply stop
    /// resolving.
    /// </summary>
    private static string AdaptTextureNames(string body)
    {
        var renamed = new HashSet<string>(StringComparer.Ordinal);

        body = MaterialTextureDecl.Replace(body, match =>
        {
            renamed.Add(match.Groups["n"].Value);
            return $"{match.Groups["t"].Value} _{match.Groups["n"].Value} : register";
        });

        if (renamed.Count == 0)
        {
            return body;
        }

        // One alternation over the whole body rather than one pass per name. A
        // material-heavy shader declares dozens of textures, and each pass was
        // rewriting the entire body to touch a handful of identifiers.
        var alternation = new System.Text.StringBuilder(renamed.Count * 24);
        alternation.Append(@"\bMaterial_(?<n>");
        bool first = true;
        foreach (string name in renamed)
        {
            if (!first)
            {
                alternation.Append('|');
            }
            alternation.Append(Regex.Escape(name));
            first = false;
        }
        alternation.Append(@")\b");

        return Regex.Replace(body, alternation.ToString(), "_${n}");
    }

    // --- 3. aliased buffers -------------------------------------------------

    private static readonly Regex AliasedByteAddressDecl =
        new(@"^\s*ByteAddressBuffer\s+T(?<n>\d+)_\d+\s*:\s*register\(t\k<n>[^\)]*\);\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex AliasedByteAddressRef =
        new(@"\bT(\d+)_\d+\b", RegexOptions.Compiled);

    /// <summary>
    /// Drop the duplicate <c>T&lt;N&gt;_&lt;M&gt;</c> declaration at the same
    /// register as <c>T&lt;N&gt;</c> and repoint its uses. The emitter produces
    /// both names when two SSA values touch one descriptor; declaring a register
    /// twice is a compile error.
    /// </summary>
    private static string CollapseAliasedBuffers(string body)
    {
        body = AliasedByteAddressDecl.Replace(body, string.Empty);
        return AliasedByteAddressRef.Replace(body, "T$1");
    }

    // --- 4/5. interstage + vertex semantics ---------------------------------

    private static readonly Regex CrossStageStruct =
        new(@"struct\s+SPIRV_Cross_(Input|Output)\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);

    private static readonly Regex FieldSemantic =
        new(@"(?<lhs>\b\w[\w\s]*\b)\s*:\s*(?<sem>TEXCOORD\d+)\s*(?<tail>;)", RegexOptions.Compiled);

    /// <summary>
    /// Renumber duplicate <c>TEXCOORDN</c> slots.
    ///
    /// The emitter gives each SPIR-V variable its own slot even when several share
    /// a location at different component offsets — a packed vec4 written as two
    /// float2s, for instance. The D3D compiler rejects that with "Semantic
    /// 'TEXCOORD' overlap at N": interstage semantics must be unique.
    ///
    /// Vertex output and fragment input are emitted separately but agree on the
    /// duplicate pattern (the emitter is deterministic), so renumbering each side
    /// independently still leaves them matching.
    /// </summary>
    private static string DeduplicateInterstageSemantics(string body)
    {
        return CrossStageStruct.Replace(body, structMatch =>
        {
            string header = structMatch.Value[..(structMatch.Value.IndexOf('{') + 1)];
            string structBody = structMatch.Groups["body"].Value;

            var used = new HashSet<int>();
            foreach (Match field in FieldSemantic.Matches(structBody))
            {
                if (TryParseTexcoordIndex(field.Groups["sem"].Value, out int index))
                {
                    used.Add(index);
                }
            }

            var seen = new HashSet<int>();
            string rewritten = FieldSemantic.Replace(structBody, field =>
            {
                if (!TryParseTexcoordIndex(field.Groups["sem"].Value, out int index))
                {
                    return field.Value;
                }

                if (seen.Add(index))
                {
                    return field.Value;   // first occurrence keeps its slot
                }

                int free = 0;
                while (used.Contains(free) || seen.Contains(free))
                {
                    free++;
                }
                seen.Add(free);
                used.Add(free);

                return $"{field.Groups["lhs"].Value} : TEXCOORD{free}{field.Groups["tail"].Value}";
            });

            return header + rewritten + "}";
        });
    }

    /// <summary>
    /// Restore real vertex input semantics.
    ///
    /// The emitter names every SPIR-V input <c>: TEXCOORD&lt;location&gt;</c>. Unity
    /// feeds vertex attributes BY SEMANTIC NAME, not by location, so a position
    /// declared as <c>TEXCOORD0</c> receives the first UV stream and the mesh
    /// renders as garbage — while compiling perfectly.
    ///
    /// The field name is still the author's original variable name, so it maps
    /// straight back to the canonical semantic. Names that map to nothing are left
    /// alone: those are engine-custom instance attributes whose location-based
    /// semantic is already what the mesh streams.
    ///
    /// Only the INPUT struct is touched. Output semantics are interstage and must
    /// keep matching the fragment side.
    /// </summary>
    private static string FixVertexInputSemantics(string body)
    {
        return CrossStageStruct.Replace(body, structMatch =>
        {
            if (structMatch.Groups[1].Value != "Input")
            {
                return structMatch.Value;
            }

            string header = structMatch.Value[..(structMatch.Value.IndexOf('{') + 1)];
            string rewritten = FieldSemantic.Replace(structMatch.Groups["body"].Value, field =>
            {
                string? canonical = MapVertexSemantic(field.Groups["lhs"].Value.Trim());

                // No usable field name — the bytecode was Vulkan SPIR-V, whose
                // inputs carry a location and nothing else. Fall back to Unity's
                // own attribute ordering, which the location IS.
                if (canonical is null && TryParseTexcoordIndex(field.Groups["sem"].Value, out int location))
                {
                    canonical = MapUnityAttributeLocation(location);
                }

                return canonical is null
                    ? field.Value
                    : $"{field.Groups["lhs"].Value} : {canonical}{field.Groups["tail"].Value}";
            });

            return header + rewritten + "}";
        });
    }

    /// <summary>
    /// Unity's vertex attribute order, which is what a Vulkan input's
    /// <c>location</c> encodes.
    ///
    /// WHY THIS IS SOUND, not a guess. When the shader came from a DXBC
    /// container the front end names its inputs from the container's own input
    /// signature, so <see cref="MapVertexSemantic"/> resolves them and this table
    /// is never consulted. Reaching here therefore means the bytecode was Vulkan
    /// SPIR-V, where the only thing an input carries is a location — and Unity
    /// assigns those in <c>VertexAttribute</c> order. The provenance test and the
    /// fallback are the same decision, which is what keeps the two from being
    /// applied to the wrong kind of shader.
    ///
    /// The observed corpus corroborates it: locations 0..6 come through as
    /// float3, float3, float4, float4, float2, float2, float2 — exactly
    /// position, normal, tangent, colour, and three UV channels.
    ///
    /// Beyond the table the location is left alone: an engine is free to feed
    /// extra streams there, and inventing a semantic for one would be the very
    /// mistake this whole pass exists to prevent.
    /// </summary>
    private static string? MapUnityAttributeLocation(int location) => location switch
    {
        0 => "POSITION0",
        1 => "NORMAL0",
        2 => "TANGENT0",
        3 => "COLOR0",
        >= 4 and <= 11 => $"TEXCOORD{location - 4}",
        12 => "BLENDWEIGHT0",
        13 => "BLENDINDICES0",
        _ => null,
    };

    private static string? MapVertexSemantic(string fieldDeclaration)
    {
        // The loose left-hand match can pick up type tokens; only the trailing
        // identifier matters.
        int lastSpace = fieldDeclaration.LastIndexOf(' ');
        string identifier = lastSpace >= 0 ? fieldDeclaration[(lastSpace + 1)..] : fieldDeclaration;

        switch (identifier)
        {
            case "POSITION": return "POSITION0";
            case "NORMAL": return "NORMAL0";
            case "TANGENT": return "TANGENT0";
            case "COLOR": return "COLOR0";
            case "TEXCOORD": return "TEXCOORD0";
            case "BLENDINDICES": return "BLENDINDICES0";
            case "BLENDWEIGHT": return "BLENDWEIGHT0";
            case "PSIZE": return "PSIZE0";
        }

        if (identifier.StartsWith("TEXCOORD_", StringComparison.Ordinal) && int.TryParse(identifier.AsSpan(9), out int uv))
        {
            return "TEXCOORD" + uv;
        }

        if (identifier.StartsWith("COLOR_", StringComparison.Ordinal) && int.TryParse(identifier.AsSpan(6), out int colour))
        {
            return "COLOR" + colour;
        }

        return null;
    }

    /// <summary>
    /// Is this member name one the recovery pipeline minted, rather than one the
    /// author wrote? Placeholder names are unique only within their own block, so
    /// they must keep the emitter's block prefix to stay unique globally.
    /// </summary>
    private static bool IsGeneratedName(string memberName)
        => memberName.StartsWith(GeneratedNames.StrippedSymbol, StringComparison.Ordinal)
        || memberName.StartsWith(GeneratedNames.UnmappedRegion, StringComparison.Ordinal)
        || memberName.StartsWith(GeneratedNames.UnstructuredBlock, StringComparison.Ordinal);

    private static bool TryParseTexcoordIndex(string semantic, out int index)
    {
        index = 0;
        return semantic.StartsWith("TEXCOORD", StringComparison.Ordinal) && int.TryParse(semantic.AsSpan(8), out index);
    }

    // --- 6. constant-buffer member prefixes ---------------------------------

    private static readonly Regex BlockDecl =
        new(@"cbuffer\s+(?<name>[A-Za-z_][\w]*)\s*(?::\s*register\([^)]*\))?\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Strip the block-name prefix from constant-buffer members and every
    /// reference to them.
    ///
    /// The prefix appears because symbol injection names the struct type
    /// <c>type.&lt;Name&gt;</c> and the variable <c>&lt;Name&gt;</c>, and the
    /// emitter's uniquify pass then concatenates the variable name onto each
    /// member. Unity's material property binding matches members BY EXACT NAME, so
    /// <c>UnityPerMaterial_Color</c> never receives the material's <c>_Color</c> —
    /// another compiles-fine, renders-wrong failure.
    ///
    /// Only members that literally start with the block's prefix are stripped, so
    /// pre-stripped names are left alone. Longest-first so a shorter name cannot
    /// shadow a longer one sharing its root.
    ///
    /// GENERATED PLACEHOLDER NAMES ARE EXEMPT, and the reason is the whole point
    /// of the pass. Constant-buffer members share ONE global namespace in HLSL, so
    /// the emitter's prefix is not decoration — it is what makes them unique. That
    /// is safe to undo for an author name, because the original shader compiled and
    /// so its author names were already globally unique. It is NOT safe for a
    /// placeholder: those are minted per block as
    /// <c>{marker}_{byteOffset}</c>, unique only within their block, and two blocks
    /// with an unnamed member at the same offset both collapse to the same global
    /// name — a redefinition error. Observed at 1746 collisions across 123 shaders,
    /// <c>UnityPerDraw_Stripped_64</c> and <c>UnityPerFrame_Stripped_64</c> both
    /// becoming <c>_Stripped_64</c>.
    ///
    /// Nothing is lost by exempting them. Stripping exists so a member matches a
    /// material property BY EXACT NAME; a placeholder marks a member whose name did
    /// not survive compilation, so there is no property for it to match and nothing
    /// for the strip to achieve. Keeping the prefix also says which buffer it came
    /// from, which is strictly more than the stripped form said.
    /// </summary>
    private static string StripBlockMemberPrefix(string body)
    {
        if (!body.Contains("cbuffer", StringComparison.Ordinal))
        {
            return body;
        }

        var prefixed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match block in BlockDecl.Matches(body))
        {
            string blockName = block.Groups["name"].Value;

            // The declaration name is usually the sanitised struct alias
            // (`type_Foo`); the member prefix is the VARIABLE name (`Foo`).
            string variableName = blockName.StartsWith("type_", StringComparison.Ordinal) ? blockName[5..] : blockName;
            string prefix = variableName + "_";

            foreach (string line in block.Groups["body"].Value.Split('\n'))
            {
                Match member = Regex.Match(line, @"\b(?<n>" + Regex.Escape(prefix) + @"\w+)\b");
                if (member.Success
                    && !IsGeneratedName(member.Groups["n"].Value[prefix.Length..])
                    && seen.Add(member.Groups["n"].Value))
                {
                    prefixed.Add(member.Groups["n"].Value);
                }
            }
        }

        if (prefixed.Count == 0)
        {
            return body;
        }

        prefixed.Sort(static (a, b) => b.Length.CompareTo(a.Length));

        // NOT RegexOptions.Compiled. This pattern is built fresh for every body,
        // so compiling it emits IL once per variant — tens of thousands of times
        // — to run a single pass. The interpreted matcher is the cheaper choice
        // for a use-once regex by a wide margin.
        var pattern = new Regex(@"\b(" + string.Join("|", prefixed.Select(Regex.Escape)) + @")\b");
        return pattern.Replace(body, match =>
        {
            string value = match.Value;
            int underscore = value.IndexOf('_');
            return underscore >= 0 ? value[underscore..] : value;
        });
    }
}
