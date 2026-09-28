// SPDX-License-Identifier: MIT

// Fullscreen pass that copies the camera depth attachment into a capture target
// (used by SplatPresso.Rendering.SplatCaptureFeature):
// - color output (R32_SFloat): the raw depth value, for CPU readback
// - SV_Depth: the same raw depth, priming the capture target's own depth attachment so that the following
//   splat depth pass (ZTest LEqual) is properly occluded by scene geometry.
// Draw with 3 vertices (fullscreen triangle), ZTest Always. The source must be a non-MSAA depth texture of the
// same size as the target (texel-exact Load, no sampling).
Shader "Hidden/SplatPresso/Scene Depth Prime"
{
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest Always
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag

Texture2D<float> _SplatPressoSceneDepthTex;

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

float frag (v2f i, out float outDepth : SV_Depth) : SV_Target
{
    float raw = _SplatPressoSceneDepthTex.Load(int3(i.vertex.xy, 0)).r;
    outDepth = raw;
    return raw;
}
ENDCG
        }
    }
    Fallback Off
}
