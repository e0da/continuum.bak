Shader "Hidden/Continuum/PersistentTrail"
{
 SubShader
 {
  Pass
  {
   Cull Off ZWrite Off ZTest Always Blend One OneMinusSrcAlpha
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.0
   #include "UnityCG.cginc"
   float4x4 _TrailVP, _TrailView;
   sampler2D _CraftDepth, _BodyDepth;
   struct Input { float4 vertex:POSITION; float2 uv:TEXCOORD0; float4 color:COLOR; };
   struct Output { float4 position:SV_POSITION; float2 uv:TEXCOORD0; float4 color:COLOR; float4 screen:TEXCOORD1; float depth:TEXCOORD2; };
   Output vert(Input v) { Output o; o.position=mul(_TrailVP,v.vertex); o.screen=ComputeGrabScreenPos(o.position); o.depth=-mul(_TrailView,v.vertex).z; o.uv=v.uv; o.color=v.color; return o; }
   float4 frag(Output i):SV_Target
   {
    float2 uv=i.screen.xy/i.screen.w;
    float craft=tex2D(_CraftDepth,uv).r, body=tex2D(_BodyDepth,uv).r;
    if(i.depth<=0 || (craft>0 && i.depth>craft) || (body>0 && i.depth>body)) discard;
    float2 p=i.uv*2-1; float alpha=i.color.a*saturate(1-dot(p,p));
    return float4(i.color.rgb*alpha,alpha);
   }
   ENDCG
  }
  Pass
  {
   Cull Off ZWrite Off ZTest Always
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment composite
   #include "UnityCG.cginc"
   sampler2D _MainTex,_TrailColor;
   float4 composite(v2f_img i):SV_Target { float4 b=tex2D(_MainTex,i.uv),t=tex2D(_TrailColor,i.uv); return float4(b.rgb*(1-t.a)+t.rgb,1); }
   ENDCG
  }
 }
 Fallback Off
}
