Shader "Continuum/FixtureOpaque"
{
 Properties { _Color("Fixture color",Color)=(.25,.28,.32,1) }
 SubShader
 {
  Tags { "RenderType"="Opaque" "Queue"="Geometry" }
  Pass
  {
   ZWrite On ZTest LEqual Cull Back
   CGPROGRAM
   #pragma target 3.0
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   float4 _Color;
   struct F { float4 position:SV_POSITION; float3 normal:TEXCOORD0; };
   F vert(appdata_base v) { F o; o.position=UnityObjectToClipPos(v.vertex); o.normal=UnityObjectToWorldNormal(v.normal); return o; }
   float4 frag(F i):SV_Target { return float4(_Color.rgb*(.3+.7*saturate(dot(normalize(i.normal),normalize(float3(.3,.7,-.5))))),_Color.a); }
   ENDCG
  }
  Pass
  {
   Tags { "LightMode"="ShadowCaster" }
   ZWrite On ZTest LEqual Cull Back
   CGPROGRAM
   #pragma target 3.0
   #pragma vertex vert
   #pragma fragment frag
   #pragma multi_compile_shadowcaster
   #include "UnityCG.cginc"
   struct F { V2F_SHADOW_CASTER; };
   F vert(appdata_base v) { F o; TRANSFER_SHADOW_CASTER_NORMALOFFSET(o); return o; }
   float4 frag(F i):SV_Target { SHADOW_CASTER_FRAGMENT(i) }
   ENDCG
  }
 }
 Fallback Off
}
