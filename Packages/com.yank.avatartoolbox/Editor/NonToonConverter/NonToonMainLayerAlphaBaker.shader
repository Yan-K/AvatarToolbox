Shader "Hidden/AvatarToolbox/NonToonMainLayerAlphaBaker"
{
    Properties
    {
        _MainTex ("Baked Main", 2D) = "white" {}
        _SourceMainTex_ST ("Source Main ST", Vector) = (1,1,0,0)

        _UseMain2ndTex ("Use Main 2nd", Float) = 0
        _Color2nd ("Main 2nd Color", Color) = (1,1,1,1)
        _Main2ndTex ("Main 2nd Texture", 2D) = "white" {}
        [NoScaleOffset] _Main2ndBlendMask ("Main 2nd Blend Mask", 2D) = "white" {}
        _Main2ndTexAlphaMode ("Main 2nd Alpha Mode", Float) = 0
        _Main2ndTexAlphaIsOpaque ("Main 2nd Texture Has No Alpha", Float) = 0

        _UseMain3rdTex ("Use Main 3rd", Float) = 0
        _Color3rd ("Main 3rd Color", Color) = (1,1,1,1)
        _Main3rdTex ("Main 3rd Texture", 2D) = "white" {}
        [NoScaleOffset] _Main3rdBlendMask ("Main 3rd Blend Mask", 2D) = "white" {}
        _Main3rdTexAlphaMode ("Main 3rd Alpha Mode", Float) = 0
        _Main3rdTexAlphaIsOpaque ("Main 3rd Texture Has No Alpha", Float) = 0
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _SourceMainTex_ST;

            float _UseMain2ndTex;
            float4 _Color2nd;
            sampler2D _Main2ndTex;
            float4 _Main2ndTex_ST;
            sampler2D _Main2ndBlendMask;
            float _Main2ndTexAlphaMode;
            float _Main2ndTexAlphaIsOpaque;

            float _UseMain3rdTex;
            float4 _Color3rd;
            sampler2D _Main3rdTex;
            float4 _Main3rdTex_ST;
            sampler2D _Main3rdBlendMask;
            float _Main3rdTexAlphaMode;
            float _Main3rdTexAlphaIsOpaque;

            float ApplyAlphaMode(float baseAlpha, float layerAlpha, float mode)
            {
                if(mode < 1.5) return layerAlpha;
                if(mode < 2.5) return baseAlpha * layerAlpha;
                if(mode < 3.5) return saturate(baseAlpha + layerAlpha);
                return saturate(baseAlpha - layerAlpha);
            }

            fixed4 frag(v2f_img input) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, input.uv);
                float2 uvMain = input.uv * _SourceMainTex_ST.xy + _SourceMainTex_ST.zw;

                if(_UseMain2ndTex > 0.5 && _Main2ndTexAlphaMode > 0.5)
                {
                    float2 uv2nd = input.uv * _Main2ndTex_ST.xy + _Main2ndTex_ST.zw;
                    float textureAlpha = tex2D(_Main2ndTex, uv2nd).a;
                    textureAlpha = lerp(textureAlpha, 1.0, _Main2ndTexAlphaIsOpaque);
                    float layerAlpha = textureAlpha * _Color2nd.a * tex2D(_Main2ndBlendMask, uvMain).r;
                    col.a = ApplyAlphaMode(col.a, layerAlpha, _Main2ndTexAlphaMode);
                }

                if(_UseMain3rdTex > 0.5 && _Main3rdTexAlphaMode > 0.5)
                {
                    float2 uv3rd = input.uv * _Main3rdTex_ST.xy + _Main3rdTex_ST.zw;
                    float textureAlpha = tex2D(_Main3rdTex, uv3rd).a;
                    textureAlpha = lerp(textureAlpha, 1.0, _Main3rdTexAlphaIsOpaque);
                    float layerAlpha = textureAlpha * _Color3rd.a * tex2D(_Main3rdBlendMask, uvMain).r;
                    col.a = ApplyAlphaMode(col.a, layerAlpha, _Main3rdTexAlphaMode);
                }

                return col;
            }
            ENDCG
        }
    }
}
