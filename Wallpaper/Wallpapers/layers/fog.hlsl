// fog: P0 색(a=불투명도), P1 (크기, 흐름 x, 흐름 y, 아래쪽 치우침)
float4 PS(V i) : SV_Target
{
    float2 p = i.uv * float2(Resolution.x / Resolution.y, 1) * P[1].x;
    float n = Fbm(p + Time * P[1].yz + Seed * 13) * Fbm(p * 0.5 - Time * P[1].yz * 0.6 + 7.1);
    float bias = lerp(1, smoothstep(0.1, 1.0, i.uv.y), P[1].w);
    float alpha = saturate(n * 2.2) * P[0].a * bias;
    return float4(P[0].rgb * alpha, alpha);
}
