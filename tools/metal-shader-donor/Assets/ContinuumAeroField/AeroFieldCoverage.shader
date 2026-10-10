Shader "Continuum/AeroFieldCoverage"
{
 SubShader
 {
  Tags { "RenderType"="Opaque" }
  Pass
  {
   ZWrite On ZTest LEqual Cull Off
   CGPROGRAM
   #pragma target 3.0
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct V { float4 vertex:POSITION; float3 normal:NORMAL; };
   struct F { float4 position:SV_POSITION; float3 normal:TEXCOORD0; float depth:TEXCOORD1; };
   F vert(V v) { F o; o.position=UnityObjectToClipPos(v.vertex); o.normal=UnityObjectToWorldNormal(v.normal); o.depth=-UnityObjectToViewPos(v.vertex).z; return o; }
   float4 frag(F i, float facing:VFACE):SV_Target
   {
    // Two-sided packets retain the visible face's oriented geometric normal.
    float3 n=normalize(i.normal)*(facing>=0?1:-1);
    return float4(n*.5+.5,max(i.depth,0));
   }
   ENDCG
  }
 }
 Fallback Off
}
