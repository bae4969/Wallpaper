// vignette: P0 (세기, 시작 반경, 부드러움)
float4 PS(V i) : SV_Target
{
    float d = length((i.uv - 0.5) * float2(1.15, 1.0)) * 1.4;
    float alpha = P[0].x * smoothstep(P[0].y, P[0].y + P[0].z, d);
    return float4(0, 0, 0, alpha);
}
