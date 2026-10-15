Shader "Hidden/Continuum/MultiscaleDepthCopy" {
SubShader { Cull Off ZWrite Off ZTest Always Pass {
CGPROGRAM
#pragma target 3.5
#pragma vertex vert_img
#pragma fragment frag
#include "UnityCG.cginc"
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture); float4 _CameraDepthTexture_TexelSize;
float frag(v2f_img i):SV_Target {
 float2 uv=i.uv;
 #if UNITY_UV_STARTS_AT_TOP
 if(_CameraDepthTexture_TexelSize.y<0) uv.y=1-uv.y;
 #endif
 float raw=SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,uv);
 #if defined(UNITY_REVERSED_Z)
 if(raw<=0) return 0;
 #else
 if(raw>=1) return 0;
 #endif
 if(unity_OrthoParams.w>0.5) {
  #if defined(UNITY_REVERSED_Z)
  raw=1-raw;
  #endif
  return max(0,lerp(_ProjectionParams.y,_ProjectionParams.z,raw));
 }
 return max(0,LinearEyeDepth(raw));
}
ENDCG
} }
}
