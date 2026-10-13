// clouds: P0 밝은 면 색(a=불투명도), P1 그늘 색(a=덮임 정도), P2 가장자리 빛 색(a=세기), P3 (크기 px, 속도, 띠 위, 띠 아래)
//         P4 (가로 시작 px, 뒤집기, 부드러움, 원근), P5 (빛 방향 x, 빛 방향 y, 그늘 세기, 아래쪽 옅어짐), P6.x 가로로 늘임
// 무늬 좌표는 가로 = 1080p 기준 px / 크기, 세로 = 띠 아래 끝에서 잰 높이 / 크기 라서 offsetX·flip 으로 이웃 화면과 이어진다.

static const float2x2 CloudRot = float2x2(0.80, -0.60, 0.60, 0.80);

// 큰 덩어리용 잡음. 화면마다 같은 모양이어야 하므로 효과 품질과 관계없이 3회 고정.
float CloudBase(float2 p)
{
    float v = Noise(p) * 0.5333;
    p = mul(CloudRot, p) * 2.03 + 3.1;
    v += Noise(p) * 0.2667;
    p = mul(CloudRot, p) * 2.03 + 7.7;
    v += Noise(p) * 0.1333;
    return v / 0.9333;
}

float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float y = P[4].y > 0.5 ? 1 - i.uv.y : i.uv.y;
    float top = P[3].z, bottom = P[3].w;
    float band = max(bottom - top, 1e-3);
    float t = (bottom - y) / band;              // 0 = 띠 아래 끝, 1 = 띠 위 끝
    if (t < 0 || t > 1) return 0;

    // 원근: 띠 아래(먼 하늘)로 갈수록 구름이 납작해진다. 위 끝에서는 원래 크기.
    const float e = 0.35;
    float tp = (1 / e - 1 / (t + e)) * (1 + e) * (1 + e);
    float v = lerp(t, tp, P[4].w);
    float scale = max(P[3].x, 1);
    float2 p = float2(CanvasPixel(i).x / unit + P[4].x, v * band * 1080) / scale;
    p.y *= max(P[6].x, 0.1);                    // 가로로 긴 구름
    float drift = Time * P[3].y;
    float s = Seed * 17.31;

    float warp = Noise(p * 0.45 + float2(s - drift * 0.4, s * 0.7));
    float2 q = p + float2(drift + (warp - 0.5) * 0.5, (warp - 0.5) * 0.3 + s);

    float base = CloudBase(q);
    // 잔무늬(뭉게뭉게한 굴곡)는 조금 더 빨리 흘러 구름 모양이 천천히 바뀐다.
    float2 r = mul(CloudRot, q * 4.3) + float2(drift * 1.6, 0);
    float detail = abs(Noise(r) - 0.5) * 2 - 0.5;
    if (Quality >= 0.5)
    {
        r = mul(CloudRot, r) * 2.17 + 5.3;
        detail += (abs(Noise(r) - 0.5) * 2 - 0.5) * 0.5;
    }
    if (Quality >= 1.5)
    {
        r = mul(CloudRot, r) * 2.21 + 1.9;
        detail += (Noise(r) - 0.5) * 0.3;
    }
    if (Quality >= 2) detail += (Noise(mul(CloudRot, r) * 2.3 + 8.4) - 0.5) * 0.16;
    float profile = smoothstep(0, max(P[5].w, 1e-3), t) * smoothstep(1, 0.7, t);
    float n = base + detail * 0.16 - (1 - profile) * 0.35;
    float threshold = lerp(0.68, 0.34, saturate(P[1].a));
    float soft = max(P[4].z, 0.01);
    float density = saturate((n - threshold) / soft);
    if (density <= 0) return 0;

    // 빛 쪽으로 조금 옮긴 곳의 밀도와 비교해 빛 받는 면·그늘진 면을 가른다.
    float2 L = normalize(P[5].xy + float2(0, 1e-4));
    float baseL = CloudBase(q + L * 0.09);
    float facing = (base - baseL) * 9;          // + 빛 받는 면, - 그늘
    float thick = saturate((n - threshold) / 0.22);
    float lit = saturate(0.62 + facing + detail * 0.7 - thick * P[5].z * 0.75);
    lit = lit * lit * (3 - 2 * lit);
    float3 color = lerp(P[1].rgb, P[0].rgb, lit);

    // 얇은 가장자리는 빛이 비쳐 밝게 빛난다(빛 쪽일수록 세게).
    float rim = (1 - thick) * saturate(0.5 + facing * 1.5);
    color += P[2].rgb * P[2].a * rim * rim;

    float alpha = density * P[0].a;
    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(color * alpha, alpha);
}
