// aurora: P0 아래 가장자리 색(a=밝기), P1 위쪽 색(a=섞임), P2 (가장자리 높이, 퍼짐 길이, 출렁임, 굽이 폭 px)
//         P3 (속도, 가로 시작 px, 뒤집기, 빛줄기)
float4 PS(V i) : SV_Target
{
    float x = CanvasPixel(i).x / Unit() + P[3].y;
    float y = P[3].z > 0.5 ? 1 - i.uv.y : i.uv.y;
    float t = Time * P[3].x;
    float3 total = 0;
    [unroll] for (int k = 0; k < 2; k++)
    {
        float s = Seed * 7.1 + k * 23.3;
        float u = x / P[2].w;
        float drift = t * (k == 0 ? 1.0 : -0.7);
        float edge = P[2].x - k * P[2].y * 0.45
            + (Noise(float2(u * 1.2 + drift * 0.8, s)) - 0.5) * P[2].z * 2
            + sin(u * 2.7 + drift * 2.0 + s) * P[2].z * 0.35;
        float above = edge - y;
        float thick = P[2].y * (0.7 + 0.6 * Noise(float2(u * 0.9 - drift * 0.5, s + 3)));
        float body = above > 0 ? exp(-above / thick) : exp(-pow(above / 0.03, 2));
        float rays = lerp(1, 0.35 + 0.9 * Noise(float2(x / 14 + drift * 6, s + 9)) * Noise(float2(x / 41 - drift * 3, s + 1)) * 2, P[3].w);
        float patch = smoothstep(0.2, 0.7, Noise(float2(u * 0.6 + drift * 0.3, s + 5)));
        float3 color = lerp(P[0].rgb, P[1].rgb, saturate(above / (thick * 1.6)) * P[1].a);
        total += color * body * rays * patch * (k == 0 ? 1.0 : 0.55);
    }
    // 화면 맨 위로 갈수록 옅어져 빛줄기가 하늘 끝까지 이어지지 않게 한다(뒤집힌 반사는 화면 끝이 하늘 끝이 아니므로 그대로).
    float fade = P[3].z > 0.5 ? 1 : smoothstep(0.0, 0.3, y);
    return float4(total * P[0].a * fade, 0);
}
