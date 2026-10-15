Shader "Continuum/AeroFieldFlow"
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
   float _ContinuumFlowFar;
   struct F { float4 position:SV_POSITION; float depth:TEXCOORD0; };
   F vert(float4 vertex:POSITION) { F o; o.position=UnityObjectToClipPos(vertex); o.depth=-UnityObjectToViewPos(vertex).z; return o; }
   float4 frag(F i):SV_Target { return float4(max(i.depth,0)/max(_ContinuumFlowFar,.001),0,0,1); }
   ENDCG
  }
 }
 Fallback Off
}
