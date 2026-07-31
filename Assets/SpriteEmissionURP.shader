Shader "Custom/SpriteEmissionURP"
{
    Properties
    {
        _MainTex ("Base Texture", 2D) = "white" {}
        _EmissionTex ("Emission Texture", 2D) = "black" {}
        _EmissionColor ("Emission Color", Color) = (1,1,1,1)
        _EmissionStrength ("Emission Strength", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            sampler2D _MainTex;
            sampler2D _EmissionTex;
            float4 _MainTex_ST;

            float4 _EmissionColor;
            float _EmissionStrength;

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                half4 baseCol = tex2D(_MainTex, i.uv) * i.color;
                half4 emission = tex2D(_EmissionTex, i.uv) * _EmissionColor;

                // Add emission on top (HDR-friendly)
                half3 finalRGB = baseCol.rgb + emission.rgb * _EmissionStrength;

                return half4(finalRGB, baseCol.a);
            }
            ENDHLSL
        }
    }
}