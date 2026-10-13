// forest(침엽수 숲): P0 나무 색(a=불투명도), P1 (지면 y, 최소 높이, 최대 높이, 나무 간격 px), P2 (밀도, 안개 섞임, 땅안개, 땅안개 높이)
//                   P3 안개 색(a=폭 배율), P4 (가로 시작 px, 뒤집기, 지면 기복, 흔들림 px), P5 가장자리 빛 색(a=세기), P6 (가지 층 수, 들쭉날쭉, 밑동 투명, 가운데 비우기 px)
// 좌표는 1080p 기준 px. 한 픽셀에서 이웃 나무 5그루를 검사해 겹친 실루엣을 만든다(잡음 2회 + 해시만 쓰므로 가볍다).
float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float y = P[4].y > 0.5 ? 1 - i.uv.y : i.uv.y;
    float X = CanvasPixel(i).x / unit + P[4].x;
    float Y = y * 1080.0;
    float spacing = max(P[1].w, 4);
    float hill = Noise(float2(X / 1300.0, Seed * 3.7)) * 1.4 + Noise(float2(X / 420.0, Seed * 5.3 + 9)) * 0.6 - 1;
    float gY = (P[1].x - P[4].z * hill) * 1080.0;

    float slot = floor(X / spacing);
    float f = 60;
    [unroll] for (int j = -2; j <= 2; j++)
    {
        float k = slot + j;
        float s = k * 1.618 + Seed * 31.7;
        float h1 = Hash11(s), h2 = Hash11(s + 0.37), h3 = Hash11(s + 0.71), h4 = Hash11(s + 1.13);
        float height = lerp(P[1].y, P[1].z, pow(h2, 1.5)) * 1080.0;
        float halfW = min(height * lerp(0.1, 0.16, h3) * P[3].a, spacing * 2.4);
        float cx = (k + 0.5 + (h1 - 0.5) * 0.8) * spacing;
        float v = (gY - Y) / height;
        float bend = P[4].w * sin(Time * (0.5 + 0.3 * h1) + h3 * 6.2832) * v * v;
        float dx = abs(X - cx - bend);

        // 가지 층: 층마다 아래가 넓고 위로 좁아지는 톱니. 바깥으로 갈수록 처져(droop) 층 경계가 기운다.
        // 좌우 층 위치·층마다 가지 길이를 따로 흔들어 크리스마스 트리처럼 반듯해 보이지 않게 한다.
        float side = X > cx + bend ? 1 : 0;
        float vc = (v - 0.03) / 0.97;
        float n = max(3, floor(P[6].x * lerp(0.6, 1.0, h2) + 0.5));
        float q = vc * n + 0.9 * dx / max(halfW, 1) + side * (0.3 + 0.4 * h3);
        float tier = floor(q);
        float st = q - tier;
        float e = clamp(5.0 * n / height, 0.04, 0.45);
        float saw = st < 1 - e ? st / (1 - e) : (1 - st) / e;
        float jag = 1 + (Hash11(tier * 7.13 + k * 3.31 + side * 0.5) - 0.5) * P[6].y * 0.9;
        float twig = 1 + (Hash11(floor(q * 3) * 3.7 + k * 1.9 + side * 0.25) - 0.5) * P[6].y * 0.35;
        float cone = pow(saturate(1 - vc), 0.9);
        float hw = halfW * cone * (1 - 0.6 * pow(saw, 0.7)) * jag * twig;
        float crown = max(dx - hw, (vc - 1) * height);
        float trunk = dx - max(height * 0.01, 1.0);
        float ft = vc < 0 ? trunk : crown;
        bool cleared = abs(cx - (Resolution.x * 0.5 / unit + P[4].x)) < P[6].w;   // clearCenter: 가운데 비우기(0이면 끔)
        f = min(f, h4 < P[2].x && !cleared ? ft : 60);
    }
    f = clamp(min(f, gY - Y), -60, 60);
    // 화면 픽셀 단위 거리(대략). 나무 안쪽의 층 경계·좌우 경계에서 f 가 끊겨 fwidth 가 튀므로 상한을 둔다.
    float fw = clamp(fwidth(f), 0.02, 2.0 / unit);
    float d = f / fw;
    float alpha = saturate(0.5 - d) * P[0].a;

    float fogRamp = 1 - smoothstep(0, max(P[2].w, 0.001) * 1080.0, gY - Y);
    float baseFog = P[2].z * fogRamp;
    alpha *= 1 - P[6].z * fogRamp;   // 밑동을 투명하게 녹여 지면 경계가 보이지 않게
    float mixHaze = saturate(P[2].y + (1 - P[2].y) * baseFog);
    float rim = exp(-max(-d, 0) / (1.2 * unit));
    float3 color = lerp(P[0].rgb, P[3].rgb, mixHaze) + P[5].rgb * P[5].a * rim * (1 - mixHaze);
    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(color * alpha, alpha);
}
