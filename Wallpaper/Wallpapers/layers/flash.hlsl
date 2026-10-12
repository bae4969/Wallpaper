// flash(번개): P0 색(a=세기), P1.x 평균 간격 초
float4 PS(V i) : SV_Target
{
    float interval = max(P[1].x, 1);
    float index = floor(Time / interval);
    float local = Time - index * interval;
    float chance = Hash11(index + Seed * 31);
    float at = Hash11(index * 1.7 + Seed) * (interval - 1.5);
    float t = local - at;
    float strength = 0;
    if (chance > 0.45 && t > 0) strength = exp(-t * 7) + 0.6 * exp(-max(t - 0.18, 0) * 10) * step(0.18, t);
    float glow = strength * P[0].a * (0.55 + 0.45 * (1 - i.uv.y));
    return float4(P[0].rgb * glow, 0);
}
