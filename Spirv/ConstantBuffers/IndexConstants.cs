namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Declares every integer constant the access translator can need as an index.
///
/// This is not an optimisation. The translator resolves indices BY VALUE through
/// the constant map and never mints one mid-translation — doing so would mutate
/// the module while a validation pass is reasoning about it. A missing constant
/// therefore reads as "this access does not translate", which would sink an
/// otherwise perfectly rewritable buffer, or (worse, once the layout is trimmed
/// against translation results) delete a member something needs.
///
/// So every index the layout could produce is declared up front. Idempotent —
/// interning an existing constant returns it.
/// </summary>
internal static class IndexConstants
{
    /// <summary>
    /// Headroom above the largest structural count, covering the component and
    /// column indices of the widest member shape without enumerating every path
    /// that can produce one.
    /// </summary>
    private const int ComponentHeadroom = 8;

    public static void Declare(StructuringContext context, BlockLayout layout)
    {
        int ceiling = layout.Members.Count + ComponentHeadroom;
        foreach (BlockMemberLayout member in layout.Members)
        {
            AccumulateCeiling(member, ref ceiling);
        }

        for (uint value = 0; value <= (uint)ceiling; value++)
        {
            context.Constants.Register(context.Types.InternUIntConstant(value), value);
        }
    }

    private static void AccumulateCeiling(BlockMemberLayout member, ref int ceiling)
    {
        MemberShape shape = member.Shape;

        ceiling = Math.Max(ceiling, member.RegisterCount);
        ceiling = Math.Max(ceiling, shape.Rows);
        ceiling = Math.Max(ceiling, shape.Columns);
        ceiling = Math.Max(ceiling, shape.ArrayLength);
        ceiling = Math.Max(ceiling, shape.SecondaryArrayLength);

        if (shape.StructMembers is null)
        {
            return;
        }

        ceiling = Math.Max(ceiling, shape.StructMembers.Count);
        foreach (BlockMemberLayout child in shape.StructMembers)
        {
            AccumulateCeiling(child, ref ceiling);
        }
    }
}
