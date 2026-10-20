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

// Noise 와 같은 값(z)과 그 기울기(xy)를 한 번에 구한다.
float3 NoiseGrad(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3 - 2 * f), du = 6 * f * (1 - f);
    float a = Hash21(WrapCell(i)), b = Hash21(WrapCell(i + float2(1, 0)));
    float c = Hash21(WrapCell(i + float2(0, 1))), d = Hash21(WrapCell(i + float2(1, 1)));
    float m = a - b - c + d;
    return float3(du * float2(b - a + m * u.y, c - a + m * u.x), lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y));
}

// 수면 물결의 기울기(dh/dx, dh/dz). 물결 = 잡음으로 구부린 가로 물결 줄 몇 겹이고, 기울기는 미분식으로 바로 구한다.
// w 는 수면 위 위치(앞쪽 물결 크기 단위), footY 는 픽셀 하나가 덮는 앞뒤 길이. 먼 곳에서 픽셀보다 잘아지는 겹은 줄인다.
float2 RippleSlope(float2 w, float footY, float t)
{
    float2 slope = 0;
    float amp = 1, len = 2;
    int count = Quality >= 1 ? 4 : 3;
    for (int k = 0; k < count; k++)
    {
        float lod = saturate(len / (footY * 3) - 0.5);
        float2 dq = float2(1 / (len * 3), 1 / len);   // dq/dw
        float2 q = w * dq + float2(k * 17.3 + t * 0.05, k * 5.1 + t * 0.1);
        float3 n1 = NoiseGrad(q * float2(1, 0.5)), n2 = NoiseGrad(q * 2.1 + 3.7);
        float3 n3 = NoiseGrad(q * 0.35 + 9.1);   // 물결이 센 곳과 잔잔한 곳
        float bend = (n1.z - 0.5) * 1.2 + (n2.z - 0.5) * 0.5;
        float2 dbend = (n1.xy * float2(1.2, 0.6) + n2.xy * 1.05) * dq;
        float phase = (w.y / len + bend) * 6.2832 + t * 2 / sqrt(len) + k * 1.9;
        float sn, cs;
        sincos(phase, sn, cs);
        float c = amp * len * lod * 0.1;
        // h = c * sin(phase) * (0.3 + patch) 의 미분
        slope += c * (cs * (dbend + float2(0, 1 / len)) * 6.2832 * (0.3 + n3.z) + sn * n3.xy * 0.35 * dq);
        amp *= 0.6;
        len *= 0.55;
    }
    return slope;
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
    // 수면을 원근 평면으로 본다(화면 위 끝 = 수평선에서 near 만큼 내려온 곳). 화면 아래에서 물결 크기 1 = wavelength px.
    const float near = 0.35;
    float s = depth + near;
    float scale = Screen.y / max(P[1].y * unit, 1) * (1 + near);
    float2 world = float2((ScreenUV(i).x - 0.5) * Screen.x / Screen.y, 1) / s * scale;
    float footY = scale / (s * s * Screen.y);
    // 물결 기울기만큼 반사를 밀고 밝기를 바꾼다. 앞뒤 기울기가 주로, 좌우는 덜.
    float2 slope = RippleSlope(world, footY, t);
    float amp = P[1].x * unit * s / (1 + near);
    float2 offset = float2(slope.x * 0.4, slope.y) * amp / Screen;
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
    // 앞을 향한 물결 면은 밝게, 뒤를 향한 면은 어둡게(물결 줄). 세기는 일렁임 폭에 비례(5px 에서 0.12).
    color *= 1 + clamp(slope.y, -1.5, 1.5) * min(P[1].x * 0.024, 0.25);
    float luminance = dot(color, float3(0.3, 0.5, 0.2));
    // 반짝임은 가로로 길게 늘인 잡음의 꼭대기에서만 생긴다.
    float n3 = Noise(float2(px.x / (wavelength * 7), px.y / (wavelength * 0.45)) + float2(t * 0.5, -t * 2.2) + 41.3);
    float glint = pow(saturate(n3 * 1.6 - 0.6), 10) * P[2].x;
    color += glint * (color * 2.5 + luminance * 1.5);
    color += rain * (color * 1.2 + luminance + P[0].rgb * 0.04);
    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(color, 1);
}
