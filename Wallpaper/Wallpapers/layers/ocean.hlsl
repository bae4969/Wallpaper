// ocean: P0 깊은 물 색(a=불투명도), P1 물마루 색(a=섞임), P2 비친 하늘 색(a=반사 세기), P3 햇빛 색(a=햇빛 반사 세기)
//        P4 (수평선 높이, 해 가로 위치, 해 세로 위치, 물결 크기), P5 (속도, 물결 높이, 흰 물거품, 반짝임)
//        P6 수평선 안개 색(a=안개 폭), P7 (물결 방향 도, 가까운 물결 디테일)
// 카메라 높이 1, 수평으로 바라보는 원근으로 바다 평면을 그린다. 물결은 사인파 여러 개의 합이고, 픽셀보다 잘아지는 물결은 줄여 반짝임이 깨지지 않게 한다.

static const float OceanAngle[8] = { 0.0, 0.55, -0.45, 1.05, -0.95, 0.25, -0.7, 0.85 };
static const float OceanPhase[8] = { 0.0, 1.7, 4.1, 2.6, 5.3, 0.9, 3.4, 6.0 };

float4 PS(V i) : SV_Target
{
    float horizon = P[4].x;
    float sy = i.uv.y - horizon;
    if (sy * Resolution.y < -1) return 0;
    float edge = saturate(sy * Resolution.y + 0.5);
    sy = max(sy, 0.5 / Resolution.y);
    float aspect = Resolution.x / Resolution.y;
    float sx = (i.uv.x - 0.5) * aspect;

    float scale = max(P[4].w, 0.01);
    float2 world = float2(sx, 1) / sy / scale;           // 바다 평면 위 위치(물결 크기 단위)
    float2 footprint = float2(1 / sy, 1 / (sy * sy)) / (Resolution.y * scale);  // 픽셀 하나가 덮는 넓이
    float t = Time * P[5].x;
    float steep = P[5].y;

    // 큰 물결이 너무 반듯하게 보이지 않도록 위치를 살짝 일그러뜨린다.
    float wobble = Noise(world * float2(0.35, 0.5) + float2(t * 0.05, Seed * 3.1)) - 0.5;

    int count = Quality >= 1 ? 8 : 6;
    float wind = radians(P[7].x);
    float h = 0, amplitudeSum = 0;
    float2 slope = 0, lost = 0;
    float wavelength = 1.6;
    for (int k = 0; k < count; k++)
    {
        float a = wind + OceanAngle[k];
        float2 dir = float2(sin(a), cos(a));
        float kk = 6.2832 / wavelength;
        float amp = wavelength * steep * 0.022;
        float pix = dot(abs(dir), footprint);
        float lod = saturate(wavelength / (pix * 4) - 0.5);
        float omega = sqrt(kk * 4.0);
        float arg = kk * dot(dir, world) - omega * t + OceanPhase[k] + wobble * (2.0 + k * 0.6);
        float sn, cs;
        sincos(arg, sn, cs);
        h += amp * lod * sn;
        slope += amp * lod * kk * cs * dir;
        amplitudeSum += amp;
        float s2 = amp * kk;
        lost += s2 * s2 * 0.5 * (1 - lod * lod) * dir * dir;
        wavelength *= 0.63;
    }
    // 가까운 바다(화면 아래·서브 continue): 잡음 경사로 잔물결을 더한다(사인파를 더 겹치면 격자 무늬가 생긴다).
    // 픽셀보다 잘면 사라지고(lod) 경사 분산(lost)에는 넣지 않아 먼 바다·빛 길 모양은 그대로다.
    float nearDetail = P[7].y;
    if (nearDetail > 0)
    {
        int extra = Quality >= 1.5 ? 4 : (Quality >= 1 ? 3 : 2);
        float freq = 9;
        [loop] for (int k = 0; k < extra; k++)
        {
            float lod = saturate(1 / (freq * footprint.y * 10) - 0.5);
            if (lod <= 0) break;
            float ca = cos(1.1 + k * 2.3), sa = sin(1.1 + k * 2.3);
            float2 q = mul(float2x2(ca, -sa, sa, ca), world * freq * float2(1, 1.4))
                + float2(t * 0.35, -t * 0.55) * (1 + k * 0.5) + Seed * 5.1 + k * 17.7;
            float n0 = Noise(q), nx = Noise(q + float2(0.3, 0)), ny = Noise(q + float2(0, 0.3));
            float2 g = float2(nx - n0, ny - n0) / 0.3;
            g = mul(g, float2x2(ca, -sa, sa, ca));                 // 돌린 좌표의 기울기를 바다 평면으로 되돌린다
            slope += g * steep * 0.11 * nearDetail * lod;
            h += (n0 - 0.5) * steep * 0.02 * nearDetail * lod / freq * 9;
            freq *= 1.9;
        }
    }

    float3 view = normalize(float3(sx, -sy, 1));
    float3 normal = normalize(float3(-slope.x, 1, -slope.y));
    float3 refl = reflect(view, normal);
    refl.y = abs(refl.y);
    float cosT = saturate(dot(-view, normal));
    float fresnel = 0.02 + 0.98 * pow(1 - cosT, 5);

    // 비친 하늘: 수평선 쪽은 안개 색, 위로 갈수록 하늘 색.
    float3 sky = lerp(P[6].rgb, P[2].rgb, saturate(refl.y * 3));
    float3 sun = normalize(float3((P[4].y - 0.5) * aspect, horizon - P[4].z, 1));

    // 물 자체 색: 물마루(높은 곳)는 밝은 물색이 비친다.
    float crest = saturate(h / max(amplitudeSum, 1e-4) * 1.2 + 0.5);
    float3 water = lerp(P[0].rgb, P[1].rgb, crest * crest * P[1].a);
    float3 color = lerp(water, sky, saturate(fresnel * P[2].a * 1.6));

    // 햇빛 반사(경사 공간): 해를 비추려면 필요한 물결 경사와 실제 경사의 차이로 밝기를 정한다.
    // 픽셀보다 잘아 사라진 잔물결의 경사 분산은 방향별로 더해 빛 길을 세로로 길게 퍼뜨린다.
    float3 hv = normalize(sun - view);
    float2 need = float2(-hv.x, -hv.z) / max(hv.y, 0.05);
    float2 diff = need - slope;
    float reflectance = 0.25 + fresnel;
    float2 v0 = 0.0005 + lost;                 // 날카로운 반짝임(해 원반 크기)
    float glint = exp(-0.5 * dot(diff * diff, 1 / v0)) * 0.0005 / sqrt(v0.x * v0.y);
    float2 v1 = 0.008 + lost;                   // 빛 길 둘레의 부드러운 빛
    float glow = exp(-0.5 * dot(diff * diff, 1 / v1)) * 0.008 / sqrt(v1.x * v1.y);
    float2 v2 = 0.05 + lost;                    // 넓은 따뜻한 번짐
    float wide = exp(-0.5 * dot(diff * diff, 1 / v2)) * 0.05 / sqrt(v2.x * v2.y);
    float spread = lost.x + lost.y;
    color += P[3].rgb * P[3].a * reflectance * (glint * P[5].w * 4 + glow + wide * 0.3);
    // 빛 길 안의 잔 반짝임: 아주 잔 물결이 해를 비추는 점. 멀어서 픽셀보다 잘아지면 줄인다.
    if (Quality >= 0.5)
    {
        float2 wq = world * float2(11, 7);
        float micro = Noise(wq + float2(t * 1.1, -t * 1.9) + Seed);
        micro = pow(saturate(micro * 1.6 - 0.6), 6);
        float fine = saturate(0.02 / footprint.y - 0.3);
        if (nearDetail > 0)
        {
            // 가까울수록 반짝임 점이 뭉개지지 않게 잡음 주기를 픽셀 크기에 맞춰 두 단계 사이로 섞는다.
            float level = max(log2(0.012 / footprint.y), 0);
            float l0 = floor(level), lf = level - l0;
            float2 drift = float2(t * 1.1, -t * 1.9) + Seed;
            float m0 = Noise(wq * exp2(l0) + drift * exp2(l0 * 0.5) + l0 * 7.3);
            float m1 = Noise(wq * exp2(l0 + 1) + drift * exp2(l0 * 0.5 + 0.5) + (l0 + 1) * 7.3);
            float m = lerp(m0, m1, lf);
            m = pow(saturate(m * 1.7 - 0.7), 6);
            micro = lerp(micro, m, saturate(level) * nearDetail);
        }
        color += P[3].rgb * P[3].a * reflectance * micro * fine * (glow * 2 + wide * 0.6) * P[5].w * 6;
    }

    // 흰 물거품: 물마루 꼭대기에 드문드문.
    if (P[5].z > 0)
    {
        float n = Noise(world * 1.3 + float2(-t * 0.1, t * 0.2));
        float foam = smoothstep(0.78, 0.95, crest * 0.75 + n * 0.45) * P[5].z * saturate(1 - spread * 8);
        color = lerp(color, P[1].rgb * 0.5 + 0.5 * sky + 0.25, foam);
    }

    // 수평선 안개
    float haze = exp(-sy / max(P[6].a, 1e-3));
    float3 hazeColor = P[6].rgb + P[3].rgb * P[3].a * 0.35 * pow(saturate(dot(normalize(float3(sx, 0, 1)), sun)), 400);
    color = lerp(color, hazeColor, haze);

    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    float alpha = edge * P[0].a;
    return float4(color * alpha, alpha);
}
