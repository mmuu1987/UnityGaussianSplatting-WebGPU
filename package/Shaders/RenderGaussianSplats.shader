// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
float _SplatScale;
float _SplatOpacityScale;
uint _SHOrder;
uint _SHOnly;

void DecomposeCovarianceForRender(float3 cov2d, out float2 v1, out float2 v2)
{
    float diag1 = cov2d.x, diag2 = cov2d.z, offDiag = cov2d.y;
    float mid = 0.5f * (diag1 + diag2);
    float radius = length(float2((diag1 - diag2) / 2.0, offDiag));
    float lambda1 = mid + radius;
    float lambda2 = max(mid - radius, 0.1);
    float2 diagVec = normalize(float2(offDiag, lambda1 - diag1));
    diagVec.y = -diagVec.y;
    float maxSize = 4096.0;
    v1 = min(sqrt(2.0 * lambda1), maxSize) * diagVec;
    v2 = min(sqrt(2.0 * lambda2), maxSize) * float2(diagVec.y, -diagVec.x);
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
#ifdef SHADER_API_WEBGPU
    SplatData splat = LoadSplatData(instID);
    float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
#else
	SplatViewData view = _SplatViewData[instID];
	float4 centerClipPos = view.pos;
#endif
	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
#ifdef SHADER_API_WEBGPU
		float4 boxRot = splat.rot;
		float3 boxSize = splat.scale;
		float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);

		float3 cov3d0, cov3d1;
		CalcCovariance3D(splatRotScaleMat, cov3d0, cov3d1);
		float splatScale2 = _SplatScale * _SplatScale;
		cov3d0 *= splatScale2;
		cov3d1 *= splatScale2;
		float3 cov2d = CalcCovariance2D(splat.pos, cov3d0, cov3d1, UNITY_MATRIX_MV, UNITY_MATRIX_P, _ScreenParams);

		float2 axis1, axis2;
		DecomposeCovarianceForRender(cov2d, axis1, axis2);

		float3 worldViewDir = _WorldSpaceCameraPos.xyz - centerWorldPos;
		float3 objViewDir = mul((float3x3)unity_WorldToObject, worldViewDir);
		objViewDir = normalize(objViewDir);

		o.col.rgb = ShadeSH(splat.sh, objViewDir, _SHOrder, _SHOnly != 0);
		o.col.a = min(splat.opacity * _SplatOpacityScale, 65000);
#else
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);
#endif

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

#ifdef SHADER_API_WEBGPU
		float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _ScreenParams.xy;
#else
		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
#endif
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

		// is this splat selected?
		if (_SplatBitsValid)
		{
			uint wordIdx = instID / 32;
			uint bitIdx = instID & 31;
			uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
			if (selVal & (1 << bitIdx))
			{
				o.col.a = -1;				
			}
		}
	}
	FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	
    if (alpha < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
