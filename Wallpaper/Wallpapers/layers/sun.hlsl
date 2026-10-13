// sun: P0 원반 색(a=원반 불투명도), P1 후광 색(a=세기), P2 (가로 위치, 세로 위치, 반지름 px, 후광 크기 px)
//      P3 (빛살 세기, 빛살 속도, 지평선 높이, 넓은 빛 세기), P4 넓은 빛 색
float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float2 center = P[2].xy * Resolution;
    float2 d = (CanvasPixel(i) - center) / unit;
    float r = length(d);
    float radius = max(P[2].z, 0.5);
    float halo = max(P[2].w, 1);

    // 원반: 가장자리는 픽셀 단위로 부드럽게, 안쪽은 살짝 밝게(가장자리 어둡기).
    float disc = saturate((radius - r) * unit + 0.5);
    float horizon = saturate(P[3].z * Resolution.y - CanvasPixel(i).y + 0.5);
    disc *= horizon;
    float limb = sqrt(saturate(1 - (r * r) / (radius * radius)));
    float3 core = P[0].rgb * (0.82 + 0.18 * limb);
    float alpha = disc * P[0].a;

    // 후광: 원반 바로 밖의 진한 빛 + 아주 넓게 퍼지는 옅은 빛.
    float outside = max(r - radius, 0);
    float glow = exp(-outside / halo) * 0.8 + exp(-outside / (halo * 0.25)) * 0.35;
    float wide = 1 / (1 + pow(r / (halo * 5), 2));
    float3 light = P[1].rgb * P[1].a * glow + P[4].rgb * P[3].w * wide;

    // 빛살: 각도의 정수배 사인만 써서 이음매가 없다. 천천히 돈다.
    if (P[3].x > 0)
    {
        float a = atan2(d.y, d.x);
        float t = Time * P[3].y;
        float rays = (0.5 + 0.5 * sin(a * 9 + t)) * (0.5 + 0.5 * sin(a * 14 - t * 0.7 + 1.3)) + 0.6 * (0.5 + 0.5 * sin(a * 23 + t * 0.4 + 4.1));
        rays = rays * 0.45;
        light += P[1].rgb * P[3].x * rays * exp(-outside / (halo * 2.0)) * saturate(outside / (radius + 1));
    }
    // 후광은 밝은 만큼 뒤 하늘을 조금 가린다(파란 하늘에 더해 청록으로 뜨지 않게).
    float veil = saturate(dot(light, 0.333)) * 0.6 * (1 - alpha);
    light += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(core * alpha + light, alpha + veil);
}
