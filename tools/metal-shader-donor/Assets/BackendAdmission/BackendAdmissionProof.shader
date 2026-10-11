Shader "Hidden/Continuum/BackendAdmissionProof"
{
    SubShader
    {
        Pass
        {
            ZWrite Off ZTest Always Cull Off Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 frag(v2f_img input) : SV_Target { return float4(.125, .5, .875, 1); }
            ENDCG
        }
    }
    Fallback Off
}
