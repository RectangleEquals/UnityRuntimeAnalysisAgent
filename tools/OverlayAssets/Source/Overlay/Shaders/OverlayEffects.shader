// Animated background effects for the overlay, drawn with Graphics.Blit into a small render texture at a capped rate
// and shown as a background (UI Toolkit) or a RawImage (uGUI). Pass 0: plasma, 1: pixel fire, 2: pixel water.
// _Pixels sets the pixel grid (0 = smooth); _Time drives the motion; the colours come from the theme.
Shader "Hidden/UnityRuntimeAnalysisAgent/Overlay/Effects"
{
    Properties
    {
        _MainTex ("Unused", 2D) = "white" {}
        _ColorA ("Colour A", Color) = (0.05, 0.10, 0.20, 1)
        _ColorB ("Colour B", Color) = (0.30, 0.80, 1.00, 1)
        _Pixels ("Pixel grid (cells across; 0 = smooth)", Float) = 64
        _Speed ("Speed", Float) = 1
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    fixed4 _ColorA;
    fixed4 _ColorB;
    float _Pixels;
    float _Speed;

    float2 Grid(float2 uv)
    {
        return _Pixels > 0 ? floor(uv * _Pixels) / _Pixels : uv;
    }

    float Hash(float2 p)
    {
        return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
    }

    float Noise(float2 p)
    {
        float2 i = floor(p);
        float2 f = frac(p);
        float2 u = f * f * (3 - 2 * f);
        return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), u.x), u.y);
    }

    fixed4 Plasma(v2f_img i) : SV_Target
    {
        float2 p = Grid(i.uv);
        float t = _Time.y * _Speed;
        float v = sin(p.x * 10 + t) + sin(p.y * 12 - t * 1.3) + sin((p.x + p.y) * 8 + t * 0.7);
        return lerp(_ColorA, _ColorB, 0.5 + 0.5 * sin(v * 2));
    }

    fixed4 Fire(v2f_img i) : SV_Target
    {
        float2 p = Grid(i.uv);
        float t = _Time.y * _Speed;
        float n = Noise(float2(p.x * 8, p.y * 6 - t * 2)) * 0.6 + Noise(float2(p.x * 16, p.y * 12 - t * 4)) * 0.4;
        float heat = saturate(n * 1.4 - p.y * 1.2 + 0.2);
        heat = _Pixels > 0 ? floor(heat * 5) / 5 : heat;
        return lerp(_ColorA, _ColorB, heat);
    }

    fixed4 Water(v2f_img i) : SV_Target
    {
        float2 p = Grid(i.uv);
        float t = _Time.y * _Speed;
        float w = sin(p.x * 14 + t * 1.7 + sin(p.y * 9 + t)) * 0.5 + 0.5;
        w = w * 0.6 + Noise(p * 10 + t * 0.5) * 0.4;
        w = _Pixels > 0 ? floor(w * 4) / 4 : w;
        return lerp(_ColorA, _ColorB, w);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Plasma
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Fire
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Water
            ENDCG
        }
    }
}
