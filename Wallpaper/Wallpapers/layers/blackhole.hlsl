// blackhole: P0 뜨거운 색(a=밝기), P1 원반 색(a=도플러), P2 빛 번짐 색(a=세기), P3 (중심 x, 중심 y, 지평선 반지름 px, 기울기 도)
//            P4 (납작함, 원반 바깥 반지름 배수, 회전 속도 바퀴/초, 렌즈 고리 세기), P5 (결 촘촘함, 결 대비)

// 가로 방향으로 per 칸마다 되풀이되는 값 잡음. 각도를 넣어도 이음매가 생기지 않는다.
float LoopNoise(float2 p, float per)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3 - 2 * f);
    float x0 = i.x - per * floor(i.x / per);
    float x1 = x0 + 1 > per - 0.5 ? 0 : x0 + 1;
    float a = Hash21(float2(x0, i.y)), b = Hash21(float2(x1, i.y));
    float c = Hash21(float2(x0, i.y + 1)), d = Hash21(float2(x1, i.y + 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// fw: 한 픽셀에 들어가는 첫 옥타브 칸 수. 칸이 픽셀보다 잘아지는 옥타브는 평균값으로 바꿔 물결 무늬(모아레)를 막는다.
float LoopFbm(float2 p, float per, float fw)
{
    float value = 0, amplitude = 0.5, total = 0;
    int octaves = Quality >= 1.5 ? 4 : 3;
    for (int k = 0; k < octaves; k++)
    {
        float keep = saturate(2 - fw * 3);
        value += (keep > 0 ? lerp(0.5, LoopNoise(p, per), keep) : 0.5) * amplitude;
        total += amplitude;
        p *= 2; per *= 2; amplitude *= 0.5; fw *= 2;
    }
    return value / total;
}

// 원반 한 점의 빛. qq 는 기울기를 바로잡은 화면 좌표(지평선 반지름 단위), side 는 +1 앞쪽 절반·-1 뒤쪽 절반만·0 전부.
float3 Disk(float2 qq, float side, float spin, float px, float freq)
{
    float k = max(P[4].x, 0.02);
    float rout = max(P[4].y, 2);
    float rin = 1.55;
    float2 d = float2(qq.x, qq.y / k);
    float rd = length(d);
    if (d.y * side < 0 || rd > rout * 1.05 || rd < rin * 0.85) return 0;
    // 왼쪽이 다가오는 쪽이라 더 밝고 하얗다(도플러).
    float boost = pow(saturate(1 - saturate(P[1].a) * 0.75 * d.x / rd), 1.7);
    float ang = atan2(d.y, d.x) * 0.159155;
    float fw = max(P[5].x * freq * px * (abs(d.x) + abs(d.y) / k) / rd, 28 * 0.159155 * px / (rd * k));
    float n = LoopFbm(float2(frac(ang + spin) * 28, rd * P[5].x * freq), 28, fw);
    float streak = lerp(1, 0.25 + 1.5 * smoothstep(0.3, 0.72, n), saturate(P[5].y));
    float prof = smoothstep(rin * 0.9, rin * 1.05, rd) * (exp(-(rd - rin) / (rout * 0.22)) + 0.6 * exp(-(rd - rin) * 3)) * smoothstep(rout, rout * 0.4, rd);
    float heat = saturate(exp(-(rd - rin) * 1.4) * (0.5 + 0.5 * boost));
    return lerp(P[1].rgb, P[0].rgb, heat) * prof * boost * streak;
}

float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float R = max(P[3].z * unit, 1);
    float2 p = (CanvasPixel(i) - P[3].xy * Resolution) / R;
    float tilt = radians(P[3].w);
    float cs = cos(tilt), sn = sin(tilt);
    float2 q = float2(cs * p.x + sn * p.y, -sn * p.x + cs * p.y);
    float r = max(length(q), 1e-3);
    float hole = smoothstep(1 + 1.2 / R, 1 - 1.2 / R, r);
    float rout = max(P[4].y, 2);
    float spin = frac(Time * P[4].z);
    float lens = P[4].w;
    float3 col = 0;

    // 앞쪽 절반: 지평선 앞을 가로지른다.
    float px = 1 / R;
    col += Disk(q, 1, spin, px, 1) * 1.9;

    if (r > 1 - 2 / R)
    {
        // 뒤쪽 절반: 빛이 휘어 실제보다 바깥에 보인다. 화면의 점을 중심 쪽으로 당겨 원반을 찾으면
        // 지평선 위로 원반 전체가 활처럼 감기는 모습이 된다. 가로 띠 높이에서는 휘지 않게 해 앞쪽과 이어 붙인다.
        float s = smoothstep(-0.05, 0.9, -q.y / r);
        float bend = lens * s;
        float2 ql = q * (1 - bend / (r * r * r));
        // 활 모양으로 눌린 곳은 결이 픽셀보다 잘아지므로 결 간격을 넓힌다.
        col += Disk(ql, -1, spin, px * (1 + 2 * bend / (r * r * r)), 1 - 0.65 * s) * 1.6 * (1 - hole);

        // 두 번째 상: 지평선 바로 바깥의 가는 고리. 반대편 원반이 아주 얇게 눌려 보인다.
        float t = (r - 1) / 0.13;
        if (t < 1.2)
        {
            float2 ds = -(q / r) * (1.5 + t * (rout - 1.5));
            float2 qs = float2(ds.x, ds.y * max(P[4].x, 0.02));
            col += Disk(qs, 0, spin, px * (rout - 1.5) / 0.13 * max(P[4].x, 0.02), 0.3) * lens * 1.3 * smoothstep(1.2, 0.6, t) * (1 - hole);
        }
    }

    // 광자 고리: 지평선 바로 바깥의 가늘고 밝은 선.
    float lboost = pow(saturate(1 - saturate(P[1].a) * 0.6 * q.x / r), 2);
    float ring = exp(-pow((r - 1.015) / 0.012, 2));
    col += P[0].rgb * ring * (0.4 + 0.6 * lboost);

    // 빛 번짐: 지평선 둘레와 원반 띠 둘레.
    float boost = pow(saturate(1 - saturate(P[1].a) * 0.75 * q.x / max(abs(q.x), 1)), 1.7);
    float halo = exp(-max(r - 1, 0) * 1.6) * (1 - hole);
    float band = exp(-abs(q.y) / (0.22 + 0.06 * abs(q.x))) * exp(-abs(q.x) / (rout * 0.4)) * (0.4 + 0.6 * boost);
    col += P[2].rgb * P[2].a * (halo * 0.35 * lboost + band * 0.45) * (1 - hole * 0.7);

    col *= P[0].a;
    // 부드러운 톤 압축으로 하얗게 타는 것을 막는다.
    col = 1 - exp(-col * 1.15);
    col += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(max(col, 0), hole);
}
