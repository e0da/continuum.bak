Shader "Hidden/Continuum/MultiscaleDebugDepth" {
Properties { _MainTex("Positive depth metres",2D)="black"{} }
SubShader { Cull Off ZWrite Off ZTest Always
Pass {
CGPROGRAM
#pragma target 3.5
#pragma vertex vert_img
#pragma fragment frag
#include "UnityCG.cginc"
sampler2D _MainTex; float4 _Range;
float4 frag(v2f_img i):SV_Target { float d=tex2D(_MainTex,i.uv).r; if(d<=0) return float4(0,0,0,1); float v=_Range.z>.5?log(1+d)/log(1+_Range.y):(d-_Range.x)/max(1e-6,_Range.y-_Range.x); return float4(saturate(v).xxx,1); }
ENDCG
}
}
}
