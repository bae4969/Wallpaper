// particles: P0~P2 색(a=선택 비중), P3 (최소 크기, 최대 크기, 불투명도, 모양 0=보케 1=줄기 2=빛 점)
//            P4 (속도 x, 속도 y, 속도 편차, 깜박임), P5 (영역 위, 영역 아래, 줄기 길이 px, 0)
V VS(uint vid : SV_VertexID, uint instance : SV_InstanceID)
{
    float s = instance + Seed * 101.0;
    float h1 = Hash11(s * 1.31), h2 = Hash11(s * 2.17), h3 = Hash11(s * 3.71), h4 = Hash11(s * 5.03), h5 = Hash11(s * 7.19);
    float unit = Unit();
    float speed = lerp(1 - P[4].z, 1 + P[4].z, h3);
    float2 velocity = P[4].xy * speed;
    float2 p = frac(float2(h1, h2) + velocity * Time * float2(SpanScale(), 1));
    float areaHeight = P[5].y - P[5].x;
    float2 center = float2(Span.x + p.x * Span.y, (P[5].x + p.y * areaHeight) * Resolution.y);   // 가로는 옆 모니터까지
    float size = lerp(P[3].x, P[3].y, h4) * unit;
    float2 corner = Corners[vid];
    float2 offset;
    if (P[3].w > 0.5 && P[3].w < 1.5)
    {
        float2 direction = normalize(velocity * float2(Resolution.x, Resolution.y * areaHeight) + float2(0, 1e-4));
        float halfLength = P[5].z * unit * 0.5 * (0.6 + 0.8 * h5);
        offset = direction * corner.y * halfLength + float2(-direction.y, direction.x) * corner.x * size * 0.5;
    }
    else offset = corner * size * 0.5;

    float weights = P[0].a + P[1].a + P[2].a;
    float pick = h5 * weights;
    float3 color = pick < P[0].a ? P[0].rgb : (pick < P[0].a + P[1].a ? P[1].rgb : P[2].rgb);
    float pulse = 1 - P[4].w * 0.5 * (1 + sin(Time * (0.6 + h3 * 1.8) + h1 * 40));

    V o;
    o.pos = PixelToClip(center + offset);
    o.uv = 0;
    o.local = corner;
    o.color = float4(color, P[3].z * pulse);
    return o;
}

float4 PS(V i) : SV_Target
{
    float r = length(i.local);
    float alpha;
    if (P[3].w < 0.5) alpha = smoothstep(1.0, 0.9, r) * (0.72 + 0.28 * smoothstep(0.5, 0.95, r));
    else if (P[3].w < 1.5) alpha = exp(-i.local.x * i.local.x * 5) * smoothstep(1.0, 0.25, abs(i.local.y));
    else alpha = exp(-r * r * 5);
    alpha *= i.color.a;
    return float4(i.color.rgb * alpha, alpha);
}
