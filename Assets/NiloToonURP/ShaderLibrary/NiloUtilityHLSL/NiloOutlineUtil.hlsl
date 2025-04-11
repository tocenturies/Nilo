// SPDX-License-Identifier: (Not available for this version, you are only allowed to use this software if you have express permission from the copyright holder and agreed to the latest NiloToonURP EULA)
// Copyright (c) 2021 Kuroneko ShaderLab Limited

// For more information, visit -> https://github.com/ColinLeung-NiloCat/UnityURPToonLitShaderExample

// #pragma once is a safe guard best practice in almost every .hlsl, 
// doing this can make sure your .hlsl's user can include this .hlsl anywhere anytime without producing any multi include conflict
#pragma once

// classic outline's width fov & cam distance control, similar concept to
// https://docs.google.com/presentation/d/e/2PACX-1vSLQNQyqfGCVsqcEuOJLFqvHpASQZ5UZhjAuWnS5C3tYSGWjpmGYmI9ZOkt36hGGe3mWYXqxJgjCCAz/pub?start=false&loop=false&delayms=3000&fbclid=IwZXh0bgNhZW0CMTAAAR2KgeLNQqQWjE4EHIBhXgnB0xBHOzwhl8oWyfdRM5VU6AKIcjbaTVjvYPI_aem_AYGshQfPSTNeG2PDowtL2m6whirj2ruJvpbkQnTaR4CWiAYRRpACRmf64m91pAfFf5c_S6Na9GavPEVG53mxxuso&slide=id.ga37e29a62e_3_386

// possible inner outline concept:
// - vertex color sdf step = fragment shader inner outline with AA (texture size independent)
// - can use the same classic outline color logic for inner outline, just like ss outline
// https://docs.google.com/presentation/d/e/2PACX-1vSLQNQyqfGCVsqcEuOJLFqvHpASQZ5UZhjAuWnS5C3tYSGWjpmGYmI9ZOkt36hGGe3mWYXqxJgjCCAz/pub?start=false&loop=false&delayms=3000&fbclid=IwZXh0bgNhZW0CMTAAAR2KgeLNQqQWjE4EHIBhXgnB0xBHOzwhl8oWyfdRM5VU6AKIcjbaTVjvYPI_aem_AYGshQfPSTNeG2PDowtL2m6whirj2ruJvpbkQnTaR4CWiAYRRpACRmf64m91pAfFf5c_S6Na9GavPEVG53mxxuso&slide=id.ga37e29a62e_3_400

// love live mobile(2020)'s outline data:
// R = outline width
// g = outline zoffset
// b = inner outline weight
float ApplyOutlineFadeOutPerspectiveCamera(float inputMulFix, float cameraFov)
{
    // make outline "fadeout" if character is too small/far in camera's view
    // imagine this line is the most simple way to do tone mapping that clamp at 60/cameraFov,
    // but here we are not remapping HDR color, we are remapping outline width
    return min(60/cameraFov, inputMulFix); // keep it similar to min(2,inputMulFix) in fov 30 camera
}
float ApplyOutlineFadeOutOrthoCamera(float inputMulFix)
{
    // make outline "fadeout" if character is too small in camera's view
    // imagine this line is the most simple way to do tone mapping that clamp at 2,
    // but here we are not remapping HDR color, we are remapping outline width
    return min(2,inputMulFix);
}
float GetOutlineCameraFovAndDistanceFixMultiplier(float positionVS_Z, float cameraFOV, float applyPercentage)
{
    float outlineWidthMulFix;
    if(IsPerspectiveProjection())
    {
        ////////////////////////////////
        // Perspective camera case
        ////////////////////////////////

        // keep outline similar width on screen across all camera distance
        outlineWidthMulFix = abs(positionVS_Z);

        // can replace to a better tonemapping function if a smooth stop is needed
        outlineWidthMulFix = ApplyOutlineFadeOutPerspectiveCamera(outlineWidthMulFix,cameraFOV);

        // keep outline similar width on screen accoss all camera fov
        outlineWidthMulFix *= cameraFOV;     
    }
    else
    {
        ////////////////////////////////
        // Orthographic camera case
        ////////////////////////////////

        // There is no need to consider camera distance or field of view (FOV) for an orthographic camera, as they don't need to be taken into account.
        float orthoSize = abs(unity_OrthoParams.y);
        orthoSize = ApplyOutlineFadeOutOrthoCamera(orthoSize);
        outlineWidthMulFix = orthoSize * 50; // 100/2 is a magic number to match perspective camera's outline width
    }

    // allow user to select applying how much of this auto outline width adjustment(this function)
    outlineWidthMulFix = lerp(60,outlineWidthMulFix, saturate(applyPercentage));

    return outlineWidthMulFix * 0.00005; // mul a const to make return result = default normal expand amount WS
}

// [currently not being used in NiloToonURP]
// If your project has a faster way to get camera fov in shader, you don't need to use this method.
// For example, you write cmd.SetGlobalFloat("_CurrentCameraFOV",cameraFOV) using a new RendererFeature in C# 
// (NiloToonURP's renderer feature did that already by providing _CurrentCameraFOV).
float GetCameraFOV()
{
    // https://answers.unity.com/questions/770838/how-can-i-extract-the-fov-information-from-the-pro.html
    float t = unity_CameraProjection._m11;
    float Rad2Deg = 180 / 3.1415;
    float fov = atan(1.0f / t) * 2.0 * Rad2Deg;
    return fov;
}
// [currently not being used in NiloToonURP]
// slower due to GetCameraFOV(), but don't need to provide cameraFOV from C#
float GetOutlineCameraFovAndDistanceFixMultiplier(float positionVS_Z, float applyPercentage = 1)
{
    return GetOutlineCameraFovAndDistanceFixMultiplier(positionVS_Z, GetCameraFOV(), applyPercentage);
}
