Shader "Custom/TerrainShader"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
// Upgrade NOTE: excluded shader from DX11; has structs without semantics (struct appdata members uv)
#pragma exclude_renderers d3d11
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows vertex:vert

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        sampler2D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
        };

        struct Vertex
        {
            float3 position;
            float3 normal;
        };



//struct appdata
//{
//    float4 vertex : SV_POSITION;
//    float3 normal : NORMAL;
//    float2 texcoord : TEXCOORD0;
//    float2 texcoord1 : TEXCOORD1;
//    float2 texcoord2 : TEXCOORD2;
//    fixed4 color : COLOR;
//    uint id : SV_VertexID;
//    UNITY_VERTEX_INPUT_INSTANCE_ID
//    //uint inst : SV_InstanceID;
//};

        #ifdef SHADER_API_D3D11
            StructuredBuffer<Vertex> _Positions;
        #endif

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;

        void vert(inout appdata_full v)
        {
//#ifdef SHADER_API_D3D11
//            Vertex vtx = _Positions[v.id];
//            float3 pos = vtx.position;
//            v.normal = vtx.normal;
//            v.vertex = float4(pos,1);
//#endif
    v.vertex.z *= 3;
}


        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Albedo comes from a texture tinted by color
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = float3(1,0,0);
    
            // Metallic and smoothness come from slider variables
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
