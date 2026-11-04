// Unlit additive, vertex-colour tinted, UV-scrolling. Works in URP and Built-in, single-pass instanced (Quest).
// The field draws over passthrough, so the fragment leaves the framebuffer alpha alone: an additive
// quad that writes alpha 1 composites as an opaque black plate over the real room.
Shader "PalmCooling/Additive Scroll"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1,1,1,1)
        _Intensity ("Intensity", Float) = 1
        _ScrollSpeed ("Scroll Speed (U/s)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One One, Zero One
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_ST; fixed4 _Tint; half _Intensity; half _ScrollSpeed;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_OUTPUT(v2f, o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.uv.x -= _Time.y * _ScrollSpeed;
                o.color = v.color;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                fixed3 c = t.rgb * t.a * i.color.rgb * i.color.a * _Tint.rgb * _Tint.a * _Intensity;
                return fixed4(c, 0);
            }
            ENDCG
        }
    }
    FallBack Off
}
