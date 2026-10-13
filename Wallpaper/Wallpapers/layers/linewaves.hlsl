// linewaves: P0 선 색 A(a=불투명도), P1 선 색 B(a=색 B 섞임), P2 (선 개수, 선 두께 px, 물결 높이, 물결 폭)
//            P3 (속도, 기울기 도, 원근, 먼 쪽 흐려짐), P4 (빛 받는 결 세기, 세로 위치, 가로 흐름, 띠 폭)

// 물결면 높이와 기울기(x: 높이, y: 가로 기울기, z: 깊이 방향 기울기). 사인 3개 + 잡음 1개로 가볍게.
float3 WaveHeight(float2 w, float t, float s)
{
    float a = w.x * 0.8 + w.y * 0.7 + t * 1.0 + s;
    float b = w.x * 1.5 - w.y * 1.0 - t * 0.7 + s * 1.7;
    float c = w.x * 0.3 + w.y * 1.3 + t * 0.45 + s * 0.3;
    float n = Noise(w * float2(0.7, 0.9) + float2(sin(t * 0.21) * 3, cos(t * 0.17) * 3) + s) - 0.5;
    float h = sin(a) * 0.5 + sin(b) * 0.22 + sin(c) * 0.35 + n * 0.6;
    float dx = cos(a) * 0.5 * 0.8 + cos(b) * 0.22 * 1.5 + cos(c) * 0.35 * 0.3;
    float dy = cos(a) * 0.5 * 0.7 - cos(b) * 0.22 * 1.0 + cos(c) * 0.35 * 1.3;
    return float3(h, dx, dy);
}

float4 PS(V i) : SV_Target
{
    float aspect = Resolution.x / Resolution.y;
    // 화면 가운데 기준 좌표(위가 +). 기울기만큼 돌린다.
    float2 q = float2((i.uv.x - 0.5) * aspect, 0.5 - i.uv.y) + float2(0, P[4].y - 0.5);
    float ang = radians(P[3].y);
    float cs = cos(ang), sn = sin(ang);
    q = float2(q.x * cs - q.y * sn, q.x * sn + q.y * cs);

    // 원근 물결면: 지평선이 화면 위쪽 D 만큼 떨어진 곳에 있고 카메라 높이 1에서 내려다본다.
    // 원근이 0이면 지평선이 아주 멀어 선이 고르게 평행하다.
    float D = lerp(3.0, 0.06, saturate(P[3].z));
    float sy = 0.5 + D - q.y;            // 지평선 아래 거리(화면 높이 단위)
    if (sy <= 0.002) return 0;
    float syc = 0.5 + D;                  // 화면 가운데 값(크기 기준)
    float t = Time * P[3].x;
    float wl = max(P[2].w, 0.05);
    float A = P[2].z / syc;               // 화면 가운데에서 물결 높이가 amplitude 가 되도록
    float drift = frac(Time * P[4].z / 64) * 64;

    // 높이가 있는 면에서 이 픽셀이 보는 깊이 z 를 고정점 반복으로 찾는다: z = (1 - A*h) / sy
    float z = 1 / sy;
    float3 hw = 0;
    int iterations = Quality >= 1 ? 3 : 2;
    for (int k = 0; k < iterations; k++)
    {
        float2 w = float2(q.x * z * syc + drift, log(z) * syc) * (2.0 / wl);
        hw = WaveHeight(w, t, Seed * 2.3);
        z = max((1 - A * hw.x) / sy, 1e-3);
    }

    // 선은 깊이(log z)가 같은 곳. 화면 가운데 기준 화면 높이당 lines 개.
    float v = log(z) * P[2].x * syc;

    // 1px 안티에일리어싱. 너무 촘촘하면 평균 밝기로 바꿔 무아레를 없앤다.
    float fw = max(fwidth(v), 1e-5);
    float dist = abs(frac(v + 0.5) - 0.5) / fw;
    float widthPx = max(P[2].y * Resolution.y / 1080.0, 0.7);
    float stroke = saturate(widthPx * 0.5 + 0.5 - dist);
    float cover = lerp(stroke, saturate(widthPx * fw), smoothstep(0.2, 0.55, fw));

    // 결 빛: 왼쪽 위에서 오는 빛을 받는 면은 밝게, 반대 면은 어둡게.
    float lit = saturate(0.5 - 0.45 * hw.y + 0.55 * hw.z);
    float shade = lerp(1, smoothstep(0.12, 0.9, lit) * 1.05 + pow(lit, 5) * 0.9 + 0.03, P[4].x);
    // 먼 쪽 흐려짐(지평선 쪽)
    float far = lerp(1, smoothstep(0.0, 0.9, sy / syc), P[3].w);
    // 선 색: 가로 방향으로 은은한 그라데이션 + 물결을 따라 살짝 흔들림
    float g = saturate(i.uv.x * 0.8 + 0.1 + hw.x * 0.12);
    float3 color = lerp(P[0].rgb, P[1].rgb, g * P[1].a);

    // 띠: 물결면을 따라 휘는 리본 모양으로 선을 모은다.
    float band = 1;
    if (P[4].w > 0)
    {
        float y = (v / (P[2].x * syc) + log(syc)) * syc / P[4].w;
        band = exp(-y * y * 2.5);
    }
    float alpha = saturate(cover * shade * far * band * P[0].a);
    alpha = saturate(alpha + (Dither(i.pos.xy) - 0.5) / 255.0);
    return float4(color * alpha, alpha);
}
