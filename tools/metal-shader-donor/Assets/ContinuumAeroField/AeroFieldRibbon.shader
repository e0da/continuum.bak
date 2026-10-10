Shader "Continuum/AeroFieldRibbon"
{
 Properties { _ContinuumNoiseTex("Noise",2D)="white"{} }
 SubShader
 {
  Tags { "Queue"="Transparent" }
  Pass
  {
   Blend SrcAlpha One ZWrite Off ZTest Always Cull Off
   CGPROGRAM
   #pragma target 3.5
   #pragma vertex vert
   #pragma fragment frag
   #pragma multi_compile_instancing
   #include "AeroFieldCommon.cginc"
   struct V { float4 vertex:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
   struct F { float4 position:SV_POSITION; float4 screen:TEXCOORD0; float2 uv:TEXCOORD1; float depth:TEXCOORD2; float exposure:TEXCOORD3; };
   F vert(V v)
   {
    UNITY_SETUP_INSTANCE_ID(v);
    F o;
    uint seed=0;
    #if defined(UNITY_INSTANCING_ENABLED)
    seed=unity_InstanceID;
    #endif
    float2 cell=float2(seed%16,seed/16);
    float2 uv=_ContinuumSeedRect.xy+((cell+.5)/16)*_ContinuumSeedRect.zw;
    float4 field=tex2Dlod(_ContinuumViewField,float4(uv,0,0));
    float3 normal=normalize(field.rgb*2-1);
    float3 anchor=FieldWorld(uv,max(field.a,.001));
    float exposed=field.a>0&&all(uv>=0)&&all(uv<=1)?Exposed(anchor,normal):0;
    float3 downstream=normalize(_ContinuumEffectDirection.xyz);
    float3 toCamera=normalize(_ContinuumViewCameraToWorld._m03_m13_m23-anchor);
    float3 crossAxis=cross(downstream,toCamera);
    float crossLength=length(crossAxis);
    crossAxis=crossLength>.0001?crossAxis/crossLength:_ContinuumViewCameraToWorld._m00_m10_m20;
    float phase=frac(_ContinuumTextureParameters.w*max(.01,_ContinuumTextureParameters.x)*.2+seed*.61803398875);
    float lengthMeters=clamp(_ContinuumEffectParameters.y,.01,100);
    float width=clamp(lengthMeters*.035,.01,.6);
    float wobble=sin(phase*6.2831853+seed)*clamp(_ContinuumTextureParameters.z,0,4)*width;
    float3 world=anchor+normal*.015+downstream*((v.vertex.y+.5+phase*.35)*lengthMeters)+crossAxis*(v.vertex.x*width+wobble);
    o.position=mul(UNITY_MATRIX_VP,float4(world,1));
    o.screen=ComputeGrabScreenPos(o.position); o.depth=-mul(UNITY_MATRIX_V,float4(world,1)).z;
    o.uv=v.uv; o.exposure=exposed;
    if(exposed<=0) o.position=float4(-2,-2,0,1);
    return o;
   }
   float4 frag(F i):SV_Target
   {
    if(i.exposure<=0||i.depth<=0) discard;
    float2 uv=i.screen.xy/i.screen.w;
    if(any(uv<0)||any(uv>1)) discard;
    float lateral=saturate(1-abs(i.uv.x*2-1));
    lateral=pow(lateral,max(.25,_ContinuumFalloffs.z));
    float taper=pow(saturate(1-i.uv.y),max(.2,_ContinuumFalloffs.x));
    float noise=tex2D(_ContinuumNoiseTex,float2(i.uv.x,i.uv.y*max(.01,_ContinuumTextureParameters.y)-
       _ContinuumTextureParameters.w*_ContinuumTextureParameters.x)).r;
    float alpha=saturate(lateral*taper*i.exposure*max(0,_ContinuumEffectParameters.x)*(.4+.6*noise));
    alpha*=Unoccluded(uv,i.depth)*_ContinuumEffectColor.a;
    return float4(_ContinuumEffectColor.rgb,alpha);
   }
   ENDCG
  }
 }
 Fallback Off
}
