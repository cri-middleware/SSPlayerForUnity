Shader "SpriteStudio/Default"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        { 
            "Queue"="Transparent" 
            "IgnoreProjector"="True" 
            "RenderType"="Transparent" 
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        // Premultiplied-alpha 合成。1パスで Mix(通常) と Add(加算) を両立させる。
        //   Mix : out=(rgb*a, a)  -> rgb*a + dst*(1-a)   (通常アルファ合成と等価)
        //   Add : out=(rgb*a, 0)  -> rgb*a + dst*(1-0)   (加算合成)
        // frag 側で rgb を a で premultiply し、加算パーティクルのみ a=0 を出力する。
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1; // rate, blend_idx, pma_flag, reserved
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                // Normal parts output pixel coordinates, Effect parts output normalized coordinates (IN.texcoord1.z = 1)
                float isNormalized = IN.texcoord1.z;
                float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
                OUT.texcoord = IN.texcoord * texelSizeMultiplier;
                // SpriteStudio uses Top-Left UV origin, Unity uses Bottom-Left UV origin
                OUT.texcoord.y = 1.0 - OUT.texcoord.y;
                OUT.color = IN.color;
                OUT.texcoord1 = IN.texcoord1;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                fixed4 texColor = tex2D(_MainTex, IN.texcoord);

                // texcoord1 = [rate, blend_idx, isNormalized(=effect), reserved]
                //   blend_idx は ss.format.BlendType: 0=Mix, 1=Mul, 2=Add, 3=Sub
                float rate = IN.texcoord1.x;
                float blendIdx = IN.texcoord1.y;
                float isNormalized = IN.texcoord1.z; // 1 = エフェクトパート, 0 = 通常パート

                fixed3 finalRGB;
                if (isNormalized > 0.5)
                {
                    // エフェクト: 頂点カラーで乗算 (tint)
                    finalRGB = texColor.rgb * IN.color.rgb;
                }
                else
                {
                    // 通常パート: SpriteStudio の PartColor Mix ブレンド (近似)
                    finalRGB = lerp(texColor.rgb, IN.color.rgb, rate);
                }

                fixed finalAlpha = texColor.a * IN.color.a;

                // --- Premultiplied alpha ---
                // Blend One OneMinusSrcAlpha と組み合わせ、通常パートは従来と等価な結果になる。
                finalRGB *= finalAlpha;

                // 加算合成: エフェクトパートで blend_idx == Add(2) のときのみ a=0 を出力し
                // dst を素通し ( rgb*a + dst ) させる。通常パートには影響しない。
                // TODO: 通常パートの Add/Mul/Sub は別パス/別マテリアルで対応予定。
                if (isNormalized > 0.5 && abs(blendIdx - 2.0) < 0.5)
                {
                    finalAlpha = 0.0;
                }

                return fixed4(finalRGB, finalAlpha);
            }
            ENDCG
        }
    }
}
