// skyline: P0 건물 색(a=불투명도), P1/P2 창문 색(a=밝기), P3 (지면 y, 최소 높이, 최대 높이, 건물 폭 px)
//          P4 (창문 켜짐 비율, 깜박임 속도, -, 안개 섞임), P5 안개 색, P6 (창문 칸 폭, 높이, 가로 여백, 세로 여백)
float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float2 px = i.uv * Resolution;
    float width = P[3].w * unit;
    float shift = Seed * 37.0;
    float slot = px.x / width + shift;
    float k = floor(slot);
    float localX = frac(slot);
    float hk = Hash11(k * 1.7 + Seed);
    float top = P[3].x - lerp(P[3].y, P[3].z, hk * hk);
    bool stepped = Hash11(k * 3.1 + Seed) > 0.55;
    if (stepped && (localX < 0.22 || localX > 0.78)) top += 0.035;
    bool antenna = Hash11(k * 5.3 + Seed) > 0.8 && abs(localX - 0.5) < 0.015 && i.uv.y > top - 0.07 && i.uv.y < top;
    bool gap = localX < 0.05 || localX > 0.97;
    bool inside = (i.uv.y >= top && i.uv.y <= P[3].x && !gap) || antenna;
    if (!inside) return 0;

    float3 color = P[0].rgb;
    float2 cellSize = P[6].xy * unit;
    float2 rel = float2((localX - 0.05) * width, px.y - top * Resolution.y);
    float2 cell = floor(rel / cellSize);
    float2 inCell = frac(rel / cellSize);
    bool window = !antenna && inCell.x > P[6].z && inCell.x < 1 - P[6].z && inCell.y > P[6].w && inCell.y < 1 - P[6].w
        && rel.x > cellSize.x * 0.5 && rel.x < (0.92 * width) - cellSize.x * 0.5 && rel.y > cellSize.y * 0.6;
    float hc = Hash21(cell + k * 7.31 + Seed);
    float lit = step(hc, P[4].x);
    float twinkle = Hash21(cell + k * 3.7 + floor(Time * P[4].y * (0.2 + hc) + hc * 50));
    lit *= step(0.07, twinkle);
    if (window && lit > 0)
    {
        float4 tone = Hash21(cell * 1.3 + k) > 0.5 ? P[1] : P[2];
        color = tone.rgb * tone.a * lerp(0.55, 1.0, Hash21(cell + 2.2));
    }
    color = lerp(color, P[5].rgb, P[4].w);
    float alpha = P[0].a;
    return float4(color * alpha, alpha);
}
