Shader "Continuum/AeroFieldSceneDepthCopy"
{
 SubShader
 {
  Pass
  {
   ZWrite Off ZTest Always Cull Off
   CGPROGRAM
   #pragma target 3.0
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
   float4 _CameraDepthTexture_TexelSize;
   float4 frag(v2f_img i):SV_Target
   {
    float2 uv=i.uv;
    #if UNITY_UV_STARTS_AT_TOP
    if(_CameraDepthTexture_TexelSize.y<0) uv.y=1-uv.y;
    #endif
    float raw=SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,uv);
    #if defined(UNITY_REVERSED_Z)
    bool empty=raw<=0;
    #else
    bool empty=raw>=1;
    #endif
    return float4(empty?0:LinearEyeDepth(raw),0,0,1);
   }
   ENDCG
  }
 }
 Fallback Off
}
