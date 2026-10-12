// glass(빗방울 유리, 아래 레이어 전체를 다시 그림): P0 (작은 방울 밀도, 굴절, 김 서림 흐림 mip, 김 서림 어둡기)
//            P1 김 서림 색조, P2 (방울 크기 px, 흘러내림 속도, 흘러내리는 줄 간격 배수, 0)

// 반지름이 0이면 방울이 없다(smoothstep 양 끝이 같아지면 0으로 나누게 된다).
float Drop(float radius, float distance) { return radius > 1e-4 ? smoothstep(radius, radius * 0.8, distance) : 0; }
float4 StaticDrops(float2 px, float size, float t, float salt)
{
    float2 grid = px / size;
    float2 id = floor(grid);
    float2 f = frac(grid) - 0.5;
    float h = Hash21(id + salt);
    float radius = lerp(0.12, 0.3, Hash21(id + 7.1 + salt)) * step(1 - P[0].x, h);
    float life = frac(t * 0.04 + h * 10);
    radius *= smoothstep(0.0, 0.08, life) * (1 - smoothstep(0.8, 1.0, life));
    float2 c = (float2(Hash21(id + 3.3 + salt), Hash21(id + 5.7 + salt)) - 0.5) * 0.3;
    float2 d = f - c;
    float mask = Drop(radius, length(d));
    return float4(mask > 0 ? d / radius : 0, mask, 0);
}

float4 SlidingDrops(float2 px, float size, float t)
{
    float column = size * P[2].z;
    float col = floor(px.x / column);
    float h = Hash11(col + Seed * 13);
    float speed = (0.35 + h) * P[2].y * size;
    float period = Resolution.y + size * 30;
    float travel = speed * (t + 0.6 * sin(t * 0.9 + h * 20) / 0.9) + h * period;
    float y = fmod(travel, period) - size * 15;
    float x = (col + 0.5 + (Hash11(col * 1.7) - 0.5) * 0.5) * column + sin(y / (size * 4) + h * 6) * size * 0.3;

    float2 d = (px - float2(x, y)) / size;
    d.y *= 0.8;
    float radius = 0.42 + 0.18 * h;
    float mask = Drop(radius, length(d));

    float above = (y - px.y) / size;
    float trail = step(0, above) * smoothstep(0.4, 0.15, abs(px.x - x) / size) * saturate(1 - above / 28);
    float ty = px.y / (size * 0.9);
    float tid = floor(ty);
    float2 td = float2((px.x - x) / size, (frac(ty) - 0.5) * 0.9);
    float tr = 0.16 * step(0.45, Hash11(tid + col * 31));
    float tmask = Drop(tr, length(td)) * step(0.5, above) * saturate(1 - above / 22);
    float2 normal = mask >= tmask ? (mask > 0 ? d / radius : 0) : td / tr;
    return float4(normal, max(mask, tmask), trail);
}

float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float2 px = i.uv * Resolution;
    float size = P[2].x * unit;
    float4 small = StaticDrops(px, size, Time, Seed);
    float4 tiny = StaticDrops(px + 37.3 * size, size * 0.55, Time * 1.3, Seed + 9);
    float4 slide = SlidingDrops(px, size * 1.7, Time);

    float still = max(small.z, tiny.z);
    float mask = max(still, slide.z);
    float2 normal = slide.z >= still ? slide.xy : (small.z >= tiny.z ? small.xy : tiny.xy);
    float clear = max(slide.w, mask);

    float2 offset = -normal * P[0].y * mask * size / Resolution;
    float level = lerp(P[0].z, 0.5, clear);
    float3 color = Source.SampleLevel(Linear, i.uv + offset, level).rgb;
    color = lerp(color, color * P[1].rgb, (1 - clear) * P[0].w);
    color *= 1 - mask * smoothstep(0.65, 1.0, length(normal)) * 0.35;
    float3 n = normalize(float3(normal, 0.65));
    color += mask * pow(saturate(dot(n, normalize(float3(-0.45, -0.6, 0.65)))), 24) * 0.3;
    return float4(color, 1);
}
