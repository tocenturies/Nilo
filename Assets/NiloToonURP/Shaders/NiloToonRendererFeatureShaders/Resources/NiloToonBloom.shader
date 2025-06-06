// A direct copy of URP10.7's Bloom.shader 
// all logic changes will have [NiloToon] tag marked

Shader "Hidden/Universal Render Pipeline/NiloToonBloom"
{
    HLSLINCLUDE
        #pragma exclude_renderers gles
        #pragma multi_compile_local _ _USE_RGBM
        #pragma multi_compile _ _USE_DRAW_PROCEDURAL

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Filtering.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"
        #include "../../../ShaderLibrary/NiloUtilityHLSL/NiloHSVRGBConvert.hlsl"

#if UNITY_VERSION < 202220
        #define _BlitTexture _SourceTex
        #define _BlitTexture_TexelSize _SourceTex_TexelSize
        TEXTURE2D_X(_SourceTex);    
#endif

#if UNITY_VERSION < 202320    
        float4 _BlitTexture_TexelSize;
#endif

        TEXTURE2D_X(_SourceTexLowMip);
        float4 _SourceTexLowMip_TexelSize;

        float4 _Params; // x: scatter, y: clamp, z: threshold (linear), w: threshold knee

        #define Scatter             _Params.x
        #define ClampMax            _Params.y
        #define Threshold           _Params.z
        #define ThresholdKnee       _Params.w

        // [NiloToon added]
        //==========================================================
        TEXTURE2D_X(_NiloToonPrepassBufferTex);

        float _NiloToonBloomCharacterAreaThreshold;
        float _NiloToonBloomCharacterAreaThresholdKnee;

        float _NiloToonBloomCharacterAreaBloomEmitMultiplier;

        half4 _NiloToonBloomHSVModifier;
        //==========================================================

        half4 EncodeHDR(half3 color)
        {
        #if _USE_RGBM
            half4 outColor = EncodeRGBM(color);
        #else
            half4 outColor = half4(color, 1.0);
        #endif

        #if UNITY_COLORSPACE_GAMMA
            return half4(sqrt(outColor.xyz), outColor.w); // linear to γ
        #else
            return outColor;
        #endif
        }

        half3 DecodeHDR(half4 color)
        {
        #if UNITY_COLORSPACE_GAMMA
            color.xyz *= color.xyz; // γ to linear
        #endif

        #if _USE_RGBM
            return DecodeRGBM(color);
        #else
            return color.xyz;
        #endif
        }

        half4 FragPrefilter(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        // SHADER_LIBRARY_VERSION_MAJOR is deprecated for Unity2022.2 or later, so we will use UNITY_VERSION instead
        // https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/main/URP_SimpleLitTemplate.shader#L145
    #if UNITY_VERSION >= 202220 // (for URP 14 or above)
        float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord); // URP14 changed the naming from uv to texcoord, see URP14's Runtime\Utilities\Blit.hlsl
    #else // (for below URP 14)
        float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
    #endif           

        #if _BLOOM_HQ
            float texelSize = _BlitTexture_TexelSize.x;
            half4 A = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(-1.0, -1.0));
            half4 B = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(0.0, -1.0));
            half4 C = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(1.0, -1.0));
            half4 D = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(-0.5, -0.5));
            half4 E = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(0.5, -0.5));
            half4 F = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(-1.0, 0.0));
            half4 G = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
            half4 H = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(1.0, 0.0));
            half4 I = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(-0.5, 0.5));
            half4 J = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(0.5, 0.5));
            half4 K = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(-1.0, 1.0));
            half4 L = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(0.0, 1.0));
            half4 M = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texelSize * float2(1.0, 1.0));

            half2 div = (1.0 / 4.0) * half2(0.5, 0.125);

            half4 o = (D + E + I + J) * div.x;
            o += (A + B + G + F) * div.y;
            o += (B + C + H + G) * div.y;
            o += (F + G + L + K) * div.y;
            o += (G + H + M + L) * div.y;

            half3 color = o.xyz;
        #else
            half3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).xyz;
        #endif

            // User controlled clamp to limit crazy high broken spec
            color = min(ClampMax, color);

            // [NiloToon added]
            //==========================================================
            float characterArea = SAMPLE_TEXTURE2D_X(_NiloToonPrepassBufferTex, sampler_LinearClamp, uv).g;
            float FinalThreshold = lerp(Threshold,_NiloToonBloomCharacterAreaThreshold,characterArea);
            float FinalThresholdKnee = lerp(ThresholdKnee, _NiloToonBloomCharacterAreaThresholdKnee, characterArea);
            float FinalEmitMultiplier = lerp(1,_NiloToonBloomCharacterAreaBloomEmitMultiplier, characterArea);
            //==========================================================

            // Thresholding
            half brightness = Max3(color.r, color.g, color.b);
            half softness = clamp(brightness - FinalThreshold + FinalThresholdKnee, 0.0, 2.0 * FinalThresholdKnee);
            softness = (softness * softness) / (4.0 * FinalThresholdKnee + 1e-4);
            half multiplier = max(brightness - FinalThreshold, softness) / max(brightness, 1e-4);
            color *= multiplier;

            // [NiloToon added]
            //==========================================================
            // boost saturation
            half3 postHSVColor = ApplyHSVChange(color, _NiloToonBloomHSVModifier.x, _NiloToonBloomHSVModifier.y, _NiloToonBloomHSVModifier.z);
            color = lerp(postHSVColor,lerp(color,postHSVColor,characterArea),_NiloToonBloomHSVModifier.w);

            color *= FinalEmitMultiplier;
            //==========================================================

            // Clamp colors to positive once in prefilter. Encode can have a sqrt, and sqrt(-x) == NaN. Up/Downsample passes would then spread the NaN.
            color = max(color, 0);
            return EncodeHDR(color);
        }

        half4 FragBlurH(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float texelSize = _BlitTexture_TexelSize.x * 2.0;


        // SHADER_LIBRARY_VERSION_MAJOR is deprecated for Unity2022.2 or later, so we will use UNITY_VERSION instead
        // https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/main/URP_SimpleLitTemplate.shader#L145
    #if UNITY_VERSION >= 202220 // (for URP 14 or above)
        float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord); // URP14 changed the naming from uv to texcoord, see URP14's Runtime\Utilities\Blit.hlsl
    #else // (for below URP 14)
        float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
    #endif  

            // 9-tap gaussian blur on the downsampled source
            half3 c0 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(texelSize * 4.0, 0.0)));
            half3 c1 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(texelSize * 3.0, 0.0)));
            half3 c2 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(texelSize * 2.0, 0.0)));
            half3 c3 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(texelSize * 1.0, 0.0)));
            half3 c4 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv                               ));
            half3 c5 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(texelSize * 1.0, 0.0)));
            half3 c6 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(texelSize * 2.0, 0.0)));
            half3 c7 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(texelSize * 3.0, 0.0)));
            half3 c8 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(texelSize * 4.0, 0.0)));

            half3 color = c0 * 0.01621622 + c1 * 0.05405405 + c2 * 0.12162162 + c3 * 0.19459459
                        + c4 * 0.22702703
                        + c5 * 0.19459459 + c6 * 0.12162162 + c7 * 0.05405405 + c8 * 0.01621622;

            return EncodeHDR(color);
        }

        half4 FragBlurV(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float texelSize = _BlitTexture_TexelSize.y;

        // SHADER_LIBRARY_VERSION_MAJOR is deprecated for Unity2022.2 or later, so we will use UNITY_VERSION instead
        // https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/main/URP_SimpleLitTemplate.shader#L145
    #if UNITY_VERSION >= 202220 // (for URP 14 or above)
        float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord); // URP14 changed the naming from uv to texcoord, see URP14's Runtime\Utilities\Blit.hlsl
    #else // (for below URP 14)
        float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
    #endif  

            // Optimized bilinear 5-tap gaussian on the same-sized source (9-tap equivalent)
            half3 c0 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(0.0, texelSize * 3.23076923)));
            half3 c1 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(0.0, texelSize * 1.38461538)));
            half3 c2 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv                                      ));
            half3 c3 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(0.0, texelSize * 1.38461538)));
            half3 c4 = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(0.0, texelSize * 3.23076923)));

            half3 color = c0 * 0.07027027 + c1 * 0.31621622
                        + c2 * 0.22702703
                        + c3 * 0.31621622 + c4 * 0.07027027;

            return EncodeHDR(color);
        }

        half3 Upsample(float2 uv)
        {
            half3 highMip = DecodeHDR(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv));

        #if _BLOOM_HQ && !defined(SHADER_API_GLES)
            half3 lowMip = DecodeHDR(SampleTexture2DBicubic(TEXTURE2D_X_ARGS(_SourceTexLowMip, sampler_LinearClamp), uv, _SourceTexLowMip_TexelSize.zwxy, (1.0).xx, unity_StereoEyeIndex));
        #else
            half3 lowMip = DecodeHDR(SAMPLE_TEXTURE2D_X(_SourceTexLowMip, sampler_LinearClamp, uv));
        #endif

            return lerp(highMip, lowMip, Scatter);
        }

        half4 FragUpsample(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            // SHADER_LIBRARY_VERSION_MAJOR is deprecated for Unity2022.2 or later, so we will use UNITY_VERSION instead
            // https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/main/URP_SimpleLitTemplate.shader#L145
        #if UNITY_VERSION >= 202220 // (for URP 14 or above)
            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord); // URP14 changed the naming from uv to texcoord, see URP14's Runtime\Utilities\Blit.hlsl
        #else // (for below URP 14)
            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
        #endif  

            half3 color = Upsample(UnityStereoTransformScreenSpaceTex(uv));
            return EncodeHDR(color);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Bloom Prefilter"

            HLSLPROGRAM
                #pragma vertex Vert // FullscreenVert
                #pragma fragment FragPrefilter
                #pragma multi_compile_local _ _BLOOM_HQ
            ENDHLSL
        }

        Pass
        {
            Name "Bloom Blur Horizontal"

            HLSLPROGRAM
                #pragma vertex Vert // FullscreenVert
                #pragma fragment FragBlurH
            ENDHLSL
        }

        Pass
        {
            Name "Bloom Blur Vertical"

            HLSLPROGRAM
                #pragma vertex Vert // FullscreenVert
                #pragma fragment FragBlurV
            ENDHLSL
        }

        Pass
        {
            Name "Bloom Upsample"

            HLSLPROGRAM
                #pragma vertex Vert // FullscreenVert
                #pragma fragment FragUpsample
                #pragma multi_compile_local _ _BLOOM_HQ
            ENDHLSL
        }
    }
}
