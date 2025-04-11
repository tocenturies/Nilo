// A copy and edited version of Unity6(Unity2023.3)'s motion vector pass hlsl

#ifndef NILOTOON_OBJECT_MOTION_VECTORS_INCLUDED
#define NILOTOON_OBJECT_MOTION_VECTORS_INCLUDED

#pragma target 3.5

#pragma vertex vert
#pragma fragment frag

//--------------------------------------
// GPU Instancing
//#pragma multi_compile_instancing
//#include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

//-------------------------------------
// Other pragmas
#include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"

// -------------------------------------
// Includes
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityInput.hlsl"

#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MotionVectorsCommon.hlsl"

// -------------------------------------
//#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

// -------------------------------------
// Structs
struct MotionVectorAttributes
{
    float4 position             : POSITION;
#if _ALPHATEST_ON
    float2 uv                   : TEXCOORD0;
#endif
    float3 positionOld          : TEXCOORD4;
#if _ADD_PRECOMPUTED_VELOCITY
    float3 alembicMotionVector  : TEXCOORD5;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct MotionVectorVaryings
{
    float4 positionCS                 : SV_POSITION;
    float4 positionCSNoJitter         : POSITION_CS_NO_JITTER;
    float4 previousPositionCSNoJitter : PREV_POSITION_CS_NO_JITTER;
#if _ALPHATEST_ON
    float2 uv                         : TEXCOORD0;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// -------------------------------------
//TEXTURE2D(_BaseMap);
//SAMPLER(sampler_BaseMap);
//float4 _BaseMap_TexelSize;

// -------------------------------------
// Nilo Removed: We use our own CBuffer, to ensure the CBuffer is the same with other passes = correctly SRP batching  
/*
CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _Cutoff;
    //half _Surface;
    //UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END
*/

// -------------------------------------
// Vertex
MotionVectorVaryings vert(MotionVectorAttributes input)
{
    MotionVectorVaryings output = (MotionVectorVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    const VertexPositionInputs vertexInput = GetVertexPositionInputs(input.position.xyz);

    #if defined(_ALPHATEST_ON)
        output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    #endif

    // Jittered. Match the frame.
    output.positionCS = vertexInput.positionCS;
    output.positionCSNoJitter = mul(_NonJitteredViewProjMatrix, mul(UNITY_MATRIX_M, input.position));

    float4 prevPos = (unity_MotionVectorsParams.x == 1) ? float4(input.positionOld, 1) : input.position;

#if _ADD_PRECOMPUTED_VELOCITY
    prevPos = prevPos - float4(input.alembicMotionVector, 0);
#endif

    output.previousPositionCSNoJitter = mul(_PrevViewProjMatrix, mul(UNITY_PREV_MATRIX_M, prevPos));

    ApplyMotionVectorZBias(output.positionCS);

    return output;
}

// -------------------------------------
// Fragment
float4 frag(MotionVectorVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    #if defined(_ALPHATEST_ON)

    // URP original:
    //Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff); // TODO: use GetCombinedBaseColor()

    // Nilo implementation:
    Varyings varings = (Varyings)0;
    UVData uvData = (UVData)0;
    uvData.allUVs[0] = input.uv; // TODO: send all UVs
    half4 baseColor = GetFinalBaseColor(varings,uvData,1);
    DoClipTestToTargetAlphaValue(baseColor.a);
    #endif

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return float4(CalcNdcMotionVectorFromCsPositions(input.positionCSNoJitter, input.previousPositionCSNoJitter), 0, 0);
}


#endif // NILOTOON_OBJECT_MOTION_VECTORS_INCLUDED
