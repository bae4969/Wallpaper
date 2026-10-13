// lightrays(빛줄기): P0 색(a=밝기), P1 (점 x, 점 y, 가운데 방향 도, 부채꼴 반폭 도), P2 (촘촘함, 선명함, 속도, 옅어지는 길이)
//                   P3 (점 번짐 세기, 번짐 크기 px, 흐르는 얼룩, 아래로 옅어짐)
// 방향은 단위 벡터로 잡음을 읽어(원 둘레를 따라 읽기) atan2 의 이음매가 생기지 않는다. 잡음 3회.
float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float2 dv = (CanvasPixel(i) - P[1].xy * Resolution) / unit;   // 1080p 기준 px
    float dist = max(length(dv), 1e-3);
    float2 ud = dv / dist;

    // 부채꼴 창: 가운데 방향과의 각도 차(1-cos ~ 각도^2/2)로 가우스
    float dirRad = radians(P[1].z);
    float spread = max(radians(P[1].w), 0.01);
    float cosd = dot(ud, float2(sin(dirRad), cos(dirRad)));
    float window = exp(-2 * (1 - cosd) / (spread * spread));

    // 빛줄기: 원 둘레를 따라 읽은 잡음. 시간에 따라 읽는 위치가 미끄러져 천천히 일렁인다.
    float t = Time * P[2].z;
    float n = Noise(ud * P[2].x + float2(t, -t * 0.6)) * 0.62
            + Noise(ud * P[2].x * 2.3 + float2(5.2 - t * 1.3, 1.7 + t * 0.9)) * 0.38;
    float rays = pow(saturate(n * 1.15), max(P[2].y, 0.1));
    // 점 가까이에선 줄기가 한데 모여 지글거리므로 평균값으로 녹인다
    rays = lerp(0.3, rays, saturate(dist / 220.0));

    // 줄기를 따라 바깥으로 흘러가는 얼룩(먼지·안개)
    float dust = Noise(ud * 5.0 + float2(1.0, 0.6) * (dist / 160.0 - Time * P[2].z * 8.0) + 11.0);
    float body = rays * window * lerp(1, 0.35 + 1.3 * dust, P[3].z);
    body *= exp(-dist / max(P[2].w * 1080.0, 1)) * (1 - P[3].w * i.uv.y);

    float r = dist / max(P[3].y, 1);
    float glow = P[3].x * (exp(-r * r) * 0.6 + exp(-r) * 0.4);

    float3 color = P[0].rgb * P[0].a * (body + glow);
    color = floor(color * 255.0 + Dither(i.pos.xy)) / 255.0;   // 8비트 띠를 디더로 흩뜨림
    return float4(color, 0);
}
