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
        Blend SrcAlpha OneMinusSrcAlpha // Mix blend mode

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
                // Basic implementation of PartColor Mix blend mode for now
                // In a full implementation, we'd use IN.texcoord1.y (blend_idx) to switch behavior
                fixed4 texColor = tex2D(_MainTex, IN.texcoord);
                
                // Color.a stores the overall part alpha
                // Color.rgb stores the part color RGB
                // rate stores the mix rate
                float rate = IN.texcoord1.x;
                float isNormalized = IN.texcoord1.z;
                
                fixed3 finalRGB;
                if (isNormalized > 0.5) 
                {
                    // Effect parts use vertex color multiplication (tinting)
                    finalRGB = texColor.rgb * IN.color.rgb;
                }
                else
                {
                    // Normal parts use SpriteStudio's PartColor blending (Mix approximation)
                    // In a full implementation, we'd use IN.texcoord1.y (blend_idx) to switch behavior
                    finalRGB = lerp(texColor.rgb, IN.color.rgb, rate);
                }
                
                fixed finalAlpha = texColor.a * IN.color.a;

                return fixed4(finalRGB, finalAlpha);
            }
            ENDCG
        }
    }
}
