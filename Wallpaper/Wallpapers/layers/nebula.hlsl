// nebula(성운): P0 색 A(a=밝기), P1 색 B(a=구름 양), P2 색 C(a=먼지 띠), P3 (무늬 크기 px, 흐름 속도, 가로 위치 px, 세로 위치 px)
//               P4 (가운데 옅게, 비틀림, 덩어리 정도, 메인 아래 채움), P5.x 메인 아래 밝기 배율
float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float2 p = (CanvasPixel(i) / unit + P[3].zw) / max(P[3].x, 1) + Seed * 7.31;
    // 시간은 계속 커지므로 위상을 frac 으로 감아 천천히 오가는 흐름을 만든다.
    float w = P[3].y;
    float a1 = frac(Time * w * 0.159155) * 6.2831853;
    float a2 = frac(Time * w * 0.116183) * 6.2831853;
    float a3 = frac(Time * w * 0.201330) * 6.2831853;
    float2 drift = float2(sin(a1), cos(a2)) * 2.5;
    float2 evolve = float2(sin(a3), cos(a1 + 1.7)) * 0.35;
    p += drift * 0.35;

    // 도메인 워핑: q 로 좌표를 비틀어 소용돌이 모양을 만든다.
    float2 q = float2(Fbm(p + evolve), Fbm(p + float2(5.2, 1.3) - evolve));
    float n = Fbm(p + P[4].y * q + float2(1.7, 9.2));

    // 메인 아래(continue 서브, uv.y > 1)는 구름을 더 채워 빈 하늘이 되지 않게 한다. 메인(uv.y <= 1)은 그대로.
    float belowRamp = smoothstep(1.0, 1.45, i.uv.y);
    float below = saturate(P[4].w) * belowRamp;
    float lo = lerp(0.62, 0.22, saturate(P[1].a + below * 0.12));
    float d = smoothstep(lo, lo + 0.42, n);

    float3 color = lerp(P[0].rgb, P[1].rgb, smoothstep(0.46, 0.66, q.x));
    color = lerp(color, P[2].rgb, smoothstep(0.48, 0.66, q.y) * smoothstep(lo + 0.05, lo + 0.4, n) * 0.9);
    // 잘게 결이 진 무늬로 흐릿한 덩어리에 섬세함을 더한다.
    float detail = Noise(p * 7.3 + q * 3.1);
    d *= 0.6 + 0.8 * detail;
    // 짙은 곳은 하얗게 달아오른 심(core)처럼 밝힌다.
    float core = d * d * d;
    float3 emission = color * (d * d * 0.9 + core * 0.6) + core * core * 0.35 * (P[0].rgb + P[1].rgb + P[2].rgb) / 3;

    // 큰 덩어리 마스크와 가운데 옅게.
    float m = smoothstep(0.2, 0.8, Noise(p * 0.33 + float2(3.1, 7.7)));
    m = lerp(1, m, saturate(P[4].z));
    m = lerp(m, 1, below);
    // 가운데 옅게는 화면마다(시계 위젯 자리). 메인에서는 ScreenUV = 캔버스 좌표라 그대로다.
    float2 c = (ScreenUV(i) - 0.5) * float2(Screen.x / Screen.y, 1);
    float fade = lerp(1, smoothstep(0.05, 0.62, length(c)), saturate(P[4].x));

    // 먼지 띠: 구름이 있는 곳에서 꼬인 어두운 줄기. 아래의 별도 가린다.
    float alpha = 0;
    if (P[2].a > 0)
    {
        float dn = Fbm(p * 2.3 + q * 1.6 + 11.7);
        float lane = smoothstep(0.5, 0.68, dn + (detail - 0.5) * 0.08) * smoothstep(0.05, 0.45, n - lo + 0.25);
        alpha = saturate(lane * P[2].a) * m;
    }
    emission *= P[0].a * m * fade * (1 - alpha * 0.9) * lerp(1, max(P[5].x, 0), belowRamp);
    emission += (Dither(i.pos.xy) - 0.5) / 255.0 * saturate(d * 4);
    return float4(max(emission, 0), alpha);
}
