Shader "Continuum/MultiscaleBodyDiffuse" {
Properties { _MainTex("Captured body texture",2D)="white"{} _Color("Captured tint",Color)=(1,1,1,1) }
SubShader { Tags { "RenderType"="Opaque" } Cull Back
CGPROGRAM
#pragma target 3.5
#pragma surface surf Lambert addshadow
sampler2D _MainTex; float4 _Color;
struct Input { float2 uv_MainTex; };
void surf(Input i,inout SurfaceOutput o) { o.Albedo=tex2D(_MainTex,i.uv_MainTex).rgb*_Color.rgb; o.Alpha=1; }
ENDCG
}
}
