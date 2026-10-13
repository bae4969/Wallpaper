// meteor(별똥별): P0 색(a=밝기), P1 (기회 간격 초, 꼬리 길이 px, 떨어지는 시간 초, 굵기 px), P2 (시작 위 경계, 아래 경계, 각도, 이동 거리 px)
float4 PS(V i) : SV_Target
{
    float unit = Unit();
    float interval = max(P[1].x, 1);
    float duration = max(P[1].z, 0.05);
    float3 total = 0;
    // 간격보다 오래 떨어지는 경우를 위해 직전 간격의 별똥별도 본다.
    [unroll] for (int k = 0; k < 2; k++)
    {
        float index = floor(Time / interval) - k;
        float salt = index + Seed * 31;
        float t = Time - index * interval - Hash11(salt * 1.7) * max(interval - duration, 0);
        if (Hash11(salt) < 0.4 || t < 0 || t > duration) continue;
        float p = t / duration;
        float2 origin = float2(lerp(0.1, 0.9, Hash11(salt * 3.3)), lerp(P[2].x, P[2].y, Hash11(salt * 4.7))) * Resolution;
        float angle = radians(P[2].z + (Hash11(salt * 5.9) - 0.5) * 20);
        float2 direction = float2(cos(angle) * (Hash11(salt * 6.1) > 0.5 ? 1 : -1), sin(angle));
        float2 head = origin + direction * P[2].w * unit * p;
        float tailLength = P[1].y * unit * saturate(p * 4);
        float2 d = CanvasPixel(i) - head;
        float along = dot(d, -direction);
        float across = dot(d, float2(-direction.y, direction.x));
        float width = P[1].w * unit;
        float tail = along > 0 ? saturate(1 - along / max(tailLength, 1)) : 0;
        float trail = exp(-across * across / (width * width * (0.3 + 0.7 * tail))) * tail * tail;
        float spark = exp(-dot(d, d) / (width * width * 3));
        total += P[0].rgb * (trail + spark) * sin(p * 3.14159);
    }
    return float4(total * P[0].a, 0);
}
