Shader "Continuum/MultiscaleDiagnostic" {
Properties { _MainTex("Captured albedo",2D)="white"{} _Mode("Diagnostic",Float)=0 _MaterialColor("Stable material identity",Color)=(1,1,1,1) _Tint("Approximate captured tint",Color)=(1,1,1,1) _Cull("Diagnostic culling",Float)=2 _ContinuumPacketContract("Declared packet contract",Float)=1 _ContinuumRetainedContract("Declared retained state contract",Float)=1 _ContinuumRetained("Retained state enabled",Float)=0 _ContinuumState("Affine normal and hue table",2D)="black"{} _ContinuumStateBase("Packet source base",Float)=0 _ContinuumStateShape("State dimensions",Vector)=(8,1,0,0) _ContinuumRetainedAnchor("Camera relative anchor",Vector)=(0,0,0,0) }
SubShader {
Tags { "RenderType"="Opaque" } Cull [_Cull] ZWrite On ZTest LEqual
Pass {
CGPROGRAM
#pragma target 3.5
#pragma vertex vert
#pragma fragment frag
#pragma multi_compile_instancing
#include "UnityCG.cginc"
sampler2D _MainTex; float4 _MainTex_ST,_Tint; float _Mode;
sampler2D _ContinuumState; float4 _ContinuumStateShape,_ContinuumRetainedAnchor; float _ContinuumStateBase,_ContinuumRetained;
float4 stateRow(float row,float column) { return tex2Dlod(_ContinuumState,float4((column+.5)/8,(row+.5)/_ContinuumStateShape.y,0,0)); }
UNITY_INSTANCING_BUFFER_START(Props)
UNITY_DEFINE_INSTANCED_PROP(float4,_MaterialColor)
UNITY_INSTANCING_BUFFER_END(Props)
struct app { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; float4 color:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
struct vary { float4 pos:SV_POSITION; float3 worldNormal:TEXCOORD0; float3 viewNormal:TEXCOORD1; float2 uv:TEXCOORD2; float4 color:COLOR; float4 sourceHue:TEXCOORD3; float eyeDepth:TEXCOORD4; UNITY_VERTEX_INPUT_INSTANCE_ID };
vary vert(app v) {
vary o; UNITY_SETUP_INSTANCE_ID(v); UNITY_TRANSFER_INSTANCE_ID(v,o); o.sourceHue=0;
if(_ContinuumRetained>.5) {
float row=_ContinuumStateBase;
#if defined(UNITY_INSTANCING_ENABLED)
row+=unity_InstanceID-unity_BaseInstanceID;
#endif
float4 r0=stateRow(row,0),r1=stateRow(row,1),r2=stateRow(row,2),r3=stateRow(row,3);
float4 world=float4(dot(r0,v.vertex),dot(r1,v.vertex),dot(r2,v.vertex),dot(r3,v.vertex)); world.xyz+=_ContinuumRetainedAnchor.xyz;
o.pos=mul(UNITY_MATRIX_VP,world);
o.eyeDepth=max(0,-mul(UNITY_MATRIX_V,world).z);
float3 n=float3(dot(stateRow(row,4).xyz,v.normal),dot(stateRow(row,5).xyz,v.normal),dot(stateRow(row,6).xyz,v.normal)); float q=dot(n,n); o.worldNormal=q>1e-20?n*rsqrt(q):float3(0,0,0); o.sourceHue=stateRow(row,7);
} else { o.pos=UnityObjectToClipPos(v.vertex); o.worldNormal=UnityObjectToWorldNormal(v.normal); o.eyeDepth=max(0,-UnityObjectToViewPos(v.vertex).z); }
o.viewNormal=mul((float3x3)UNITY_MATRIX_V,o.worldNormal); o.uv=v.uv; o.color=v.color; return o;
}
float3 encoded(float3 n) { float q=dot(n,n); return q>1e-20?saturate(n*rsqrt(q)*.5+.5):float3(.5,.5,.5); }
float4 shade(vary i) {
UNITY_SETUP_INSTANCE_ID(i);
if(_Mode<.5) return float4(.8,.8,.8,1);
if(_Mode<1.5) return float4(encoded(i.worldNormal),1);
if(_Mode<2.5) return float4(encoded(i.viewNormal),1);
if(_Mode<3.5) return float4(tex2D(_MainTex,i.uv*_MainTex_ST.xy+_MainTex_ST.zw).rgb,1);
if(_Mode<4.5) return float4(i.color.rgb,1);
if(_Mode<5.5) return float4(frac(i.uv),0,1);
if(_Mode<6.5) return float4(_ContinuumRetained>.5?i.sourceHue.rgb:UNITY_ACCESS_INSTANCED_PROP(Props,_MaterialColor).rgb,1);
return float4(tex2D(_MainTex,i.uv*_MainTex_ST.xy+_MainTex_ST.zw).rgb*_Tint.rgb*i.color.rgb,1);
}
struct output { float4 color:SV_Target0; float depth:SV_Target1; };
output frag(vary i) { output o; o.color=shade(i); o.depth=i.eyeDepth; return o; }
ENDCG
}
Pass {
Tags { "LightMode"="ShadowCaster" }
CGPROGRAM
#pragma target 3.5
#pragma vertex shadowVert
#pragma fragment shadowFrag
#pragma multi_compile_shadowcaster
#pragma multi_compile_instancing
#include "UnityCG.cginc"
struct shadowApp { float4 vertex:POSITION; float3 normal:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
struct shadowVary { V2F_SHADOW_CASTER; };
shadowVary shadowVert(shadowApp v) { shadowVary o; UNITY_SETUP_INSTANCE_ID(v); TRANSFER_SHADOW_CASTER_NORMALOFFSET(o); return o; }
float4 shadowFrag(shadowVary i):SV_Target { SHADOW_CASTER_FRAGMENT(i) }
ENDCG
}
}
}
