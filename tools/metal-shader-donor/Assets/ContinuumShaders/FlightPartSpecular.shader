Shader "Continuum/FlightPartSpecularInstanced"
{
 Properties
 {
  _MainTex ("Albedo", 2D) = "white" {}
  _BumpMap ("Normal", 2D) = "bump" {}
  _SpecMap ("Specular", 2D) = "white" {}
  _Emissive ("Emission", 2D) = "black" {}
  _Color ("Color", Color) = (1,1,1,1)
  _SpecColor ("Specular tint", Color) = (.5,.5,.5,1)
  _Shininess ("Specular exponent scale", Range(.001,1)) = .03
  _AmbientMultiplier ("Ambient multiplier", Float) = 1
  _Mapped ("Mapped family", Float) = 0
  _TemperatureColor ("Temperature", Color) = (0,0,0,0)
  _RimColor ("Highlight", Color) = (0,0,0,0)
  _RimFalloff ("Highlight exponent", Float) = 2
  _EmissiveColor ("Emission multiplier", Color) = (0,0,0,0)
  _Opacity ("Opaque admission", Float) = 1
 }
 SubShader
 {
  Tags { "RenderType"="Opaque" "Queue"="Geometry" }
  LOD 300
  CGPROGRAM
  #pragma target 3.0
  #pragma surface surf BlinnPhong fullforwardshadows addshadow
  #pragma multi_compile_instancing
  #include "UnityCG.cginc"
  sampler2D _MainTex, _BumpMap, _SpecMap, _Emissive;
  fixed4 _Color;
  half _Shininess, _AmbientMultiplier, _Mapped;
  UNITY_INSTANCING_BUFFER_START(Part)
   UNITY_DEFINE_INSTANCED_PROP(float4, _TemperatureColor)
   UNITY_DEFINE_INSTANCED_PROP(float4, _RimColor)
   UNITY_DEFINE_INSTANCED_PROP(float, _RimFalloff)
   UNITY_DEFINE_INSTANCED_PROP(float4, _EmissiveColor)
   UNITY_DEFINE_INSTANCED_PROP(float, _Opacity)
  UNITY_INSTANCING_BUFFER_END(Part)
  struct Input { float2 uv_MainTex; float2 uv_BumpMap; float2 uv_SpecMap; float2 uv_Emissive; float3 viewDir; UNITY_VERTEX_INPUT_INSTANCE_ID };
  void surf(Input input, inout SurfaceOutput output)
  {
   fixed4 albedo = tex2D(_MainTex, input.uv_MainTex) * _Color;
   output.Albedo = albedo.rgb;
   output.Normal = UnpackNormal(tex2D(_BumpMap, input.uv_BumpMap));
   output.Specular = _Shininess;
   output.Gloss = lerp(albedo.a, tex2D(_SpecMap, input.uv_SpecMap).r, _Mapped);
   float rim = 1 - saturate(dot(normalize(input.viewDir), output.Normal));
   float4 highlight = UNITY_ACCESS_INSTANCED_PROP(Part, _RimColor);
   float4 temperature = UNITY_ACCESS_INSTANCED_PROP(Part, _TemperatureColor);
   float4 emission = UNITY_ACCESS_INSTANCED_PROP(Part, _EmissiveColor);
   output.Emission = tex2D(_Emissive, input.uv_Emissive).rgb * emission.rgb + temperature.rgb +
       highlight.rgb * highlight.a * pow(rim, max(.001, UNITY_ACCESS_INSTANCED_PROP(Part, _RimFalloff)));
   output.Alpha = UNITY_ACCESS_INSTANCED_PROP(Part, _Opacity);
  }
  ENDCG
 }
 Fallback Off
}
