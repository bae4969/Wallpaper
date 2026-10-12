// 위젯 합성용 내부 셰이더(사용자 편집 대상 아님). Wallpapers/layers/common.hlsli 내장본 뒤에 붙여 컴파일한다.
// 위젯: P0 (x, y, 폭, 높이) 픽셀 사각형. 텍스처는 화면 픽셀과 1:1이다.
V WidgetVS(uint vid : SV_VertexID)
{
    float2 corner = Corners[vid] * 0.5 + 0.5;
    V o;
    o.pos = PixelToClip(P[0].xy + corner * P[0].zw);
    o.uv = corner;
    o.local = 0;
    o.color = 1;
    return o;
}

float4 WidgetShadowPS(V i) : SV_Target
{
    float a = Source.Load(int3(int2(i.pos.xy - P[0].xy), 0)).r;
    return a.xxxx;
}

float4 WidgetMaskPS(V i) : SV_Target
{
    float a = Source.Load(int3(int2(i.pos.xy), 0)).r;
    a = floor(a * 255 + Dither(i.pos.xy)) / 255;
    return float4(0, 0, 0, a);
}

float4 WidgetPS(V i) : SV_Target
{
    return Source.Load(int3(int2(i.pos.xy - P[0].xy), 0));
}

// 렌더 해상도를 낮췄을 때 레이어 결과를 출력 크기로 확대한다.
float4 UpscalePS(V i) : SV_Target
{
    return Source.Sample(Linear, i.uv);
}
