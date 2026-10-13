// fluid: P0 색 A 바탕(a=무늬 크기), P1 색 B(a=속도), P2 색 C(a=휘감김 세기), P3 색 D 하이라이트(a=광택), P4 (밝기, 대비, 광택 날카로움, 결 세기)

// 큰 원을 따라 도는 오프셋. Time 이 커져도 좌표가 커지지 않는다(정밀도 유지).
float2 Orbit(float t, float r, float w, float ph)
{
    return float2(cos(t * w + ph), sin(t * w * 0.83 + ph * 1.37)) * r;
}

// 부드러운 결을 위한 짧은 fbm(옥타브 수 지정). 효과 품질과 관계없이 모양이 같도록 고정 횟수.
float FluidFbm(float2 p, int octaves)
{
    float value = 0, amplitude = 0.5, total = 0;
    for (int k = 0; k < octaves; k++)
    {
        value += Noise(p) * amplitude;
        total += amplitude;
        p = mul(float2x2(1.6, 1.2, -1.2, 1.6), p) + 7.31;
        amplitude *= 0.5;
    }
    return value / total;
}

float4 PS(V i) : SV_Target
{
    float2 p = (CanvasPixel(i) - Resolution * 0.5) / Resolution.y * (2.0 / max(P[0].a, 0.05));
    float t = Time * P[1].a;
    float s = Seed * 1.731;
    float warp = P[2].a;

    // 도메인 왜곡(domain warping): fbm 으로 fbm 의 좌표를 두 번 휘게 해 유체처럼 감기는 결을 만든다.
    float2 q = float2(FluidFbm(p + Orbit(t, 2.5, 0.11, s), 3),
                      FluidFbm(p + float2(5.2, 1.3) + Orbit(t, 2.5, 0.087, s + 2.1), 3));
    float2 r = float2(FluidFbm(p + warp * q + float2(1.7, 9.2) + Orbit(t, 1.8, 0.13, s + 4.2), 3),
                      FluidFbm(p + warp * q + float2(8.3, 2.8) + Orbit(t, 1.8, 0.071, s + 6.3), 3));
    float f = FluidFbm(p + warp * r + Orbit(t, 1.2, 0.05, s + 8.4), Quality >= 1 ? 4 : 3);

    // 0~1 로 펴기
    float fn = saturate((f - 0.22) / 0.56);
    float qa = saturate((q.x - 0.3) / 0.4);
    float rb = saturate((r.y - 0.3) / 0.4);
    float k = P[4].y;

    float3 color = lerp(P[0].rgb, P[1].rgb, smoothstep(0.15, 0.85, pow(fn, k)));
    color = lerp(color, P[2].rgb, smoothstep(0.35, 0.95, qa) * 0.85);
    color = lerp(color, P[3].rgb, smoothstep(0.5, 1.0, fn * rb * 1.4) * 0.9);
    // 결(가는 줄무늬): 왜곡 좌표 길이로 얇은 층을 만들어 잉크가 섞이는 느낌을 준다.
    float band = 0.5 + 0.5 * sin(length(r) * 18 + fn * 6);
    color *= lerp(1, 0.82 + 0.3 * band, P[4].w);
    color *= (0.55 + 0.65 * fn) * P[4].x;

    // 광택: 높이(f)의 화면 기울기로 법선을 만들어 반사광을 얹는다.
    float2 g = float2(ddx_fine(f), ddy_fine(f)) * Resolution.y * 0.25 / max(P[0].a, 0.05);
    float3 n = normalize(float3(-g, 1));
    float3 l = normalize(float3(-0.45, -0.6, 0.66));
    float3 h = normalize(l + float3(0, 0, 1));
    float diff = saturate(dot(n, l));
    float spec = pow(saturate(dot(n, h)), max(P[4].z, 1));
    color *= lerp(1, 0.75 + 0.4 * diff, saturate(P[3].a * 2));
    color += spec * P[3].a * lerp(float3(1, 1, 1), P[3].rgb, 0.3);

    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(color, 1);
}
