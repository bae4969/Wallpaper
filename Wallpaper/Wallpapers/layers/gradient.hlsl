// gradient: P0 위 색, P1 아래 색, P2 빛 번짐 색(a=세기), P3.x 번짐 중심 y, P3.y 번짐 폭
float4 PS(V i) : SV_Target
{
    float3 color = lerp(P[0].rgb, P[1].rgb, saturate(i.uv.y));
    float glow = exp(-pow((i.uv.y - P[3].x) / max(P[3].y, 0.001), 2));
    color += P[2].rgb * P[2].a * glow;
    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(color, 1);
}
