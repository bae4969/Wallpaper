# 움직이는 배경 장면·위젯 작성 안내

이 문서는 사람과 AI가 **실행파일만 가지고** 장면(움직이는 배경)과 위젯을 만들고 확인하기 위한 안내서다. 위젯은 7절. `Wallpaper.exe --guide`로도 출력된다.

## 1. 한눈에 보기

- 장면(scene)은 **레이어 목록**이다. 위에 적은 레이어부터 차례로 그려 쌓는다.
- 레이어 종류(layer type)는 **셰이더(.hlsl) + 값 정의(.json)** 한 쌍이다. 기본 24종이 있고, 파일을 추가하면 새 종류가 생긴다.
- 모두 이 폴더에 있다: `Wallpaper.exe --folder` (보통 `%LOCALAPPDATA%\Wallpaper\Wallpapers`)

```
Wallpapers\
  AUTHORING.md               이 문서
  errors.log                 실행 중 장면 오류 기록
  layers\common.hlsli        모든 레이어 셰이더 앞에 붙는 공통 헤더
  layers\<종류>.hlsl         레이어 셰이더
  layers\<종류>.json         레이어 값 정의(slot·기본값·설명)
  scenes\<id>.json           장면(id = 파일 이름)
  widgets\<id>\widget.json   위젯 정의(이름·크기·갱신 간격·설정 값)
  widgets\<id>\widget.xaml   위젯 화면(WPF XAML)
```

- 기본 파일은 실행파일에 들어 있다. 없는 파일을 채우고, 고치지 않은 기본 파일만 새 버전으로 바꾼다(고친 파일은 덮어쓰지 않는다). 기본본으로 되돌리려면 그 파일을 지우고 앱을 다시 실행하거나 `--folder`를 실행한다.

## 2. 작업 순서 (명령줄)

```
Wallpaper.exe --help                         명령 요약
Wallpaper.exe --list                         장면·레이어 종류·값 목록
Wallpaper.exe --validate my-scene            컴파일 검사(실패하면 종료 코드 1, 오류에 레이어 번호·줄 번호)
Wallpaper.exe --render my-scene out.png --time 3 --size 1920x1080 [--quality high|medium|low|0~2]
                                                한 프레임을 PNG로 저장해 결과를 본다(위젯 제외)
Wallpaper.exe --render path\to\draft.json out.png   폴더 밖 파일도 바로 그려 볼 수 있다
Wallpaper.exe --render my-scene sub.png --part sub --size 1920x1200 --main 3440x1440 --at 796,1440
                                                서브 모니터 쪽 한 프레임(--at: 메인 왼쪽 위 기준 서브 위치, 기본은 메인 바로 아래 가운데)
Wallpaper.exe --render-set my-scene set.png --main 3440x1440 --sub 1920x1200 --at 796,1440 --scale 0.5
                                                메인·서브를 실제 배치대로 한 장에 그린다(이어짐·반사 확인)
Wallpaper.exe --render-set my-scene set.png --main 2560x1440 --screen 1920x1080@-1920,180 --screen 1920x1080@2560,180 --sub 1920x1080
                                                옆 모니터(--screen, 메인 왼쪽 위 기준 좌표)를 포함한 배치
```

- 출력은 UTF-8이다. 콘솔 인코딩이 다르면 `--out result.txt`를 붙여 파일로 받는다.
- PowerShell은 이 실행파일(창 프로그램)이 끝나기를 기다리지 않는다. `& .\Wallpaper.exe --validate --out r.txt | Out-Null`처럼 `| Out-Null`을 붙이거나 `Start-Process -Wait`로 실행한다. bash·cmd·파이썬 subprocess는 출력을 받을 때 기다린다.
- 시간에 따라 움직이므로 `--time`을 바꿔 여러 장 그려 보면 움직임을 확인할 수 있다.
- 트레이 앱이 실행 중이면 폴더의 파일을 저장할 때마다 그 장면을 쓰는 화면을 바로 다시 그린다. 오류가 나면 그 화면은 Windows 배경화면으로 표시되고 트레이 알림과 `errors.log`에 원인이 남는다.
- 장면 적용: 트레이 아이콘 → 설정 → **디스플레이** 탭의 **배경 세트**에서 고른다. 메인 모니터에는 `layers`, 나머지 모니터에는 `sub` 구성이 그려진다.

## 3. 장면 파일 `scenes\<id>.json`

```json
{
  "name": "보라 노을",
  "description": "천천히 흐르는 구름과 반딧불",
  "layers": [
    { "type": "gradient", "top": "#120A2A", "bottom": "#3B1E54", "glow": "#FF8A5B", "glowStrength": 0.4, "glowCenter": 0.8 },
    { "type": "fog", "seed": 2, "color": "#6B4A8C", "opacity": 0.35, "scale": 1.6, "flowX": 0.01 },
    { "type": "particles", "seed": 7, "count": 80, "shape": "glow", "colorA": "#FFE9A8", "minSize": 4, "maxSize": 10,
      "opacity": 0.8, "velocityX": 0.004, "velocityY": -0.01, "speedJitter": 0.6, "twinkle": 0.8, "top": 0.4, "bottom": 1.0 },
    { "type": "vignette", "strength": 0.5 }
  ],
  "sub": { "mode": "mirror", "add": [ { "type": "water", "reflectivity": 0.75, "ripple": 8 } ] }
}
```

- `name`(설정 창 배경 목록에 보이는 이름), `description`, `layers`(필수, 메인 모니터), `sub`(서브 모니터 구성 — 기본 장면은 모두 있다). `//` 주석과 마지막 쉼표를 허용한다.
- **장면은 메인·서브 2세트다.** `sub` 의 값:
  - `mode`: `mirror`(메인 풍경을 메인 바닥선 기준으로 뒤집어 비춘다 — 수면 반사), `continue`(메인 아래로 이어지는 한 장의 큰 그림), `screen`(서브 화면을 독립된 화면으로 따로 그린다)
  - `layers`: 서브용 레이어 목록. 생략하면 메인의 `layers` 를 그대로 쓴다.
  - `add`: 위 목록 뒤에 덧붙일 레이어(예: mirror 의 `water`).
  - `sub` 가 없으면 예전처럼 모든 화면에 같은 장면을 각자 따로 그린다.
- 메인 모니터는 21:9 울트라와이드와 16:9 모두에서 보기 좋아야 한다. 주인공(해·블랙홀·봉우리 등)은 가로 0.3~0.7 안에 두고, 울트라와이드에서 늘어나는 양옆은 하늘·바다·숲처럼 이어지는 배경으로 채운다. 화면 가운데에는 큰 시계 위젯이 놓이는 일이 많으니 너무 밝거나 복잡하지 않게 한다.
- 레이어 공통 값: `type`(필수, 레이어 종류 이름), `seed`(숫자 — 같은 종류를 여러 겹 쓸 때 배치를 바꾼다), `blend`(opaque|alpha|add — 종류의 기본값 대신), `comment`(메모).
- 그 밖의 값은 레이어 종류의 `params`에 있는 이름만 쓸 수 있다. 오타는 검사 단계에서 "알 수 없는 값"으로 걸린다. 생략한 값은 기본값을 쓴다.
- 값의 형태: 숫자, 색 `"#RRGGBB"` 또는 `"#RRGGBBAA"`, 숫자 배열 `[0.1, 0.2]`, 또는 `values`에 정의된 이름(예: `"shape": "streak"`).
- 크기·길이 단위는 **1080p 기준 픽셀**이며 캔버스(메인 화면) 높이에 비례해 맞춘다. 위치·높이는 캔버스 비율(0=위, 1=아래)이다 — 서브·옆 모니터는 같은 값으로 이어진다(4.1절).

## 4. 기본 레이어 종류

정확한 값 목록은 `--list`로 본다.

| 종류 | kind | 용도 |
|------|------|------|
| gradient | fullscreen | 하늘 그라데이션 + 지평선 빛 번짐. 보통 맨 아래 |
| skyline | fullscreen | 생성형 빌딩 실루엣 + 깜박이는 창문. seed·높이·폭을 바꿔 여러 겹 |
| particles | particles | 보케(bokeh)·빗줄기(streak)·빛 점(glow) 입자 |
| fog | fullscreen | 흐르는 안개 |
| flash | fullscreen | 불규칙한 번개 섬광 |
| glass | post | 빗방울 유리. 그때까지 그린 화면 전체를 굴절·흐림 처리. 뒤에 둔 레이어는 유리 앞에 선명 |
| vignette | fullscreen | 가장자리 어둡게 |
| aurora | fullscreen | 흔들리는 오로라 커튼 두 겹 |
| ridge | fullscreen | 생성형 산 능선 실루엣. seed·scale 을 바꿔 여러 겹 |
| water | post | 잔물결 수면. 그때까지 그린 화면을 일렁이게 하고 어둡게·색조를 입힘 |
| meteor | fullscreen | 드물게 떨어지는 별똥별 |
| blackhole | fullscreen | 블랙홀(광자 고리·빛이 휘어 감기는 강착원반) |
| nebula | fullscreen | 천천히 흐르는 색 성운과 어두운 먼지 띠 |
| sun | fullscreen | 해·달 원반과 후광·빛살 |
| clouds | fullscreen | 빛 방향에 따라 음영이 지는 뭉게구름. offsetX·flip 지원 |
| ocean | fullscreen | 수평선 아래 원근 바다와 햇빛 반사 길 |
| grass | fullscreen | 바람 물결이 지나가는 풀밭 |
| petals | particles | 흩날리는 꽃잎·눈송이·낙엽(회전·뒤집힘) |
| forest | fullscreen | 침엽수 숲 실루엣과 안개 |
| lightrays | fullscreen | 한 점에서 퍼지는 빛줄기(god rays) |
| fluid | fullscreen | 광택 있는 컬러 유체 |
| matrix | fullscreen | 디지털 코드 비 |
| contour | fullscreen | 천천히 변하는 지형 등고선 |
| linewaves | fullscreen | 원근 물결면 위의 가는 선(미니멀) |

### 4.1 캔버스 좌표 — 메인·서브 잇기

앱은 실제 모니터 배치(크기·위치)를 셰이더에 넘긴다. 레이어는 **캔버스** 좌표로 그린다: 메인 모니터에서 캔버스 = 메인 화면이고, 서브 모니터가 `continue` 면 캔버스에서 메인 아래(서브가 놓인 자리, `i.uv.y` > 1)를, `mirror` 면 메인 바닥선(`i.uv.y` = 1)을 기준으로 뒤집은 자리를 본다. 그래서 같은 레이어 값을 메인·서브에 그대로 쓰면 경계에서 무늬가 저절로 이어지고, 모니터 배치가 바뀌어도 다시 맞춰진다. 모든 화면은 같은 시계를 쓰므로 반사·이어짐도 같은 순간을 그린다.

- **화면 역할**은 메인과의 위치로 정한다. 메인과 높이 범위가 겹치는 **좌우 화면(옆 모니터)** 은 메인 `layers` 를 캔버스 좌우(`i.uv.x` < 0 또는 > 1)로 이어 그린다 — 하늘·바다·산·성운이 옆으로 이어지고, 해·블랙홀처럼 한 자리에 있는 주인공은 메인에만 남는다. 그 밖의 화면(아래, 대각선 아래 등)은 `sub` 구성을 그린다. 그래서 잡음 기반 레이어는 `i.uv.x` 가 0~1 밖이어도 자연스럽게 이어지게 만든다.
- 입자는 모든 화면을 합친 가로 범위(`Span`: x=왼쪽 끝, y=폭, 캔버스 픽셀)에 퍼지고, 입자 수는 그 폭 ÷ 캔버스 폭만큼 앱이 늘린다(메인 밀도 유지). 새 입자 레이어는 가로 위치를 `Span.x + 비율 * Span.y` 로, 화면 폭 단위 가로 속도에는 `SpanScale()` 을 곱한다.
- `continue` 서브에서 `i.uv.y` 는 1보다 크다(서브 화면 높이 ÷ 메인 높이만큼). 입자의 `top`/`bottom` 처럼 영역을 비율로 받는 값은 1보다 크게 주면 서브까지 채운다. `gradient` 는 0~1 밖에서 끝 색을 유지한다.
- `vignette`·`glass`·`water` 는 화면마다 따로(화면 좌표로) 그린다 — 가장자리 어둡게와 수면·유리 효과는 각 모니터 기준이다.
- 예전의 `offsetX`/`flip` 값(`aurora`·`ridge`·`skyline`·`clouds`)은 수동으로 맞추던 방식이라 2세트 장면에서는 쓰지 않는다.

## 5. 새 레이어 종류 만들기

`layers\<이름>.json`과 `layers\<이름>.hlsl`을 만들면 장면에서 `"type": "<이름>"`으로 쓸 수 있다.

### 5.1 값 정의 `layers\<이름>.json`

```json
{
  "description": "가로로 흐르는 오로라 띠",
  "kind": "fullscreen",
  "blend": "add",
  "params": {
    "color":  { "slot": "P0.rgb", "default": "#5CFFC8", "description": "띠 색" },
    "amount": { "slot": "P0.a",   "default": 0.6,       "description": "세기" },
    "speed":  { "slot": "P1.x",   "default": 0.05,      "description": "흐름 속도" },
    "height": { "slot": "P1.y",   "default": 0.35,      "description": "띠 중심 높이(0=위)" }
  }
}
```

- `kind`
  - `fullscreen`: 화면 전체를 한 번 그린다. 셰이더에 `PS`만 정의한다.
  - `particles`: 입자 하나당 사각형(정점 6개)을 `instances` 개 그린다. `VS`와 `PS`를 정의한다.
  - `post`: `fullscreen`과 같지만, 그때까지 그린 화면이 `Source`(흐림 단계 mip 포함)로 주어진다. 결과로 화면을 덮어쓴다. `Source` 는 `ScreenUV(i)` 로 읽고 픽셀 크기는 `Screen` 을 쓴다.
- `blend`: `opaque`(덮어쓰기), `alpha`(premultiplied 알파 합성), `add`(더하기)
- `slot`: 셰이더 상수 `P[0..7]`(float4 8개)의 성분. `P0`, `P0.x`, `P2.rgb`, `P3.yz`, `P5.a`처럼 쓴다(xyzw = rgba). 색을 `.rgb`에 넣으면 세 성분이 채워진다. `"instances"`는 입자 수다.
- `values`: 이름 → 숫자 대응(예: `{ "bokeh": 0, "streak": 1 }`). 장면에서 이름으로 쓴다.

### 5.2 셰이더 `layers\<이름>.hlsl`

`common.hlsli`가 앞에 붙은 채로 컴파일된다(Shader Model 5.0). 쓸 수 있는 것:

```hlsl
cbuffer Frame : register(b0) {
    float2 Resolution;   // 캔버스 픽셀 크기(메인 화면 — 4.1절)
    float  Time;         // 초
    float  Seed;         // 장면에 적은 seed
    float4 P[8];         // 값 정의의 slot
    float  Quality;      // 효과 품질(설정 창): 2=최고 1.5=높음 1=보통 0.5=낮음 0=최저
    float  Mirror;       // 1: 서브 mirror(캔버스를 메인 바닥선 기준으로 뒤집어 본다)
    float2 Screen;       // 실제로 그리는 화면 픽셀 크기
    float2 Origin;       // 이 화면의 왼쪽 위가 캔버스에서 놓인 위치
    float2 Span;         // 모든 화면을 합친 가로 범위(x=왼쪽 끝, y=폭). 입자용
};
Texture2D Source;        // post 레이어에서만: 그때까지 그린 화면(mip 있음)
SamplerState Linear;     // 선형·가장자리 고정
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; float4 color : COLOR0; };
// i.uv: 캔버스 좌표(메인 화면 0~1, 왼쪽 위 0,0 — continue 서브에서는 1보다 크다), Resolution: 캔버스 픽셀 크기
// i.pos.xy: 이 화면의 픽셀 좌표 — 레이어 좌표로 쓸 때는 CanvasPixel(i)를 쓴다(디더처럼 화면 그대로가 맞는 곳만 i.pos.xy)
float2 CanvasPixel(V i);                          // 캔버스 픽셀 좌표
float2 ScreenUV(V i);                             // 이 화면 0~1 좌표(post 레이어가 Source 를 읽을 때. 픽셀 크기는 Screen)
float SpanScale();                                // 캔버스 폭 ÷ 가로 범위 폭(옆 모니터가 없으면 1)
float Hash11(float n); float Hash21(float2 p);   // 0~1 난수
float Noise(float2 p); float Fbm(float2 p);       // 값 잡음, 5옥타브 잡음
float2 WrapCell(float2 i);                        // 정수 격자 번호를 -2048~2047 로 감는다(시간이 들어가는 Hash21 입력용)
float Dither(float2 pixel);                       // 0~1 디더(띠 줄이기)
float Unit();                                     // 캔버스 높이 / 1080 (픽셀 값에 곱한다)
float4 PixelToClip(float2 pixel);                 // 캔버스 픽셀 → 클립 좌표(입자 VS용)
static const float2 Corners[6];                   // 사각형 정점 6개(-1~1)
```

- **출력은 premultiplied alpha**다: `return float4(color * alpha, alpha);`
- fullscreen·post: `float4 PS(V i) : SV_Target { ... }`
- particles: `V VS(uint vid : SV_VertexID, uint instance : SV_InstanceID)`에서 `Corners[vid]`로 사각형을 만들고, `PixelToClip`으로 위치를 정한다. `o.local`에 모서리 좌표를, `o.color`에 색·알파를 넘겨 `PS`에서 모양을 만든다. 같은 `instance`·`Seed`면 같은 난수가 나오도록 `Hash11(instance + Seed * 101)`처럼 쓴다.
- 코드에는 ASCII만 쓴다(주석의 한글은 컴파일 전에 공백으로 바뀌므로 주석은 괜찮다).

예: 위 오로라 레이어의 셰이더

```hlsl
float4 PS(V i) : SV_Target
{
    float wave = sin(i.uv.x * 6 + Time * P[1].x * 20) * 0.05 + sin(i.uv.x * 13 - Time * P[1].x * 9) * 0.02;
    float band = exp(-pow((i.uv.y - P[1].y - wave) / 0.08, 2));
    float shimmer = 0.6 + 0.4 * Fbm(float2(i.uv.x * 8 + Time * P[1].x * 5, i.uv.y * 3));
    float alpha = saturate(band * shimmer * P[0].a);
    return float4(P[0].rgb * alpha, alpha);
}
```

## 6. 성능과 주의

- 사용자는 설정 창에서 프레임 제한(15~144 FPS 14단계/주사율), 렌더 해상도(100~25% 10단계 — 레이어를 낮은 해상도로 그려 확대, `Resolution`도 그 크기), 효과 품질(최고·높음·보통·낮음·최저 = `Quality` 2·1.5·1·0.5·0)을 고른다. 입자 수는 100·80·60·45·30%로 줄고 `Fbm` 반복은 5·4·4·3·3회가 되며, 최저에서는 `post` 레이어를 건너뛴다. 단계가 늘 수 있으므로 셰이더는 `Quality >= 1`처럼 범위로 비교한다. 무거운 셰이더는 `Quality`를 보고 반복 횟수 등을 줄인다. `--render --quality 1.5`처럼 미리 본다.
- 레이어 하나는 화면 전체 픽셀마다 셰이더를 실행한다. 반복문은 짧게(예: Fbm 5회) 유지한다. 레이어 10개 안팎, 입자 수천 개 정도는 가볍다.
- 최대화·전체 화면 창이 화면을 덮으면 그리지 않는다.
- 시간(`Time`)은 앱을 켠 뒤 계속 커진다(하루면 86400). `sin`·`frac` 등으로 주기를 만들고, 시간에 비례해 커지는 값을 `Hash21`에 그대로 넣지 않는다 — 수천을 넘으면 float 정밀도가 무너져 무늬가 뭉개진다. `Noise`·`Fbm`은 안에서 격자 번호를 감아 괜찮지만, `Hash21(cell + floor(Time * …))`처럼 직접 쓸 때는 `WrapCell(floor(Time * …))`로 감는다. `--time 100000`으로 그려 오래 켠 뒤의 모습도 확인한다.
- 오류 메시지의 줄 번호는 레이어 .hlsl 파일 기준이다.

## 7. 위젯 만들기

위젯은 바탕화면 위(움직이는 배경 위에도)에 놓이는 작은 화면이다. `widgets\<id>\` 폴더에 두 파일을 두면 설정 창 **위젯** 탭의 **위젯 추가 ▾** 메뉴에 나타난다. 놓은 위젯마다 **설정** 버튼으로 값을 바꾼다(설정 창은 `params`로 자동 생성). 이동·크기 조절·그림자는 다른 위젯과 같다. 앱이 실행 중이면 파일을 저장할 때 바로 다시 만든다.

```
Wallpaper.exe --list                                   위젯 목록과 params
Wallpaper.exe --validate                               위젯 XAML도 실제로 만들어 검사
Wallpaper.exe --render-widget memo out.png --option "text=안녕" --option size=60
                                                       위젯 한 장을 PNG로 저장(하드웨어 값은 실제 측정)
```

### 7.1 정의 `widget.json`

```json
{
  "name": "메모",
  "description": "짧은 글을 바탕화면에 띄운다.",
  "width": 520, "height": 140,     // 위젯 내용 크기(DIP). 그림자 여백은 바깥에 자동으로 붙는다
  "interval": 60,                  // 다시 그리는 간격(초, 0.1 이상). 시각 경계에 맞춘다(1이면 초가 바뀔 때)
  "metrics": false,                // true 면 이 PC의 CPU·RAM·GPU·네트워크 측정값을 받는다
  "params": {
    "text":  { "type": "text",   "label": "내용", "default": "오늘도 좋은 하루" },
    "color": { "type": "color",  "label": "글자 색", "default": "#F0F1F4" },
    "size":  { "type": "number", "label": "글자 크기", "default": 44, "min": 12, "max": 120 },
    "bold":  { "type": "bool",   "label": "굵게", "default": true, "description": "ⓘ 툴팁으로 보이는 설명" },
    "align": { "type": "choice", "label": "정렬", "default": "가운데",
               "choices": { "왼쪽": "Left", "가운데": "Center", "오른쪽": "Right" } }
  }
}
```

- `type`: `bool`(체크박스) · `choice`(콤보박스, `choices`는 `{ "보이는 이름": "XAML에 넘길 값" }` 또는 이름 배열) · `color`(`#RRGGBB`/`#AARRGGBB`) · `number`(`min`·`max`) · `text`
- XAML에는 형식에 맞는 값으로 들어간다: bool → `true/false`, number → 실수, color → `SolidColorBrush`(색 값 자체는 `ParamColors[이름]`), choice → 고른 항목의 값, text → 문자열.

### 7.2 화면 `widget.xaml`

```xml
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:w="clr-namespace:Wallpaper.Scripting;assembly=Wallpaper">
  <TextBlock Text="{Binding Params[text]}" Foreground="{Binding Params[color]}"
             FontSize="{Binding Params[size]}" TextAlignment="{Binding Params[align]}"/>
</Grid>
```

- 최상위 요소는 화면 요소(Grid·Border·StackPanel 등)이고 크기는 `width`×`height`로 맞춰진다. 배경을 칠하지 않으면 투명하다.
- **쓸 수 있는 데이터**(`DataContext`):

| 이름 | 내용 |
|------|------|
| `Now` | 현재 시각(DateTime) |
| `Params[이름]` / `ParamColors[이름]` | 설정 값 / 색 설정의 Color |
| `Cpu`, `CpuRatio` | CPU 사용률 %(0~100), 비율(0~1) — `metrics: true` 필요 |
| `Ram`, `RamRatio`, `RamUsedGB`, `RamTotalGB` | 메모리 |
| `Download`, `Upload`, `DownloadText`, `UploadText` | 네트워크 바이트/초, "1.2 MB/s" |
| `HasGpu`, `Gpu`, `Gpus[i]` | NVIDIA GPU. 각 GPU: `Name`, `Usage`, `UsageRatio`, `MemoryUsedGB`, `MemoryTotalGB`, `MemoryPercent`, `MemoryRatio`, `Temperature`, `Power` |

- **변환기**(`xmlns:w` 필요): `{x:Static w:Converters.Format}`(MultiBinding [값, 형식 문자열] → 문자열. 형식이 빈 문자열이면 아무것도 표시하지 않음), `Converters.Visible`·`Converters.Hidden`(bool → 보임/숨김), `Converters.Rate`(바이트/초 → "1.2 MB/s").
- 형식이 고정이면 `StringFormat`을 쓴다: `{Binding Cpu, StringFormat={}{0:0}%}`, `{Binding Now, StringFormat=HH:mm}`. 형식을 설정으로 고르게 하려면 `choice` 값에 형식 문자열을 넣고 `Converters.Format`에 넘긴다(기본 `digital-clock` 참고). 날짜·요일·오전/오후 표기는 Windows 지역 설정을 따른다.
- 막대는 0~1 비율을 `ScaleTransform ScaleX="{Binding CpuRatio}"`로 늘린다(기본 `system-meters` 참고). 넘칠 수 있는 내용은 `<Viewbox StretchDirection="DownOnly">`로 감싼다.
- **쓸 수 없는 것**(읽기 거부): `x:Class`, `x:Code`, `ObjectDataProvider`, `XmlDataProvider`, `WebBrowser`, `Frame`, 위의 `xmlns:w` 외의 `clr-namespace`, 인터넷 주소(`http://`, `https://`)를 값으로 쓰는 속성. 인터넷에서 받은 위젯 파일은 내용을 확인하고 넣는다.
- `interval` 마다 한 장씩 다시 그리므로 XAML 애니메이션(Storyboard)은 부드럽게 보이지 않는다. 움직임이 필요하면 `Now`로 계산하고 `interval`을 줄인다(0.1초 이상, 짧을수록 CPU를 더 쓴다).
- 모든 위젯의 설정 창 끝에는 공통 값 **배경 진하기**(0~100%, 기본 38)와 **그라데이션 범위**(0~128px, 기본 110)가 자동으로 붙는다. 위젯 뒤 어두운 그라데이션은 앱이 그리므로 XAML에서 따로 그리지 않아도 된다.
- 오류는 레이어처럼 트레이 알림과 `errors.log`에 남고 그 위젯만 빠진다. `--validate`는 XAML 줄·칸 번호를 알려 준다.

