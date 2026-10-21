sampler2D image : register(s0);
float4 curve : register(c0); // brightness, contrast, gamma, shadows
float4 balance : register(c1); // RGB gains, saturation
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 source = tex2D(image, uv);
    float3 x = saturate(source.rgb / max(source.a, 0.00001));
    float3 y = pow(max(x, 0.00001), 1.0 / curve.z);
    y += curve.w * 4.0 * x * (1.0 - x) * (1.0 - x);
    y = saturate(((y - 0.5) * curve.y + 0.5 + curve.x) * balance.rgb);
    float luminance = dot(y, float3(0.2126, 0.7152, 0.0722));
    y = saturate(lerp(luminance.xxx, y, balance.a));
    return float4(y * source.a, source.a);
}
