Shader "Hidden/Continuum/MultiscaleComposite" {
SubShader { Cull Off ZWrite Off ZTest Always Pass {
CGPROGRAM
#pragma target 3.5
#pragma vertex vert_img
#pragma fragment frag
#include "UnityCG.cginc"
sampler2D _CraftColor,_BodyColor,_CraftDepth,_BodyDepth;
float4 frag(v2f_img i):SV_Target {
 float craft=tex2D(_CraftDepth,i.uv).r,body=tex2D(_BodyDepth,i.uv).r;
 float4 c=craft>0&&(body<=0||craft<body)?tex2D(_CraftColor,i.uv):body>0?tex2D(_BodyColor,i.uv):float4(.015,.02,.035,1);
 return float4(c.rgb,1);
}
ENDCG
} }
}
