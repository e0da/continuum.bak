Shader "Continuum/MultiscaleDiagnostic" {
Properties { _MainTex("Captured albedo",2D)="white"{} _Mode("Diagnostic",Float)=0 _MaterialColor("Stable material identity",Color)=(1,1,1,1) }
SubShader {
Tags { "RenderType"="Opaque" } Cull Back ZWrite On ZTest LEqual
Pass {
CGPROGRAM
#pragma target 3.5
#pragma vertex vert
#pragma fragment frag
#include "UnityCG.cginc"
sampler2D _MainTex; float4 _MainTex_ST,_MaterialColor; float _Mode;
struct app { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; float4 color:COLOR; };
struct vary { float4 pos:SV_POSITION; float3 worldNormal:TEXCOORD0; float3 viewNormal:TEXCOORD1; float2 uv:TEXCOORD2; float4 color:COLOR; };
vary vert(app v) { vary o; o.pos=UnityObjectToClipPos(v.vertex); o.worldNormal=UnityObjectToWorldNormal(v.normal); o.viewNormal=mul((float3x3)UNITY_MATRIX_V,o.worldNormal); o.uv=v.uv; o.color=v.color; return o; }
float3 encoded(float3 n) { float q=dot(n,n); return q>1e-20?saturate(n*rsqrt(q)*.5+.5):float3(.5,.5,.5); }
float4 frag(vary i):SV_Target {
if(_Mode<.5) return float4(.8,.8,.8,1);
if(_Mode<1.5) return float4(encoded(i.worldNormal),1);
if(_Mode<2.5) return float4(encoded(i.viewNormal),1);
if(_Mode<3.5) return float4(tex2D(_MainTex,i.uv*_MainTex_ST.xy+_MainTex_ST.zw).rgb,1);
if(_Mode<4.5) return float4(i.color.rgb,1);
if(_Mode<5.5) return float4(frac(i.uv),0,1);
return float4(_MaterialColor.rgb,1);
}
ENDCG
}
UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
}
}
