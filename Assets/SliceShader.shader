Shader "Custom/SliceShader"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 3D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _Slice ("Slice", Range(0,1)) = 0.0
        _SizeInVoxels("SizeInVoxels", Range(0,1000)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest"}
        LOD 200
        Cull Off

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows alphatest:_Cutoff addshadow


        // Use shader model 3.0 target, to get nicer looking lighting 
        #pragma target 3.0

        sampler3D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
        };

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;
        float _Slice;
        int _SizeInVoxels;

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Albedo comes from a texture tinted by color
            //fixed4 c = tex3D (_MainTex, float3(IN.uv_MainTex,_Slice));
            //float d = c.r;

            float3 c = float3(IN.uv_MainTex, _Slice);

            float d = 0;

            /*for (int ix = -1; ix <= 1; ix++)
            for (int iy = -1; iy <= 1; iy++)
            for (int iz = -1; iz <= 1; iz++)
                d += tex3D(_MainTex, c + float3(ix,iy,iz)/ _SizeInVoxels);
            d /= 27;*/

            d = tex3D(_MainTex, c );

            //clip(0.502 - d);
    
            o.Albedo = d < 0.502 ? 1 : 0;
            // Metallic and smoothness come from slider variables
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = d;

            float dx = tex3D (_MainTex, c + float3(1.0 / _SizeInVoxels, 0, 0));
            float dy = tex3D (_MainTex, c + float3(0, 1.0 / _SizeInVoxels, 0));
            float dz = tex3D (_MainTex, c + float3(0, 0, 1.0 / _SizeInVoxels));
            o.Normal = normalize(d - float3(dx,dy,dz));
            o.Normal.z = -o.Normal.z;
            
#ifdef SHADER_API_D3D11
            clip(d - 0.5);
#endif
            //o.Alpha = 0.5;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
