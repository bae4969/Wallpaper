// 모든 레이어 셰이더 앞에 붙는 공통 헤더. 이 파일을 고치면 모든 레이어에 반영된다.
// 레이어 파일은 float4 PS(V i) : SV_Target 를 정의한다(입자 레이어는 V VS(uint vid : SV_VertexID, uint instance : SV_InstanceID)도).
// 색은 모두 premultiplied alpha로 출력한다. 값은 레이어 정의(.json)의 slot 에 따라 P[0..7]에 채워진다.
// 좌표는 "캔버스" 기준이다. 메인 모니터(또는 독립 화면)에서는 캔버스 = 그 화면이고, 서브 모니터가 continue/mirror 로
// 메인과 이어질 때는 캔버스 = 메인 화면이며 서브는 그 아래(Origin)를 보거나(continue) 메인 바닥선을 기준으로 뒤집어(mirror) 본다.
// 그래서 i.uv·Resolution·Unit()·PixelToClip 은 모두 캔버스 기준이고, 픽셀 좌표가 필요하면 i.pos.xy 대신 CanvasPixel(i)를 쓴다.
cbuffer Frame : register(b0)
{
    float2 Resolution;   // 캔버스 크기(픽셀)
    float Time;
    float Seed;
    float4 P[8];
    float Quality;   // 효과 품질: 2=최고 1.5=높음 1=보통 0.5=낮음 0=최저(설정 창). 무거운 반복을 줄이는 데 쓴다. 범위로 비교할 것.
    float Mirror;    // 1이면 캔버스를 메인 바닥선(y = Resolution.y) 기준으로 뒤집어 본다(서브 mirror)
    float2 Screen;   // 실제로 그리는 화면 크기(픽셀). post 레이어가 Source 를 읽을 때 쓴다
    float2 Origin;   // 이 화면의 왼쪽 위가 캔버스에서 놓인 위치(픽셀)
    float2 Span;     // 모든 화면을 합친 가로 범위(캔버스 픽셀): x=왼쪽 끝, y=폭. 입자가 옆 모니터까지 퍼지는 데 쓴다
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

// 화면 픽셀 → 캔버스 픽셀(mirror 면 위아래를 뒤집는다). 같은 식을 거꾸로 쓰면 캔버스 → 화면이다.
float2 ScreenToCanvas(float2 pixel)
{
    float2 p = pixel + Origin;
    if (Mirror > 0.5) p.y = 2 * Resolution.y - p.y;
    return p;
}

float2 CanvasToScreen(float2 pixel)
{
    if (Mirror > 0.5) pixel.y = 2 * Resolution.y - pixel.y;
    return pixel - Origin;
}

// 레이어에서 쓰는 픽셀 좌표(캔버스 기준). 디더처럼 화면 그대로가 맞는 곳은 i.pos.xy 를 쓴다.
float2 CanvasPixel(V i) { return ScreenToCanvas(i.pos.xy); }
// post 레이어가 Source(이 화면에 그린 결과)를 읽을 좌표
float2 ScreenUV(V i) { return i.pos.xy / Screen; }
// 캔버스 폭 ÷ 가로 범위 폭(옆 모니터가 없으면 1). 화면 폭 단위 속도를 가로 범위에 맞출 때 곱한다.
float SpanScale() { return Resolution.x / Span.y; }

V FullscreenVS(uint id : SV_VertexID)
{
    float2 uv = float2((id << 1) & 2, id & 2);
    V o;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = ScreenToCanvas(uv * Screen) / Resolution;   // 선형 변환이라 보간해도 정확하다
    o.local = 0;
    o.color = 1;
    return o;
}

static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };

// 캔버스 픽셀 → 클립 좌표(입자 VS용)
float4 PixelToClip(float2 pixel) { return float4(CanvasToScreen(pixel) / Screen * float2(2, -2) + float2(-1, 1), 0, 1); }
