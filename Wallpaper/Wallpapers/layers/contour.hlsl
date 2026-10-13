// contour: P0 바탕 색(a=높이 색조 채움), P1 낮은 곳 선 색(a=선 불투명도), P2 높은 곳 선 색(a=높이별 색 섞임)
//          P3 (선 개수, 선 두께 px, 굵은 선 간격 N, 굵은 선 두께 배율), P4 (무늬 크기 px, 변화 속도, 굵은 선 밝기 배율, 가장자리 흐려짐)

// 지형용 부드러운 잡음(옥타브 수 고정 → 효과 품질과 관계없이 같은 지형, 낮은 품질만 1회 줄임)
float ContourFbm(float2 p)
{
    float value = 0, amplitude = 0.5, total = 0;
    int octaves = Quality >= 1 ? 4 : 3;
    for (int k = 0; k < octaves; k++)
    {
        value += Noise(p) * amplitude;
        total += amplitude;
        p = mul(float2x2(1.6, 1.2, -1.2, 1.6), p) + 3.17;
        amplitude *= 0.45;
    }
    return value / total;
}

// 1px 안티에일리어싱한 등고선 덮임(0~1). 선이 너무 촘촘해지면 평균 밝기로 바꿔 무아레를 막는다.
float ContourLine(float v, float widthPx)
{
    float fw = max(fwidth(v), 1e-5);
    float dist = abs(frac(v + 0.5) - 0.5) / fw;
    float stroke = saturate(widthPx * 0.5 + 0.5 - dist);
    float average = saturate(widthPx * fw);
    return lerp(stroke, average, smoothstep(0.25, 0.6, fw));
}

float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float2 p = CanvasPixel(i) / (max(P[4].x, 10) * unit) + Seed * 3.71;
    // 두 겹의 지형이 서로 다른 큰 원을 따라 천천히 움직이며 겹쳐져 모양이 변한다(Time 이 커져도 좌표는 유한).
    float t = Time * P[4].y;
    float2 o1 = float2(cos(t / 30), sin(t / 30)) * 30;
    float2 o2 = float2(sin(t / 23 + 1.3), cos(t / 23 + 1.3)) * 23;
    float h = ContourFbm(p + o1) * 0.65 + ContourFbm(p * 0.7 + o2 + 11.3) * 0.35;
    // 자르지 않는다: 잘라서 평평한 곳이 생기면 그 면 전체가 선으로 칠해진다.
    h = (h - 0.25) / 0.5;
    float hc = saturate(h);

    float levels = max(P[3].x, 1);
    float v = h * levels;
    float n = max(round(P[3].z), 1);
    float widthPx = max(P[3].y * unit, 0.8);
    float minor = ContourLine(v, widthPx);
    float major = ContourLine(v / n, widthPx * P[3].w);
    float alpha = max(minor, major * P[4].z);

    float3 lineColor = lerp(P[1].rgb, P[2].rgb, hc * P[2].a);
    float3 bg = P[0].rgb * lerp(1, 0.7 + 0.6 * hc, P[0].a) + lineColor * hc * P[0].a * 0.06;
    // 가장자리 흐려짐은 화면마다(continue 서브도 제 화면 가장자리 기준). 메인에서는 ScreenUV = 캔버스 좌표라 그대로다.
    float edge = 1 - P[4].w * smoothstep(0.35, 0.75, length((ScreenUV(i) - 0.5) * float2(Screen.x / Screen.y, 1)));
    float3 color = lerp(bg, lineColor, saturate(alpha * P[1].a * edge));
    color += (Dither(i.pos.xy) - 0.5) / 255.0;
    return float4(color, 1);
}
