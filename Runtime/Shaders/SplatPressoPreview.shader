// SPDX-License-Identifier: MIT

// Unlit, alpha-blended color for the placement preview ("hologram") boxes. Shipped with the package and
// referenced from a serialized field so it is included in builds (URP/Unlit is stripped unless something
// references it, and Shader.Find returns null for stripped shaders in players).
// Output = _BaseColor * _Color. _BaseColor is the main color (Material.color writes it); _Color is an extra
// tint that stays white unless set. No LightMode tag: URP renders the pass as SRPDefaultUnlit.
Shader "SplatPresso/Preview"
{
    Properties
    {
        [MainColor] _BaseColor ("Color", Color) = (0.35, 0.8, 1.0, 0.35)
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma multi_compile_instancing
#include "UnityCG.cginc"

fixed4 _BaseColor;
fixed4 _Color;

struct appdata
{
    float4 vertex : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2f
{
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

v2f vert (appdata v)
{
    v2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.vertex = UnityObjectToClipPos(v.vertex);
    return o;
}

fixed4 frag (v2f i) : SV_Target
{
    return _BaseColor * _Color;
}
ENDCG
        }
    }
    Fallback Off
}
