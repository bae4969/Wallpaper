// 모든 레이어 셰이더 앞에 붙는 공통 헤더. 이 파일을 고치면 모든 레이어에 반영된다.
// 레이어 파일은 float4 PS(V i) : SV_Target 를 정의한다(입자 레이어는 V VS(uint vid : SV_VertexID, uint instance : SV_InstanceID)도).
// 색은 모두 premultiplied alpha로 출력한다. 값은 레이어 정의(.json)의 slot 에 따라 P[0..7]에 채워진다.
cbuffer Frame : register(b0)
{
    float2 Resolution;
    float Time;
    float Seed;
    float4 P[8];
    float Quality;   // 효과 품질: 0=낮음 1=보통 2=높음(설정 창). 무거운 반복을 줄이는 데 쓴다.
    float3 QualityPadding;
};

Texture2D Source : register(t0);
SamplerState Linear : register(s0);

struct V
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
    float2 local : TEXCOORD1;
    float4 color : COLOR0;
};

float Hash11(float n) { return frac(sin(n * 12.9898) * 43758.5453); }

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float Noise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3 - 2 * f);
    return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), u.x),
                lerp(Hash21(i + float2(0, 1)), Hash21(i + float2(1, 1)), u.x), u.y);
}

float Fbm(float2 p)
{
    float value = 0, amplitude = 0.5;
    int octaves = Quality >= 2 ? 5 : (Quality >= 1 ? 4 : 3);
    for (int i = 0; i < octaves; i++) { value += Noise(p) * amplitude; p *= 2.03; amplitude *= 0.5; }
    return value;
}

static const float Bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
float Dither(float2 pixel)
{
    int2 i = int2(pixel) & 3;
    return (Bayer[i.y * 4 + i.x] + 0.5) / 16.0;
}

float Unit() { return Resolution.y / 1080.0; }

V FullscreenVS(uint id : SV_VertexID)
{
    float2 uv = float2((id << 1) & 2, id & 2);
    V o;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = uv;
    o.local = 0;
    o.color = 1;
    return o;
}

static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };

float4 PixelToClip(float2 pixel) { return float4(pixel / Resolution * float2(2, -2) + float2(-1, 1), 0, 1); }
