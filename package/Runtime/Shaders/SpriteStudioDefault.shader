Shader "SpriteStudio/Default"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}

        // --- Blend state (set by the C# MaterialKey per the B-table in project_blend_mapping_truth) ---
        // Defaults are Mix + PMA output = equivalent to the previous behavior. The MaterialKey
        // overrides these per ssab BlendType.
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1   // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10  // OneMinusSrcAlpha
        [Enum(UnityEngine.Rendering.BlendOp)]   _BlendOp  ("Blend Op",  Float) = 0   // Add
        // PMA / discard are uniform branches (not keywords), so variants collapse to one.
        _InputPMA   ("Input PMA",   Float) = 0   // 1 = texture is premultiplied -> un-premultiply
        _OutputPMA  ("Output PMA",  Float) = 1   // 1 = emit premultiplied alpha (previous behavior)
        _NotDiscard ("Not Discard", Float) = 0   // 1 = keep transparent pixels (Mul/Invert)
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
        // Blend state is material-property driven (references [_Prop] rather than fixed values).
        Blend [_SrcBlend] [_DstBlend]
        BlendOp [_BlendOp]

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Includes/SpriteStudioCore.hlsl"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1; // [rate, blend_idx, isNormalized(effect), reserved]
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                half4  color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float _InputPMA;
            float _OutputPMA;
            float _NotDiscard;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                // Normal parts carry pixel coordinates; effect parts carry normalized coordinates
                // (texcoord1.z = 1).
                float isNormalized = IN.texcoord1.z;
                float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
                OUT.texcoord = IN.texcoord * texelSizeMultiplier;
                // SS uses a top-left UV origin, Unity uses bottom-left -> flip V (once, here, not on the CPU).
                OUT.texcoord.y = 1.0 - OUT.texcoord.y;
                OUT.color = IN.color;
                OUT.texcoord1 = IN.texcoord1;
                return OUT;
            }

            half4 frag(v2f IN) : SV_Target
            {
                // [input] Sample the texture (and un-premultiply if needed).
                float4 tex = ss_input_texture(_MainTex, IN.texcoord, _InputPMA);

                float rate         = IN.texcoord1.x;
                float blendIdx     = IN.texcoord1.y; // 0=Mix,1=Mul,2=Add,3=Sub (part-color blend kind)
                float isNormalized = IN.texcoord1.z; // 1 = effect part / 0 = normal part

                float3 rgb;
                if (isNormalized > 0.5)
                {
                    // Effect: tint by the vertex color (multiply).
                    rgb = tex.rgb * IN.color.rgb;
                }
                else
                {
                    // Normal part: part-color composition (applies the already-mediated values).
                    rgb = ss_partcolor_blend(tex.rgb, IN.color.rgb, rate, blendIdx);
                }

                float alpha = tex.a * IN.color.a;
                float4 col = float4(rgb, alpha);

                // [discard] Drop transparent pixels (disabled by _NotDiscard).
                if (ss_should_discard(col.a, 0.0, _NotDiscard))
                {
                    discard;
                }

                // [output] PMA finalize (correct only in combination with the blend state).
                col = ss_output_color(col, _OutputPMA);

                // TODO(MaterialKey): effect additive is currently passed through via the a=0 trick.
                //   Once the C# side assigns an Add-blend material to effect Add submeshes, remove
                //   this special case and rely solely on the blend state (project_blend_mapping_truth).
                if (isNormalized > 0.5 && abs(blendIdx - 2.0) < 0.5)
                {
                    col.a = 0.0;
                }

                return col;
            }
            ENDHLSL
        }
    }
}
