// Hologram look for MapView's terrain model: the mesh's vertex colours (hazard zones +
// shading), slightly see-through, with faint moving scanlines and a cyan glow on edges that
// face away from you. Lives in Assets/Resources so builds always include it.
Shader "Marsmap/Hologram"
{
    Properties
    {
        _Tint ("Tint (alpha = opacity)", Color) = (1, 1, 1, 0.88)
        _Glow ("Edge glow", Color) = (0.3, 0.9, 1, 1)
        _Scan ("Scanline strength", Range(0, 1)) = 0.12
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull Off

        Pass
        {
            Name "Hologram"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half4 _Glow;
                half _Scan;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float3 normalWS   : TEXCOORD0;
                float3 viewWS     : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(p);
                o.color = v.color;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceViewDir(p);
                o.positionWS = p;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half3 n = normalize(i.normalWS + 1e-5);
                half3 view = normalize(i.viewWS);
                half edge = pow(1.0h - saturate(abs(dot(n, view))), 3.0h);
                half scan = 1.0h - _Scan * (0.5h + 0.5h * sin(i.positionWS.y * 400.0 + _Time.y * 6.0));
                half3 c = i.color.rgb * _Tint.rgb * scan + _Glow.rgb * edge * 0.6h;
                return half4(c, _Tint.a * i.color.a);
            }
            ENDHLSL
        }
    }
}
