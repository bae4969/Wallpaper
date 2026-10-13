// petals(날리는 조각): P0~P2 색(a=선택 비중), P3 (최소 크기, 최대 크기, 불투명도, 모양 0=꽃잎 1=눈 2=낙엽)
//                    P4 (바람 x, 낙하 속도, 속도 편차, 흔들림 px), P5 (회전 속도, 뒤집힘 속도, 원근 흐림, 흔들림 빠르기)
//                    P6 (영역 위, 영역 아래, 돌풍, 원근 속도), P7.x 움직임 방향 늘이기 배율
V VS(uint vid : SV_VertexID, uint instance : SV_InstanceID)
{
    float s = instance + Seed * 101.0;
    float h1 = Hash11(s * 1.31), h2 = Hash11(s * 2.17), h3 = Hash11(s * 3.71), h4 = Hash11(s * 5.03), h5 = Hash11(s * 7.19);
    float h6 = Hash11(s * 9.41), h7 = Hash11(s * 11.83), h8 = Hash11(s * 13.27), h9 = Hash11(s * 17.61);
    float unit = Unit();
    float t = Time;

    // 깊이: 0=멀고 작음, 1=가깝고 큼(작은 조각이 더 많게 제곱)
    float depth = h4 * h4;
    float size = lerp(P[3].x, P[3].y, depth) * unit;
    float speed = lerp(1 - P[4].z, 1 + P[4].z, h3) * lerp(1, 0.45 + 1.1 * depth, P[6].w);

    // 돌풍: 바람 배율 1 + g*(0.6 sin(0.13t) + 0.4 sin(0.29t+1)) 을 적분한 위치
    float gustPos = -0.6 * cos(0.13 * t) / 0.13 - 0.4 * cos(0.29 * t + 1) / 0.29;
    float windPos = P[4].x * speed * (t + P[6].z * gustPos) * SpanScale();
    float fallPos = P[4].y * speed * t;

    float swayPx = P[4].w * unit;
    float margin = size * 0.8 + swayPx;
    float2 span = float2(Span.y, (P[6].y - P[6].x) * Resolution.y) + 2 * margin;   // 가로는 옆 모니터까지
    float2 p = frac(float2(h1 + windPos, h2 + fallPos));
    float2 center = float2(Span.x - margin, P[6].x * Resolution.y - margin) + p * span;

    // 좌우 흔들림(가까운 조각일수록 크게)
    float swayFreq = P[5].w * (0.7 + 0.6 * h5);
    float phase = h6 * 6.2832;
    center.x += sin(t * swayFreq + phase) * swayPx * (0.5 + 0.5 * depth);
    center.y += sin(t * swayFreq * 2 + phase) * swayPx * 0.12;

    // 평면 회전 + 한 축을 cos 로 눌러 뒤집히는 것처럼
    float angle = h7 * 6.2832 + t * P[5].x * (h8 * 2 - 1) * (0.5 + 0.5 * depth);
    float flip = P[5].y > 0 ? cos(t * P[5].y * (0.5 + h5) + h3 * 6.2832) : 1;
    float shape = P[3].w;
    float aspect = shape < 0.5 ? 0.78 : (shape < 1.5 ? 1.0 : 0.62);
    float squash = shape > 0.5 && shape < 1.5 ? 1 : max(abs(flip), 0.12);

    float2 corner = Corners[vid];
    float2 q = corner * float2(aspect * squash, 1) * size * 0.5;
    float sa = sin(angle), ca = cos(angle);
    float2 offset = float2(q.x * ca - q.y * sa, q.x * sa + q.y * ca);
    // 움직이는 방향으로 늘이기(빠른 눈보라의 잔상). 행렬식이 양수라 사각형이 뒤집히지 않는다.
    float2 motion = normalize(float2(P[4].x * Resolution.x, P[4].y * span.y) + float2(0, 1e-4));
    offset += motion * dot(offset, motion) * (max(P[7].x, 1) - 1);

    float weights = P[0].a + P[1].a + P[2].a;
    float pick = h9 * weights;
    float3 color = pick < P[0].a ? P[0].rgb : (pick < P[0].a + P[1].a ? P[1].rgb : P[2].rgb);
    color *= 0.9 + 0.2 * Hash11(s * 19.7);

    V o;
    o.pos = PixelToClip(center + offset);
    o.uv = float2(flip, P[5].z * smoothstep(0.35, 1.0, depth) * 0.6);
    o.local = corner;
    o.color = float4(color, P[3].z);
    return o;
}

// f<0 이 안쪽. soft 는 국소 좌표 단위의 추가 흐림 폭.
float Edge(float f, float soft)
{
    float w = fwidth(f) + soft;
    return saturate(0.5 - f / max(w, 1e-4));
}

float4 PS(V i) : SV_Target
{
    float2 p = i.local;
    float soft = i.uv.y;
    float flip = i.uv.x;
    float3 color = i.color.rgb;
    float alpha;
    if (P[3].w < 0.5)
    {
        // 벚꽃잎: 끝의 둥근 두 볼(사이가 파인 홈) + 밑동으로 좁아지는 몸통을 부드럽게 합친다
        float t = saturate(p.y * 0.5 + 0.5);
        float lobe = length(float2(abs(p.x) - 0.3, p.y - 0.52)) - 0.4;
        float body = (length(float2(p.x / (0.95 * lerp(0.3, 1, t)), p.y / 0.8)) - 1) * 0.7;
        float k = 0.2;
        float hmix = saturate(0.5 + 0.5 * (body - lobe) / k);
        float f = lerp(body, lobe, hmix) - k * hmix * (1 - hmix);
        alpha = Edge(f, soft);
        color = lerp(color * 0.85, lerp(color, 1, 0.18), t);
        color *= 1 - 0.07 * exp(-p.x * p.x * 60) * (1 - t);
        color *= (flip < 0 ? 0.84 : 0.94) + 0.12 * abs(flip);
    }
    else if (P[3].w < 1.5)
    {
        // 눈송이: 부드러운 둥근 점, 가운데가 조금 밝다
        float r = length(p);
        alpha = Edge(r - 0.55, 0.3 + soft * 0.6);
        color *= 1 + 0.12 * saturate(1 - r);
    }
    else
    {
        // 낙엽: 밑동 쪽이 넓은 잎 + 톱니 + 잎자루 + 가운데 잎맥
        float t = saturate((p.y + 0.72) / 1.72);
        float hw = 0.92 * pow(sin(3.1416 * pow(t, 0.8)), 0.8) * (1 - 0.1 * abs(sin(t * 3.1416 * 5)));
        float body = abs(p.x) - hw;
        float stem = max(abs(p.x) - 0.05, abs(p.y + 0.84) - 0.16);
        alpha = Edge(min(body, stem), soft);
        float rim = saturate(abs(p.x) / max(hw, 0.05));
        color *= 1.08 - 0.3 * rim * rim;
        color *= 1 - 0.28 * saturate(1 - abs(p.x) / 0.05) * step(-0.72, p.y);
        color *= (flip < 0 ? 0.8 : 0.92) + 0.12 * abs(flip);
    }
    alpha *= i.color.a * (1 - 0.35 * soft);
    return float4(color * alpha, alpha);
}
