#ifndef CONTINUUM_AERO_FIELD_INCLUDED
#define CONTINUUM_AERO_FIELD_INCLUDED
#include "UnityCG.cginc"
sampler2D _ContinuumViewField, _ContinuumSceneDepth, _ContinuumFlowField, _ContinuumNoiseTex;
float4 _ContinuumViewField_TexelSize;
float4x4 _ContinuumViewInverseProjection, _ContinuumViewCameraToWorld;
float4x4 _ContinuumFlowViewProjection, _ContinuumFlowWorldToView;
float _ContinuumFlowFar;
float4 _ContinuumDepthParams, _ContinuumEffectColor, _ContinuumEffectDirection;
float4 _ContinuumEffectParameters, _ContinuumTextureParameters, _ContinuumFalloffs;
float4 _ContinuumSeedRect;
float2 FieldUV(float2 uv)
{
 #if UNITY_UV_STARTS_AT_TOP
 if (_ContinuumViewField_TexelSize.y<0) uv.y=1-uv.y;
 #endif
 return uv;
}
float3 FieldWorld(float2 uv, float depth)
{
 // Inverse projection is the camera's GPU projection; the producer supplies its RT orientation.
 float4 q=mul(_ContinuumViewInverseProjection,float4(uv*2-1,UNITY_NEAR_CLIP_VALUE,1));
 float3 ray=q.xyz/q.w;
 return mul(_ContinuumViewCameraToWorld,float4(ray*(depth/max(-ray.z,.00001)),1)).xyz;
}
float Exposed(float3 world, float3 normal)
{
 float4 projected=mul(_ContinuumFlowViewProjection,float4(world,1));
 float2 uv=projected.xy/projected.w*.5+.5;
 if (any(uv<0)||any(uv>1)) return 0;
 float stored=tex2Dlod(_ContinuumFlowField,float4(uv,0,0)).r*_ContinuumFlowFar;
 float depth=-mul(_ContinuumFlowWorldToView,float4(world,1)).z;
 float bias=max(.02,_ContinuumFlowFar/2048.0);
 float visible=(stored>0&&depth>0&&depth<=stored+bias)?1:0;
 // effectDirection points downstream, hence upstream-facing surfaces oppose it.
 float incidence=saturate(dot(normal,-normalize(_ContinuumEffectDirection.xyz)));
 return visible*pow(incidence,max(.25,_ContinuumEffectParameters.w));
}
float Unoccluded(float2 uv,float depth)
{
 float scene=tex2D(_ContinuumSceneDepth,uv).r;
 return scene<=0||depth<=scene+max(.02,depth*.0001)?1:0;
}
#endif
