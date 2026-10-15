Shader "Continuum/AeroFieldSheath"
{
 Properties { [HideInInspector] _ContinuumOrthographicContract("Orthographic view/depth contract",Float)=1 }
 SubShader
 {
  Tags { "Queue"="Transparent" }
  Pass
  {
   Blend SrcAlpha One ZWrite Off ZTest Always Cull Off
   CGPROGRAM
   #pragma target 3.0
   #pragma vertex vert_img
   #pragma fragment frag
   #include "AeroFieldCommon.cginc"
   float4 frag(v2f_img i):SV_Target
   {
    float2 uv=FieldUV(i.uv);
    float4 center=tex2D(_ContinuumViewField,uv);
    float width=clamp(_ContinuumEffectParameters.y*2,1,12);
    float2 pixel=abs(_ContinuumViewField_TexelSize.xy)*width;
    float4 nearest=0; float2 anchor=uv; float closest=1e20;
    float occupied=0;
    [unroll] for(int k=0;k<8;k++)
    {
     float angle=k*.78539816339;
     float2 offset=float2(cos(angle),sin(angle))*pixel;
     float2 sampleUV=uv+offset;
     float4 field=tex2D(_ContinuumViewField,sampleUV);
     occupied+=field.a>0?1:0;
     if(field.a>0&&field.a<closest&&all(sampleUV>=0)&&all(sampleUV<=1))
     { nearest=field; anchor=sampleUV; closest=field.a; }
    }
    float4 field=center.a>0?center:nearest;
    if(field.a<=0) discard;
    float3 normal=normalize(field.rgb*2-1);
    float3 world=FieldWorld(anchor,field.a);
    if(center.a>0) { anchor=uv; world=FieldWorld(uv,field.a); }
    float exposure=Exposed(world,normal);
    float3 view=FieldToCamera(world);
    float edge=pow(saturate(1-abs(dot(normal,view))),max(.25,_ContinuumEffectParameters.z*_ContinuumFalloffs.y));
    float silhouette=center.a<=0?saturate(occupied/3):edge*.35;
    float noise=tex2D(_ContinuumNoiseTex,uv*max(.01,_ContinuumTextureParameters.y)+
       float2(_ContinuumTextureParameters.w*_ContinuumTextureParameters.x*.07,0)).r;
    float alpha=saturate(silhouette*exposure*max(0,_ContinuumEffectParameters.x)*
       (0.6+noise*.4))*Unoccluded(uv,field.a);
    return float4(_ContinuumEffectColor.rgb,alpha*_ContinuumEffectColor.a);
   }
   ENDCG
  }
 }
 Fallback Off
}
