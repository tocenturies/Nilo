using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NiloToon.NiloToonURP
{
    public class NiloToonAverageShadowTestRTPass : ScriptableRenderPass
    {
        // singleton
        public static NiloToonAverageShadowTestRTPass Instance => _instance;
        static NiloToonAverageShadowTestRTPass _instance;

        [Serializable]
        public class Settings
        {
            [Tooltip(   "If you want NiloToon character to receive URP shadow map in an extremely soft and blurry way(a special URP shadow sampling that is blurry across the whole character, darken the character uniformly), turn this on.\n" +
                        "When turned on, character won't receive main directional light's direct lighting when occluded by URP's shadow casters (e.g. character completely under a bridge, where the bridge is casting URP shadow).\n\n" +
                        "Default is OFF, since some users don't want this kind of shadow ON by default when character is completely indoor")]
            [OverrideDisplayName("Enable?")]
            public bool enableAverageShadow = false;
        }

        public Settings settings;

        // for most game type, 128 is a big enough number, but still not affecting performance
        // maximum 127 active nilotoon characters can be inside the same scene(including any active character in scene, no camera culling), the last slot is reserved for camera's anime postprocess 
        const int MAX_SHADOW_SLOT_COUNT = 128;

        Material material;
        float[] shaderDataArray;

#if UNITY_2022_2_OR_NEWER
        RTHandle shadowTestResultRTH;
#else
        RenderTargetHandle shadowTestResultRTH;
#endif

        static readonly int _SphereShadowMapRT_SID = Shader.PropertyToID("_SphereShadowMapRT");

        public NiloToonAverageShadowTestRTPass(NiloToonRendererFeatureSettings allSettings)
        {
            this.settings = allSettings.sphereShadowTestSettings;

#if !UNITY_2022_2_OR_NEWER
            shadowTestResultRTH.Init("_NiloToonAverageShadowMapRT");
#endif

            shaderDataArray = new float[MAX_SHADOW_SLOT_COUNT * 4]; // each slot has 4 data, total of 128 slots. (default with (0,0,0,0))

            _instance = this;

            base.profilingSampler = new ProfilingSampler(nameof(NiloToonAverageShadowTestRTPass));
        }

        // This method is called before executing the render pass.
        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in a performant manner.
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // [possible optimization note]
            // in URP12(Unity2021.3), even there is no shadow caster to draw, URP will still allocate and clear a small 1x1 RT, used as shadowmap
            // see URP12 -> MainLightShadowCasterPass.cs's SetupForEmptyRendering().

            // here we can't do this optimization, since character shader will LOAD texture using uv = (index,0),
            // this texture must be 128x1
            int RTwidth = MAX_SHADOW_SLOT_COUNT;

            // RT width: use MAX_SHADOW_SLOT_COUNT as RT width, each pixel represent 1 NiloToon character's average shadow(the last pixel is camera's average shadow)
            // RT height: RT height is 1
            // RTFormat: RT format is RFloat, because we want to store a 0~1 average shadowAttenuation value
            // don't need depthbuffer/stencil/mipmap
            RenderTextureDescriptor renderTextureDescriptor = new RenderTextureDescriptor(RTwidth, 1, RenderTextureFormat.RFloat, 0, 1);

            // it is linear(non color) data
            renderTextureDescriptor.sRGB = false;

            renderTextureDescriptor.depthBufferBits = 0;
            renderTextureDescriptor.depthStencilFormat = GraphicsFormat.None;
            
            // Some Samsung phones didn't support GraphicsFormat.R16_UNorm, so we need to do a full fallback chain
            // Devices that can't support R16_UNorm = Galaxy S8, S7, and S21
            if(SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm,FormatUsage.Render))
            {
                renderTextureDescriptor.graphicsFormat = GraphicsFormat.R16_UNorm;
            }
            else if (SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, FormatUsage.Render))
            {
                renderTextureDescriptor.graphicsFormat = GraphicsFormat.R8_UNorm;
            }
            else if (SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, FormatUsage.Render))
            {
                renderTextureDescriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
            }
            else
            {
                Debug.LogError($"NiloToon: The creation of R8G8B8A8_UNorm RenderTexture of {nameof(NiloToonAverageShadowTestRTPass)} failed, contact email/discord for help.");
                return;
            }

            // we need each pixel providing a per character average shadow attenuation value when sampling, so FilterMode is Point
#if UNITY_2022_2_OR_NEWER
            RenderingUtils.ReAllocateIfNeeded(ref shadowTestResultRTH, renderTextureDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_NiloToonAverageShadowMapRT");
            cmd.SetGlobalTexture(shadowTestResultRTH.name, shadowTestResultRTH.nameID);
#else
            cmd.GetTemporaryRT(shadowTestResultRTH.id, renderTextureDescriptor, FilterMode.Point);
            cmd.SetGlobalTexture(_SphereShadowMapRT_SID, shadowTestResultRTH.Identifier());
#endif
            // still need to clear RT to white even average shadow is not enabled 
            // (in character shader we removed average shadow's multi_compile to save memory & build time/size, so character shader is always sampling this RT)
#if UNITY_2022_2_OR_NEWER
            ConfigureTarget(shadowTestResultRTH);
#else
            ConfigureTarget(shadowTestResultRTH.Identifier());
#endif
            ConfigureClear(ClearFlag.Color, Color.white); // default white = default no shadow
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!shouldRenderRT(renderingData)) return;

            renderPerCharacterAverageShadowAtlaRT(context, renderingData);
        }

        // Cleanup any allocated resources that were created during the execution of this render pass.
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
#if !UNITY_2022_2_OR_NEWER
            cmd.ReleaseTemporaryRT(shadowTestResultRTH.id);
#endif
        }

#if UNITY_2022_2_OR_NEWER
        public void Dispose()
        {
            shadowTestResultRTH?.Release();
        }
#endif
        bool shouldRenderRT(RenderingData renderingData)
        {
            // TODO:
            // WebGL is not safe to run big foploop in shader
            // remove this when the problem is solved
#if UNITY_WEBGL
            return false;
#endif

            if (renderingData.cameraData.cameraType == CameraType.Preview)
                return false;

            var shadowControlVolumeEffect = VolumeManager.instance.stack.GetComponent<NiloToonShadowControlVolume>();

            // in NiloToon 0.11.1, we changed the merge method from simple override to a "&&" merge, so renderer feature can force disable all average shadow even if volume has overridden and enabled it.
            bool enableAverageShadow = shadowControlVolumeEffect.enableCharAverageShadow.value && settings.enableAverageShadow;
            return enableAverageShadow;
        }
        private void renderPerCharacterAverageShadowAtlaRT(ScriptableRenderContext context, RenderingData renderingData)
        {
            // delay CreateEngineMaterial to as late as possible, to make it safe when ReimportAll is running
            if (!material)
                material = CoreUtils.CreateEngineMaterial("Hidden/NiloToon/AverageShadowTestRT");

            // sometimes the shader is not yet compile when first time opening the project or opening the project after deleting Library folder,
            // if material is not ready, cmd.DrawMesh will produce "Invalid pass" error log, so we need to skip it.
            if (!material)
                return;


            // NOTE: Do NOT mix ProfilingScope with named CommandBuffers i.e. CommandBufferPool.Get("name").
            // Currently there's an issue which results in mismatched markers.
            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, base.profilingSampler))
            {
                /*
                Note : should always ExecuteCommandBuffer at least once before using
                ScriptableRenderContext functions (e.g. DrawRenderers) even if you
                don't queue any commands! This makes sure the frame debugger displays
                everything under the correct title.
                */
                // https://www.cyanilux.com/tutorials/custom-renderer-features/?fbclid=IwAR27j2f3VVo0IIYDa32Dh76G9KPYzwb8j1J5LllpSnLXJiGf_UHrQ_lDtKg
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                
                // TODO: shadow will be wrong if number of active character in scene is more than MAX_SHADOW_SLOT_COUNT-1

                // reserve the right most slot for camera, other slots for each active character
                for (int i = 0; i < Mathf.Min(MAX_SHADOW_SLOT_COUNT - 1, NiloToonAllInOneRendererFeature.characterList.Count); i++)
                {
                    NiloToonPerCharacterRenderController controller = NiloToonAllInOneRendererFeature.characterList[i];

                    if (controller)
                    {
                        Vector3 centerPosWS = controller.GetCharacterBoundCenter();
                        float radiusWS = controller.GetCharacterBoundRadius();

                        shaderDataArray[i * 4 + 0] = centerPosWS.x;
                        shaderDataArray[i * 4 + 1] = centerPosWS.y;
                        shaderDataArray[i * 4 + 2] = centerPosWS.z;
                        shaderDataArray[i * 4 + 3] = radiusWS;
                    }
                    else
                    {
                        shaderDataArray[i * 4 + 3] = 0;
                    }
                }

                // RT's right most slot(pixel) for camera only
                Camera cam = renderingData.cameraData.camera;
                Vector3 cameraPosForTesting = cam.transform.position + cam.transform.forward * (cam.nearClipPlane + 1f); // add 1 unit offset to not use cam pos as testing center directly, to avoid wrong cascade shadowmap index(avoid testing outside of camera frustrum)
                shaderDataArray[(MAX_SHADOW_SLOT_COUNT - 1) * 4 + 0] = cameraPosForTesting.x;
                shaderDataArray[(MAX_SHADOW_SLOT_COUNT - 1) * 4 + 1] = cameraPosForTesting.y;
                shaderDataArray[(MAX_SHADOW_SLOT_COUNT - 1) * 4 + 2] = cameraPosForTesting.z;
                shaderDataArray[(MAX_SHADOW_SLOT_COUNT - 1) * 4 + 3] = 0.5f; // width is 0.5 unit, to avoid testing outside of camera frustrum

                var shadowControlVolumeEffect = VolumeManager.instance.stack.GetComponent<NiloToonShadowControlVolume>();

                cmd.SetGlobalFloatArray("_GlobalAverageShadowTestBoundingSphereDataArray", shaderDataArray); // once set, we can't change the size of array in GPU anymore, it is not Unity's fault but graphics API's design.
                cmd.SetGlobalFloat("_GlobalAverageShadowStrength", shadowControlVolumeEffect.charAverageShadowStrength.value);

#if UNITY_2022_2_OR_NEWER
                Blitter.BlitTexture(cmd, shadowTestResultRTH, shadowTestResultRTH, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, material, 0);
#else
                cmd.Blit(null, shadowTestResultRTH.Identifier(), material);
#endif
            }
            
            // must write these line after using{} finished, to ensure profiler and frame debugger display correctness
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
    }
}