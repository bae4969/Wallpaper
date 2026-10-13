// grass: P0 밑동 색(a=불투명도), P1 끝 색(a=섞임), P2 먼 줄 색(a=섞임), P3 (맨 뒤 줄 바닥 높이, 풀 높이 px, 촘촘함, 풀잎 폭 px)
//        P4 (바람 세기, 바람 속도, 돌풍 크기 px, 줄 간격), P5 (줄 수, 들쭉날쭉, 빛 방향, 꽃 크기 px)
//        P6 꽃잎 색(a=꽃 달린 풀잎 비율), P7 꽃 가운데 색(a=밑동 그늘)
// 줄마다 풀잎 칸을 나누고, 이 픽셀 근처 칸의 풀잎 몇 개만 검사한다. 바람에 휜 만큼 좌표를 되돌려 찾으므로 많이 휘어도 가볍다.

// 돌풍 세기 0~1: 가로로 지나가는 돌풍 물결
float GustAt(float x, float t, float s)
{
    float gustSize = max(P[4].z, 10);
    float g = Noise(float2((x - t * 260) / gustSize, s));
    float wave = 0.5 + 0.5 * sin((x - t * 260) / gustSize * 2.3 + s);
    return saturate(0.7 * g + 0.6 * wave * g);
}

float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float X = CanvasPixel(i).x / unit;                    // 1080p 기준 px
    float Y = CanvasPixel(i).y / unit;
    float t = Time * P[4].y;
    int rows = (int)clamp(round(P[5].x), 1, 3);
    bool skipMiddle = Quality < 1 && rows == 3;  // 품질이 낮으면 가운데 줄을 뺀다(맨 앞·맨 뒤는 남긴다)
    int reach = Quality >= 1 ? 2 : 1;

    float3 total = 0;
    float alphaTotal = 0;
    [loop] for (int k = 0; k < rows; k++)
    {
        if (skipMiddle && k == 1) continue;
        float depth = rows > 1 ? k / (float)(rows - 1) : 1;   // 0 = 맨 뒤, 1 = 맨 앞
        float rowScale = lerp(0.42, 1.0, depth);
        float s = Seed * 13.7 + k * 31.1;
        float base = (P[3].x + P[4].w * k) * 1080
            + (sin(X * 0.0041 + s) * 6 + sin(X * 0.0017 + s * 1.7) * 10) * rowScale;
        float height = P[3].y * rowScale;
        float above = base - Y;                  // 바닥에서 위로 잰 높이
        if (above > height * 1.3 + (P[6].a > 0 ? P[5].w * 2 : 0)) continue;

        float3 baseColor = lerp(P[0].rgb, P[2].rgb, (1 - depth) * P[2].a);
        float3 tipColor = lerp(P[1].rgb, P[2].rgb * 1.15, (1 - depth) * P[2].a * 0.8);
        float4 layer = 0;
        if (above <= 0)
        {
            // 바닥 아래는 빽빽한 풀 덩어리: 아래로 갈수록 그늘지고 세로 결이 살짝 보인다.
            float streak = Hash11(floor(X / (P[3].w * rowScale * 0.7)) + s);
            float shade = lerp(1.0, 0.62, saturate(-above / (height * 0.6))) * lerp(0.9, 1.04, streak);
            shade = lerp(shade, 0.62 * lerp(0.9, 1.04, streak), P[7].a);   // 밑동 그늘: 덩어리를 고르게 어둡게(줄 경계선이 안 보인다)
            layer = float4(baseColor * shade, saturate(-above * unit + 0.5));
        }
        if (above > -2)
        {
            // 바람: 가로로 지나가는 돌풍 물결 + 잔 떨림.
            float gust = GustAt(X, t, s);
            float lean = P[4].x * (0.2 + 1.1 * gust);
            float spacing = 100 / max(P[3].z, 1) * rowScale;
            float width = P[3].w * rowScale;
            float uGuess = saturate(above / height);
            float shift = lean * height * (uGuess * uGuess * 0.75 + uGuess * 0.25);
            float cell = floor((X - shift) / spacing);
            // 키 큰 풀(가까운 줄)은 풀잎마다 다른 기울기·높이로 끝이 멀리 벗어나므로 검사할 칸을 넓힌다.
            int span = reach;
            bool tall = height > 260;
            if (tall)
            {
                float stray = (0.25 + lean * P[5].y) * height * (uGuess * uGuess * 0.75 + uGuess * 0.25) + width;
                span = (int)clamp(ceil(stray / spacing), reach, Quality >= 1 ? 8 : 4);
            }
            [loop] for (int j = -span; j <= span; j++)
            {
                float id = cell + j + s * 7.0;
                float h1 = Hash11(id * 1.37), h2 = Hash11(id * 2.71), h3 = Hash11(id * 4.13), h4 = Hash11(id * 5.97);
                float bh = height * lerp(1 - P[5].y, 1.15, h2);
                float bx = (cell + j + h1) * spacing;
                // 키 큰 풀은 풀잎 밑동 자리의 돌풍으로 한 풀잎 전체를 휜다(픽셀마다 다른 돌풍을 쓰면 긴 풀잎이 S자로 꺾인다).
                float bGust = tall ? GustAt(bx, t, s) : gust;
                float bLean = tall ? P[4].x * (0.2 + 1.1 * bGust) : lean;
                float myLean = bLean + (h3 - 0.5) * 0.5 + sin(Time * (1.3 + h4) + h1 * 30) * 0.03 * P[4].x;
                float u = above / bh;
                // 꽃: 일부 풀잎 끝에 옆에서 본 꽃송이(가로로 납작한 꽃잎 고리 + 가운데)를 단다. 풀잎과 함께 흔들린다.
                float4 flower = 0;
                if (P[6].a > 0 && Hash11(id * 7.31) < P[6].a)
                {
                    float fr = P[5].w * rowScale * lerp(0.75, 1.25, h3);
                    float2 d = float2(X - (bx + myLean * bh), (Y - (base - bh)) * 1.7);
                    float r = length(d);
                    float edge = fr * (0.8 + 0.2 * cos(atan2(d.y, d.x) * 7 + h4 * 6.2832));
                    float fc = saturate((edge - r) * unit + 0.5);
                    if (fc > 0)
                    {
                        float3 petal = P[6].rgb * lerp(0.82, 1.06, saturate(0.5 - d.y / (fr * 2) + h1 * 0.3));
                        float3 fcolor = lerp(petal, P[7].rgb, saturate((fr * 0.36 - r) * unit + 0.5));
                        flower = float4(fcolor, fc);
                    }
                }
                if (u > 1 || u < -0.05)
                {
                    if (flower.a > 0) { layer.rgb = layer.a > 0 ? lerp(layer.rgb, flower.rgb, flower.a) : flower.rgb; layer.a = max(layer.a, flower.a); }
                    continue;
                }
                u = saturate(u);
                float cx = bx + myLean * bh * (u * u * 0.75 + u * 0.25);
                float hw = width * 0.5 * pow(1 - u, 0.75) * lerp(0.7, 1.2, h4);
                if (P[6].a > 0 && Hash11(id * 7.31) < P[6].a) hw = max(hw, width * 0.07 + 0.6);   // 꽃대는 끝까지 가늘게 남긴다
                float cover = saturate((hw - abs(X - cx)) * unit + 0.5);
                if (cover <= 0)
                {
                    if (flower.a > 0) { layer.rgb = layer.a > 0 ? lerp(layer.rgb, flower.rgb, flower.a) : flower.rgb; layer.a = max(layer.a, flower.a); }
                    continue;
                }
                // 밑동은 바닥 색과 같게 두어 줄 경계가 보이지 않게 하고, 위로 갈수록 풀잎마다 밝기가 다르다.
                float3 c = lerp(baseColor, tipColor, pow(u, 1.4) * P[1].a) * lerp(1, lerp(0.8, 1.12, h3), saturate(u * 2.5));
                c *= 1 + P[5].z * 0.12 * sign(X - cx) * saturate(u * 3);   // 해 쪽 면은 밝게
                c *= 1 + (bGust - 0.35) * 0.35 * u;                          // 돌풍에 누운 풀은 밝게 보여 바람 물결이 드러난다
                c *= lerp(1, lerp(0.62, 1, saturate(u * 2.5)), P[7].a);      // 밑동 그늘
                layer.rgb = layer.a > 0 ? lerp(layer.rgb, c, cover) : c;
                layer.a = max(layer.a, cover);
                if (flower.a > 0) { layer.rgb = lerp(layer.rgb, flower.rgb, flower.a); layer.a = max(layer.a, flower.a); }
            }
        }
        // 뒤에서 앞으로 겹친다(premultiplied).
        total = layer.rgb * layer.a + total * (1 - layer.a);
        alphaTotal = layer.a + alphaTotal * (1 - layer.a);
    }
    float alpha = alphaTotal * P[0].a;
    return float4(total * P[0].a, alpha);
}
