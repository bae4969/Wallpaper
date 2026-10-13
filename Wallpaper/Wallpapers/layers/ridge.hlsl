// ridge: P0 산 색(a=불투명도), P1 (바닥 높이, 최고 높이, 높낮이 차, 봉우리 폭 px), P2 (가로 시작 px, 뒤집기, 아래로 어둡게, 어둡게 거리 px), P3 테두리 빛 색(a=세기)

// 뾰족한 봉우리용 1차원 잡음. 화면마다 같은 능선이 나와야 하므로 효과 품질과 관계없이 6회 고정.
float RidgeNoise(float x, float s)
{
    float value = 0, amplitude = 0.5;
    [unroll] for (int k = 0; k < 6; k++)
    {
        float n = 1 - abs(Noise(float2(x, s + k * 17.3)) * 2 - 1);
        value += n * n * amplitude;
        x *= 2.07;
        amplitude *= 0.47;
    }
    return value;
}

float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float x = CanvasPixel(i).x / unit + P[2].x;
    float y = P[2].y > 0.5 ? 1 - i.uv.y : i.uv.y;
    float r = saturate(RidgeNoise(x / P[1].w, Seed * 7.3) / 0.8);
    float top = P[1].x - P[1].y * lerp(1 - P[1].z, 1, r);
    float below = (y - top) * Resolution.y;
    float alpha = saturate(below / 1.5 + 0.5) * P[0].a;
    if (alpha <= 0) return 0;
    float3 body = P[0].rgb;
    // 능선에서 아래로 갈수록 어둡게(눈 둔덕의 그늘·가까운 언덕의 음영). 0이면 예전처럼 한 색.
    if (P[2].z > 0) body *= 1 - P[2].z * (1 - exp(-max(below, 0) / (max(P[2].w, 1) * unit)));
    float3 color = body + P[3].rgb * P[3].a * exp(-max(below, 0) / (6 * unit));
    return float4(color * alpha, alpha);
}
