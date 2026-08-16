namespace Ruri.ShaderTools.Spirv.ConstantBuffers.Stages;

/// <summary>
/// Materialises a SPIR-V type for every member of every surviving plan, and
/// pre-declares the integer constants the access translator will need as
/// indices.
///
/// The constant pre-declaration is not an optimisation. The translator can only
/// look constants up BY VALUE — it never mints them mid-translation, because
/// doing so would mutate the module while a validation pass is reasoning about
/// it. Any index whose constant is missing simply fails to translate, which
/// would sink an otherwise perfectly rewritable buffer for no reason. So every
/// index the layout could possibly produce is declared up front.
/// </summary>
internal static class MemberTypeStage
{
    public static void Run(StructuringContext context)
    {
        var survived = new List<BlockRewritePlan>(context.Plans.Count);

        foreach (BlockRewritePlan plan in context.Plans)
        {
            if (!MaterializeMembers(context, plan))
            {
                context.Note(plan, "member type resolution failed");
                continue;
            }

            IndexConstants.Declare(context, plan.Layout);
            survived.Add(plan);
        }

        context.Plans = survived;
    }

    private static bool MaterializeMembers(StructuringContext context, BlockRewritePlan plan)
    {
        var memberTypeIds = new List<uint>(plan.Layout.Members.Count);

        foreach (BlockMemberLayout member in plan.Layout.Members)
        {
            uint memberTypeId = MemberTypeMaterializer.Materialize(context.Module, context.Types, member);
            if (memberTypeId == 0)
            {
                return false;
            }

            member.ResolvedTypeId = memberTypeId;
            MemberTypeMaterializer.ResolveAccessTypes(context.Module, context.Types, member);
            memberTypeIds.Add(memberTypeId);
        }

        plan.MemberTypeIds = memberTypeIds;
        return true;
    }
}
