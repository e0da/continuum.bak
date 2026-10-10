Shader "Continuum/FlightPartMappedInstanced"
{
 Properties
 {
  _MainTex ("Albedo", 2D) = "white" {}
  _BumpMap ("Normal", 2D) = "bump" {}
  _SpecMap ("Specular", 2D) = "white" {}
  _Emissive ("Emission", 2D) = "black" {}
  _BurnColor ("Burn color", Color) = (1,1,1,1)
  _SpecTint ("Mapped emission", Float) = 0
  _Shininess ("Smoothness", Range(0,1)) = .03
  _AmbientMultiplier ("Ambient multiplier", Float) = 1
  _TemperatureColor ("Temperature", Color) = (0,0,0,0)
  _RimColor ("Highlight", Color) = (0,0,0,0)
  _RimFalloff ("Highlight exponent", Float) = 2
  _EmissiveColor ("Emission multiplier", Color) = (0,0,0,0)
  _Opacity ("Effective opacity", Float) = 1
 }
 SubShader
 {
  Tags { "RenderType"="Opaque" "Queue"="Geometry" }
  LOD 300
  CGPROGRAM
  #pragma target 3.5
  #pragma surface surf StandardSpecular fullforwardshadows addshadow keepalpha
  #pragma multi_compile_instancing
  #include "UnityCG.cginc"
  sampler2D _MainTex, _BumpMap, _SpecMap, _Emissive;
  float4 _BurnColor;
  float _SpecTint, _Shininess, _AmbientMultiplier;
  float _SpecularAmbientBoostDiffuse, _SpecularAmbientBoostEmissive;
  float _UnderwaterFogFactor, _UnderwaterMaxAlbedoFog, _UnderwaterAlbedoDistanceScalar;
  float _UnderwaterMaxAlphaFog, _UnderwaterMinAlphaFogDistance, _UnderwaterAlphaDistanceScalar;
  float4 _UnderwaterFogColor, _LocalCameraPos, _LocalCameraDir;
  UNITY_INSTANCING_BUFFER_START(Part)
   UNITY_DEFINE_INSTANCED_PROP(float4, _TemperatureColor)
   UNITY_DEFINE_INSTANCED_PROP(float4, _RimColor)
   UNITY_DEFINE_INSTANCED_PROP(float, _RimFalloff)
   UNITY_DEFINE_INSTANCED_PROP(float4, _EmissiveColor)
   UNITY_DEFINE_INSTANCED_PROP(float, _Opacity)
  UNITY_INSTANCING_BUFFER_END(Part)
  struct Input { float2 uv_MainTex; float2 uv_BumpMap; float2 uv_SpecMap; float2 uv_Emissive; float3 viewDir; float3 worldPos; float4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
  void surf(Input input, inout SurfaceOutputStandardSpecular output)
  {
   float3 albedo = tex2D(_MainTex, input.uv_MainTex).rgb * _BurnColor.rgb * input.color.rgb;
   albedo *= 1 + _SpecularAmbientBoostDiffuse * _AmbientMultiplier;
   float4 bump = tex2D(_BumpMap, input.uv_BumpMap);
   output.Normal.xy = bump.wy * 2 - 1;
   output.Normal.z = sqrt(1 - min(dot(output.Normal.xy, output.Normal.xy), 1));
   output.Specular = tex2D(_SpecMap, input.uv_SpecMap).rgb;
   output.Smoothness = _Shininess;
   output.Occlusion = 1;
   float rim = 1 - saturate(dot(normalize(input.viewDir), output.Normal));
   float4 highlight = UNITY_ACCESS_INSTANCED_PROP(Part, _RimColor);
   float4 temperature = UNITY_ACCESS_INSTANCED_PROP(Part, _TemperatureColor);
   float4 emission = UNITY_ACCESS_INSTANCED_PROP(Part, _EmissiveColor);
   float3 glow = albedo * _SpecularAmbientBoostEmissive + output.Specular * _SpecTint +
       tex2D(_Emissive, input.uv_Emissive).rgb * emission.rgb * emission.a + temperature.rgb * temperature.a +
       highlight.rgb * highlight.a * pow(rim, UNITY_ACCESS_INSTANCED_PROP(Part, _RimFalloff));
   float distanceToCamera = length(input.worldPos - _LocalCameraPos.xyz);
   float underwater = _LocalCameraDir.w * _UnderwaterFogFactor;
   output.Albedo = lerp(albedo, _UnderwaterFogColor.rgb, underwater * _UnderwaterMaxAlbedoFog * saturate(distanceToCamera * _UnderwaterAlbedoDistanceScalar));
   output.Alpha = UNITY_ACCESS_INSTANCED_PROP(Part, _Opacity) * (1 - underwater * _UnderwaterMaxAlphaFog * saturate((distanceToCamera - _UnderwaterMinAlphaFogDistance) * _UnderwaterAlphaDistanceScalar));
   output.Emission = glow * output.Alpha;
  }
  ENDCG
 }
 Fallback Off
}
