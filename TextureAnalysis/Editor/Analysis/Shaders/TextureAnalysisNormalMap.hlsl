// TextureAnalysisNormalMap.hlsl
// Kernel for normal map analysis: NormalMapVariation.

#ifndef TEXTURE_ANALYSIS_NORMAL_MAP_INCLUDED
#define TEXTURE_ANALYSIS_NORMAL_MAP_INCLUDED

// Unity's UnpackNormalmapRGorAG: X = r * a (DXTnm keeps X in alpha with red 1; BC5/RGB have alpha 1), Y = g,
// Z rebuilt from X and Y.
float3 DecodeNormalRGorAG(float4 c)
{
    float2 xy = float2(c.r * c.a, c.g) * 2.0 - 1.0;
    float z = sqrt(max(0.0, 1.0 - saturate(dot(xy, xy))));
    return SafeNormalize(float3(xy, z));
}

// Kernel 12: Normal Map Variation (5-tap cross, dot product)
[numthreads(16, 16, 1)]
void NormalMapVariation(uint3 id : SV_DispatchThreadID)
{
    // Offset by 1 to match CPU sampling positions (1, 3, 5, ...)
    uint x = id.x * NORMAL_MAP_SAMPLE_STEP + 1;
    uint y = id.y * NORMAL_MAP_SAMPLE_STEP + 1;

    if (x < 1 || x >= _Width - 1 || y < 1 || y >= _Height - 1)
        return;

    float4 c0 = SamplePixel(x, y);
    float4 c1 = SamplePixel(x + 1, y);
    float4 c2 = SamplePixel(x, y + 1);
    float4 c3 = SamplePixel(x - 1, y);
    float4 c4 = SamplePixel(x, y - 1);

    // Decode normals like Unity's UnpackNormalmapRGorAG (DXTnm, BC5 and RGB layouts; matches the CPU
    // NormalMapAnalyzer.DecodeNormal). SafeNormalize handles degenerate vectors.
    float3 n0 = DecodeNormalRGorAG(c0);
    float3 n1 = DecodeNormalRGorAG(c1);
    float3 n2 = DecodeNormalRGorAG(c2);
    float3 n3 = DecodeNormalRGorAG(c3);
    float3 n4 = DecodeNormalRGorAG(c4);

    float variation = 0.0;
    variation += 1.0 - dot(n0, n1);
    variation += 1.0 - dot(n0, n2);
    variation += 1.0 - dot(n0, n3);
    variation += 1.0 - dot(n0, n4);
    variation *= 0.25;

    AtomicAddFixed(IDX_NORMAL_VAR_SUM, variation);
    AtomicIncrement(IDX_NORMAL_VAR_COUNT);
}

#endif // TEXTURE_ANALYSIS_NORMAL_MAP_INCLUDED
