// water(잔물결 수면, 아래 레이어 전체를 다시 그림): P0 색조(a=반사 밝기), P1 (일렁임 px, 물결 크기 px, 속도, 흐림 mip)
//            P2 (반짝임, 수면 시작 높이, 앞쪽 어둡기, 빗방울 파문 세기), P3 (파문 크기 px, 0, 0, 0)

// 빗방울 파문 한 겹: 원근에 맞춰 뒤로 갈수록 작아지는 칸마다 파문 하나가 퍼진다.
// 반환: xy = 화면 px 밀림, z = 물결 마루의 밝기. cell = 앞쪽 칸 크기 px, span = 수면 높이 px.
float3 RainRing(float2 px, float perspective, float cell, float span, float top, float layer)
{
    const float squash = 0.32;                      // 파문 타원의 세로/가로 비
    float k = cell * squash * 0.7 / span;           // perspective 를 칸 줄 번호로 바꾸는 비율
    float row = floor(log(perspective) / k + layer * 0.5);
    float h0 = Hash21(float2(row, layer * 7.3));
    float pc = exp((row + 0.5 - layer * 0.5) * k);  // 줄 가운데의 원근 배율
    float cw = cell * pc;
    float col = floor(px.x / cw + h0 + layer * 0.37);
    float2 id = float2(col, row);
    float h1 = Hash21(id + layer * 17.1), h2 = Hash21(id * 1.7 + 3.1 + layer), h3 = Hash21(id * 2.3 + 7.7);
    float pr = exp((row + 0.5 + (h2 - 0.5) * 0.3 - layer * 0.5) * k);
    float2 center = float2((col + 0.5 + (h1 - 0.5) * 0.3 - h0 - layer * 0.37) * cw, top + (pr - 0.3) / 0.7 * span);
    float2 local = (px - center) / float2(cw, cw * squash);
    float d = length(local);
    float t = Time * (0.8 + 0.4 * h3) + h1 * 9.0;
    float phase = frac(t);
    if (Hash21(id + WrapCell(floor(t)) * 3.17) > 0.4) return 0;   // 칸마다 가끔씩만 떨어진다
    float x = d - (0.04 + phase * 0.42);
    float fade = (1 - phase) * (1 - phase) * smoothstep(0.5, 0.4, d);
    float wave = sin(x * 45) * exp(-x * x * 400) * fade;
    float2 dir = local / max(d, 1e-4);
    return float3(dir * float2(1, squash) * wave * cw * 0.05, max(wave, 0));
}

float4 PS(V i) : SV_Target
{
    if (ScreenUV(i).y < P[2].y) return float4(Source.SampleLevel(Linear, ScreenUV(i), 0).rgb, 1);
    float unit = Unit();
    float2 px = ScreenUV(i) * Screen;
    float depth = saturate((ScreenUV(i).y - P[2].y) / max(1 - P[2].y, 1e-3));
    // 먼 쪽(수평선 근처)은 물결이 잘고 납작하게, 앞쪽은 크게 보인다.
    float perspective = lerp(0.3, 1.0, depth);
    float wavelength = P[1].y * unit * perspective;
    float t = Time * P[1].z;
    float2 q = float2(px.x / (wavelength * 5), px.y / wavelength);
    float n1 = Noise(q + float2(t * 0.3, -t));
    float n2 = Noise(q * float2(2.3, 2.1) + float2(-t * 0.4, -t * 1.7) + 13.7);
    float wave = (n1 - 0.5) + (n2 - 0.5) * 0.5;
    float2 offset = float2(wave * 0.3, wave) * P[1].x * unit * perspective / Screen;
    float rain = 0;
    if (P[2].w > 0)   // rain: 빗방울 파문(0이면 끔)
    {
        float span = max(Screen.y * (1 - P[2].y), 1), cell = max(P[3].x, 4) * unit;
        float3 r = RainRing(px, perspective, cell, span, P[2].y * Screen.y, 0);
        if (Quality >= 1) r += RainRing(px, perspective, cell * 0.8, span, P[2].y * Screen.y, 1);
        offset += r.xy * P[2].w / Screen;
        rain = r.z * P[2].w;
    }
    float3 color = Source.SampleLevel(Linear, ScreenUV(i) + offset, P[1].w * (0.6 + 0.4 * depth)).rgb;
    color *= P[0].rgb * P[0].a * (1 - P[2].z * depth);
    float luminance = dot(color, float3(0.3, 0.5, 0.2));
    // 반짝임은 가로로 길게 늘인 잡음의 꼭대기에서만 생긴다.
    float n3 = Noise(float2(px.x / (wavelength * 7), px.y / (wavelength * 0.45)) + float2(t * 0.5, -t * 2.2) + 41.3);
    float glint = pow(saturate(n3 * 1.6 - 0.6), 10) * P[2].x;
    color += glint * (color * 2.5 + luminance * 1.5);
    color += rain * (color * 1.2 + luminance + P[0].rgb * 0.04);
    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(color, 1);
}
