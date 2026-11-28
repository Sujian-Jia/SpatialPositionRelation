Shader "Spatial/SensorFrustum"
{
    Properties { _Color ("Color", Color) = (0.24, 0.88, 0.72, 0.065) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            fixed4 frag() : SV_Target { return _Color; }
            ENDCG
        }
    }
}
