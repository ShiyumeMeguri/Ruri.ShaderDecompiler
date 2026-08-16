using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.ShaderTools.Spirv.ConstantBuffers;

/// <summary>
/// Recognises a register-index expression as
/// <c>dynamicIndex * stride + constantOffset</c>.
///
/// Compilers spell strided array indexing several ways —
/// <c>(i &lt;&lt; 4) | 3</c>, <c>(i * 256) + 5</c>, nested combinations — and all
/// of them mean the same thing. Matching on the SHAPE rather than the spelling
/// is what makes dynamically indexed constant-buffer arrays rewritable.
///
/// <c>OpBitwiseOr</c> is treated as addition when its right operand is a
/// constant: compilers emit OR for bit-disjoint adds, e.g. <c>(i &lt;&lt; k) | c</c>
/// with <c>c &lt; 2^k</c>. Without that equivalence, instancing access patterns
/// simply do not parse.
///
/// FAILURE POLICY — the important part. An unrecognised expression falls through
/// to "the whole thing is an opaque dynamic index, stride 1", NOT to failure.
/// Returning false here would reject the entire constant buffer over one unusual
/// index; falling through means that single access finds no matching member and
/// takes the component-read path, while every other access in the buffer still
/// gets its names. The two halves of that used to disagree — dynamic+dynamic
/// hard-failed while an unknown opcode fell through — which meant a common
/// <c>m0[base + offset]</c> pattern sank buffers it had no business sinking.
///
/// No <c>checked</c> arithmetic anywhere: every cast, shift and sum that could
/// overflow is explicitly bounded and falls through on overflow. The previous
/// implementation relied on catching <c>OverflowException</c>, which raised a
/// first-chance exception on every shader containing a literal above
/// <c>int.MaxValue</c> — and <c>0xFFFFFFFF</c> is everywhere.
/// </summary>
internal static class SlotExpressionDecomposer
{
    public static bool TryParse(uint operandId, ConstantValueMap constants, ResultIdTable definitions, out SlotExpression expression)
    {
        expression = null!;

        if (constants.TryGetValue(operandId, out uint constantValue))
        {
            // A literal above int.MaxValue cannot be a register offset — no
            // constant buffer has two billion registers — so treat it as opaque
            // and let the rest of the pipeline fall through to its extract path.
            expression = constantValue > int.MaxValue
                ? new SlotExpression { DynamicIndexId = operandId, DynamicIndexStride = 1 }
                : new SlotExpression { ConstantRegisterOffset = (int)constantValue };
            return true;
        }

        if (!TryDecompose(definitions, constants, operandId, out uint dynamicIndexId, out int stride, out int offset))
        {
            return false;
        }

        expression = new SlotExpression
        {
            DynamicIndexId = dynamicIndexId,
            DynamicIndexStride = stride,
            ConstantRegisterOffset = offset,
        };
        return true;
    }

    private static bool TryDecompose(
        ResultIdTable definitions,
        ConstantValueMap constants,
        uint valueId,
        out uint dynamicIndexId,
        out int dynamicStride,
        out int constantOffset)
    {
        dynamicIndexId = 0;
        dynamicStride = 0;
        constantOffset = 0;

        SpirvInstruction? definition = definitions.DefinitionOf(valueId);
        if (definition is null)
        {
            return false;
        }

        ushort opCode = definition.OpCode;

        if ((opCode == SpvOpCode.OpIAdd || opCode == SpvOpCode.OpISub || opCode == SpvOpCode.OpBitwiseOr)
            && definition.WordCount >= 5)
        {
            uint left = definition[3];
            uint right = definition[4];

            if (constants.TryGetValue(right, out uint rightConst)
                && rightConst <= int.MaxValue
                && TryDecompose(definitions, constants, left, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                long combined = opCode == SpvOpCode.OpISub
                    ? (long)constantOffset - (int)rightConst
                    : (long)constantOffset + (int)rightConst;
                if (combined >= int.MinValue && combined <= int.MaxValue)
                {
                    constantOffset = (int)combined;
                    return true;
                }
            }

            if ((opCode == SpvOpCode.OpIAdd || opCode == SpvOpCode.OpBitwiseOr)
                && constants.TryGetValue(left, out uint leftConst)
                && leftConst <= int.MaxValue
                && TryDecompose(definitions, constants, right, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                long combined = (long)constantOffset + (int)leftConst;
                if (combined >= int.MinValue && combined <= int.MaxValue)
                {
                    constantOffset = (int)combined;
                    return true;
                }
            }

            // dynamic+dynamic, overflow, or an oversized literal: fall through.
        }

        if ((opCode == SpvOpCode.OpIMul || opCode == SpvOpCode.OpShiftLeftLogical) && definition.WordCount >= 5)
        {
            uint left = definition[3];
            uint right = definition[4];

            if (constants.TryGetValue(right, out uint rightConst))
            {
                int stride = ComputeStride(opCode, rightConst);
                if (stride > 0)
                {
                    dynamicIndexId = left;
                    dynamicStride = stride;
                    constantOffset = 0;
                    return true;
                }
            }

            if (opCode == SpvOpCode.OpIMul
                && constants.TryGetValue(left, out uint leftConst)
                && leftConst > 0 && leftConst <= int.MaxValue)
            {
                dynamicIndexId = right;
                dynamicStride = (int)leftConst;
                constantOffset = 0;
                return true;
            }

            // dynamic*dynamic or an oversized stride: fall through.
        }

        dynamicIndexId = valueId;
        dynamicStride = 1;
        constantOffset = 0;
        return true;
    }

    // Returns 0 for "not representable" — a valid sentinel because no real
    // constant-buffer element has zero stride.
    private static int ComputeStride(ushort opCode, uint constantValue)
    {
        if (opCode == SpvOpCode.OpShiftLeftLogical)
        {
            // 1 << 31 sets the sign bit and stride is consumed as a positive
            // register count, so cap at 30 — a billion-register stride already
            // exceeds anything real.
            return constantValue > 30 ? 0 : 1 << (int)constantValue;
        }

        return constantValue > int.MaxValue ? 0 : (int)constantValue;
    }
}
