// Simple lit shader for the Mesh Optimizer demo.
// It has no LightMode tag, so it renders the same in the Built-in pipeline and in URP,
// and uses a fixed key light so the demo needs no scene lighting setup.
Shader "Mesh Optimizer/Demo Lit"
{
    Properties
    {
        _Color ("Color", Color) = (0.8, 0.8, 0.8, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float3 keyDir = normalize(float3(0.4, 0.8, -0.45));
                float key = saturate(dot(n, keyDir));
                float sky = 0.5 + 0.5 * n.y;
                float3 lit = _Color.rgb * (0.25 + 0.2 * sky + 0.75 * key);
                return fixed4(lit, 1);
            }
            ENDCG
        }
    }
}
