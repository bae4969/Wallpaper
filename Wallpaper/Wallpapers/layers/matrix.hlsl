// matrix: P0 글자 색(a=밝기), P1 머리 색(a=머리 밝기), P2 (글자 칸 높이 px, 낙하 속도 칸/초, 꼬리 길이 칸, 줄기 밀도)
//         P3 (색 모드 0=한 색 1=무지개, 번짐 세기, 글자 바뀜 횟수/초, 무지개 변화 속도), P4.x 낙하 높이(메인 화면 높이 배수)

// 정수 해시(PCG). 시간·칸 번호가 커져도 품질이 떨어지지 않는다.
uint MxPcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}
float MxHash(uint a, uint b, uint c) { return MxPcg(a + MxPcg(b + MxPcg(c))) / 4294967295.0; }

// 글자 점 하나가 켜졌는지. 5x7 격자 위 16개 획(가로 6·세로 6·대각 4) 중 일부를 켜서 글자처럼 보이게 한다.
float MxGlyphBit(uint glyph, int x, int y)
{
    uint m = (MxPcg(glyph) & MxPcg(glyph ^ 0x9E3779B9u)) | (1u << (glyph % 16u)) | (1u << ((glyph >> 8) % 16u));
    bool top = y <= 3, bot = y >= 3, left = x <= 2, right = x >= 2;
    int dt = (y * 2 + 1) / 3;
    int db = ((y - 3) * 2 + 1) / 3;
    uint hit = 0;
    hit |= (y == 0 && left) ? 1u : 0u;
    hit |= (y == 0 && right) ? 2u : 0u;
    hit |= (y == 3 && left) ? 4u : 0u;
    hit |= (y == 3 && right) ? 8u : 0u;
    hit |= (y == 6 && left) ? 16u : 0u;
    hit |= (y == 6 && right) ? 32u : 0u;
    hit |= (x == 0 && top) ? 64u : 0u;
    hit |= (x == 2 && top) ? 128u : 0u;
    hit |= (x == 4 && top) ? 256u : 0u;
    hit |= (x == 0 && bot) ? 512u : 0u;
    hit |= (x == 2 && bot) ? 1024u : 0u;
    hit |= (x == 4 && bot) ? 2048u : 0u;
    hit |= (top && x == dt) ? 4096u : 0u;
    hit |= (top && x == 4 - dt) ? 8192u : 0u;
    hit |= (bot && x == 2 + db) ? 16384u : 0u;
    hit |= (bot && x == 2 - db) ? 32768u : 0u;
    return (hit & m) != 0u ? 1.0 : 0.0;
}

float3 MxHue(float h) { return saturate(abs(frac(h + float3(0, 2, 1) / 3.0) * 6 - 3) - 1); }

float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float cellH = max(P[2].x, 4) * unit;
    float cellW = cellH * 0.72;
    uint seed = (uint)(Seed * 7919 + 17);
    float2 px = CanvasPixel(i);
    // 열마다 살짝 위아래로 어긋나게 해서 격자 느낌을 줄인다.
    float colF = floor(px.x / cellW);
    uint col = (uint)(colF + 4096);
    float colShift = MxHash(col, seed, 3u) * cellH;
    float rowF = floor((px.y + colShift) / cellH);
    float2 cell = float2(frac(px.x / cellW), frac((px.y + colShift) / cellH));
    float rows = Resolution.y / cellH;

    // 줄기(떨어지는 머리와 꼬리) 밝기. 열마다 줄기 2개(낮은 품질은 1개).
    // 낙하 높이 fall: 줄기가 캔버스 높이의 몇 배를 떨어진 뒤 다시 위에서 나오는지(continue 서브까지 이어 내릴 때 1보다 크게).
    // 한 회차가 길어지는 만큼 줄기 수를 늘려 메인 화면의 밀도를 비슷하게 유지한다.
    float fall = clamp(P[4].x, 1, 3);
    float lit = 0, head = 0;
    int streams = (Quality >= 1 ? 2 : 1) * (int)ceil(fall - 0.01);
    for (int k = 0; k < streams; k++)
    {
        uint ks = (uint)k * 31u + seed;
        float speed = P[2].y * (0.45 + 1.1 * MxHash(col, ks, 5u));
        float trail = P[2].z * (0.6 + 0.8 * MxHash(col, ks, 7u));
        float period = (rows * fall + trail) * (1.0 + 1.6 * (1.0 - saturate(P[2].w)) + MxHash(col, ks, 9u) * 0.6);
        float cycleT = period / max(speed, 0.01);
        float tt = Time + MxHash(col, ks, 11u) * cycleT;
        float cycle = floor(tt / cycleT);
        // 이 회차에 줄기가 나오는지(밀도)
        float on = MxHash(col, ks, (uint)cycle + 13u) < saturate(P[2].w) * 1.3 ? 1.0 : 0.0;
        float headRow = floor((tt - cycle * cycleT) * speed) - trail * 0.2;
        float d = headRow - rowF;
        if (d >= 0 && d < trail)
        {
            float a = 1 - d / trail;
            lit = max(lit, a * sqrt(a) * on);
            head = max(head, (d < 1 ? 1.0 : 0.0) * on);
        }
    }
    if (lit <= 0.001) return 0;

    // 글자: 5x7 점 무늬. 칸마다 가끔 바뀐다.
    float change = floor(Time * P[3].z * (0.5 + MxHash(col, (uint)(rowF + 4096), seed)) + MxHash(col, (uint)(rowF + 4096), 19u) * 64);
    uint glyph = MxPcg(col * 73856093u ^ (uint)(rowF + 4096) * 19349663u ^ (uint)change * 83492791u ^ seed);
    float2 g = (cell - float2(0.14, 0.12)) / float2(0.72, 0.76) * float2(5, 7);
    float3 color = 0;
    float bit = 0;
    if (all(g >= 0) && all(g < float2(5, 7)))
    {
        int2 gi = int2(g);
        // 가끔 좌우를 뒤집어 모양을 늘린다.
        int gx = (glyph & 0x10000u) ? 4 - gi.x : gi.x;
        float on = MxGlyphBit(glyph, gx, gi.y);
        // 점 모양을 조금 둥글게 + 경계 부드럽게(1px)
        float2 f = abs(frac(g) - 0.5);
        float pxPerBit = cellW * 0.72 / 5;
        float e = max(f.x, f.y) * pxPerBit;
        bit = on * saturate(pxPerBit * 0.47 - e + 0.5);
    }
    // 번짐: 칸 가운데로 모이는 부드러운 빛(칸 경계에서 0).
    float2 c = (cell - 0.5) * 2;
    float halo = saturate(1 - dot(c, c)) ;
    halo *= halo * P[3].y * 1.5;

    float3 base = P[0].rgb;
    if (P[3].x > 0.5)
        base = MxHue(frac(colF * 0.013 + rowF * 0.004 + Time * P[3].w)) * 0.85 + 0.15;
    float3 headColor = lerp(base, P[1].rgb, 0.8);

    float glyphLight = bit * (lit * P[0].a + head * P[1].a);
    color = lerp(base, headColor, head) * glyphLight;
    color += lerp(base, headColor, head * 0.5) * halo * (lit * 0.35 + head * 0.6) * P[0].a;
    return float4(color, 0);
}
