static float4 TEXCOORD;
static float2 TEXCOORD_1;
static float4 SV_Target;

struct SPIRV_Cross_Input
{
    float4 TEXCOORD : TEXCOORD0;
    float2 TEXCOORD_1 : TEXCOORD1;
};

struct SPIRV_Cross_Output
{
    float4 SV_Target : SV_Target0;
};

void frag_main()
{
    SV_Target.x = TEXCOORD.x * TEXCOORD_1.x;
    SV_Target.y = TEXCOORD.y * TEXCOORD_1.y;
    SV_Target.z = 0.0f;
    SV_Target.w = TEXCOORD.w;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    TEXCOORD = stage_input.TEXCOORD;
    TEXCOORD_1 = stage_input.TEXCOORD_1;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.SV_Target = SV_Target;
    return stage_output;
}
