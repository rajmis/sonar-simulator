Shader "Sonar/FanView"
{
    Properties
    {
        _SonarTex ("Sonar Texture", 2D) = "black" {}
        _SwathAngle ("Swath Angle", Float) = 150
        _BackgroundColor ("Background", Color) = (0, 0, 0, 1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _SonarTex;
            float _SwathAngle;
            fixed4 _BackgroundColor;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv;
                uv.x = i.uv.x * 2.0 - 1.0;   // -1 to 1 horizontal
                uv.y = i.uv.y - 1.0;          // 0 at top, -1 at bottom

                float angle = atan2(uv.x, -uv.y);
                float dist = length(uv);
                float halfSwathRad = radians(_SwathAngle * 0.5);

                if (abs(angle) > halfSwathRad || dist > 1.0 || dist < 0.01)
                return _BackgroundColor;

                float beamUV = (angle + halfSwathRad) / (2.0 * halfSwathRad);
                float rangeUV = dist;

                float isEdge = 0;
                if (dist > 0.99) isEdge = 1;
                if (abs(abs(angle) - halfSwathRad) < 0.005) isEdge = 1;
                if (abs(frac(dist * 4.0)) < 0.01) isEdge = 0.3;

                fixed4 col = tex2D(_SonarTex, float2(beamUV, rangeUV));

                fixed4 edgeColor = fixed4(0.15, 0.15, 0.15, 1.0);
                col = lerp(col, edgeColor, isEdge);
                col.a = 1.0;
                return col;
            }
            ENDCG
        }
    }
}