// SPDX-License-Identifier: MIT
// Adapted from aras-p/UnityGaussianSplatting (MIT) RenderGaussianSplats.shader, via the SplatPresso research fork.

// Renders gaussian splats into a depth capture target (used by SplatPresso.Rendering.SplatCaptureFeature).
// Unlike the main splat shader this one writes to the depth buffer (ZWrite On, no blending): each splat quad
// writes its CENTRE depth for pixels where the gaussian falloff alpha exceeds _SplatPressoDepthAlphaThreshold.
// The color output is the raw (post-projection) depth value, so the bound R32_SFloat color target ends up
// holding the same raw depth as the depth attachment, ready for readback. Linearization to eye space happens
// on the CPU.
// Reads the per-splat view data the Gaussian Splatting URP pass computed for this camera earlier in the frame.
// There is no _OrderBuffer: with depth testing the result does not depend on draw order, so the instance id
// indexes the view data directly. Draw with a 6-index quad {0,1,2,1,3,2}, one instance per splat.
Shader "Hidden/SplatPresso/Splat Depth"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

// Resolved through the package NAME, so it works for git, registry, file: and embedded installs.
#include "Packages/org.nesnausk.gaussian-splatting/Shaders/GaussianSplatting.hlsl"

StructuredBuffer<SplatViewData> _SplatViewData;
float _SplatPressoDepthAlphaThreshold;

struct v2f
{
    half opacity : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    SplatViewData view = _SplatViewData[instID];
    float4 centerClipPos = view.pos;
    bool behindCam = centerClipPos.w <= 0;
    if (behindCam)
    {
        o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
    }
    else
    {
        o.opacity = f16tof32(view.color.y); // splat opacity (already includes opacity scale)

        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        quadPos *= 2;

        o.pos = quadPos;

        float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
        o.vertex = centerClipPos;
        o.vertex.xy += deltaScreenPos * centerClipPos.w;
    }
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

float frag (v2f i) : SV_Target
{
    half alpha = exp(-dot(i.pos, i.pos)) * i.opacity;
    if (alpha < _SplatPressoDepthAlphaThreshold)
        discard;
    // SV_POSITION.z in the fragment stage is the raw depth value that also goes into the depth buffer;
    // write the same value into the color target for CPU readback.
    return i.vertex.z;
}
ENDCG
        }
    }
    Fallback Off
}
