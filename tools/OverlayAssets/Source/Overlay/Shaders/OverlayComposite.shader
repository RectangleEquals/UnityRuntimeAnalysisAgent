// Composites a panel's render texture onto the screen: rounded corners (a mask), optional scanlines and a tint.
// Used with Graphics.Blit; independent of the render pipeline.
Shader "Hidden/UnityRuntimeAnalysisAgent/Overlay/Composite"
{
    Properties
    {
        _MainTex ("Panel", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Size ("Size in pixels (xy)", Vector) = (400, 300, 0, 0)
        _Radius ("Corner radius in pixels", Float) = 8
        _Scanlines ("Scanline strength (0-1)", Float) = 0
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Tint;
            float4 _Size;
            float _Radius;
            float _Scanlines;

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Tint;
                float2 p = i.uv * _Size.xy;
                float2 q = abs(p - _Size.xy * 0.5) - (_Size.xy * 0.5 - _Radius);
                float d = length(max(q, 0)) - _Radius;
                c.a *= saturate(0.5 - d);
                c.rgb *= 1 - _Scanlines * 0.5 * (1 + sin(p.y * 3.14159));
                return c;
            }
            ENDCG
        }
    }
}
