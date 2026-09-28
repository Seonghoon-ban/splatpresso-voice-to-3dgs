# SplatPresso — Voice To 3DGS

말로 요청하면, 지금 보고 있는 장면에 어울리는 **3D Gaussian Splat 오브젝트**를 생성해서
알맞은 위치·크기·방향으로 씬에 배치하는 Unity(URP) 패키지입니다.

> "소파 옆 바닥에 캠핑 의자 하나 놔줘" → 약 1~2분 뒤, 그 자리에 씬의 조명과 스타일에 맞춘 의자 스플랫이 나타납니다.

- **API 키는 GenPresso 하나만 사용합니다** (음성 이해·판단·이미지 편집·3D 생성 모두 GenPresso 경유).
- 렌더링은 aras-p의 [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)을 **수정 없이** 사용하며, 처음 설치할 때 자동으로 함께 설치됩니다.
- 설치는 **git URL 한 줄**이면 됩니다.

영문 요약은 문서 맨 끝의 [Quick start (English)](#quick-start-english)에 있습니다.

## 목차

1. [동작 방식](#동작-방식)
2. [요구 사항](#요구-사항)
3. [설치](#설치)
4. [GenPresso API 키](#genpresso-api-키)
5. [씬 설정 / 데모 씬](#씬-설정--데모-씬)
6. [조작법](#조작법)
7. [모드](#모드)
8. [음성 에이전트](#음성-에이전트)
9. [모델 라우팅과 모델 경로 바꾸기](#모델-라우팅과-모델-경로-바꾸기)
10. [비용 (크레딧)](#비용-크레딧)
11. [세션 폴더와 리플레이](#세션-폴더와-리플레이)
12. [문제 해결](#문제-해결)
13. [스크립트 API](#스크립트-api)
14. [제한 사항](#제한-사항)
15. [검증 상태](#검증-상태-011)
16. [패키지 구조](#패키지-구조)
17. [크레딧 & 라이선스](#크레딧--라이선스)

---

## 동작 방식

```
 [Space 홀드: 말하기]  또는  [Enter: 텍스트 입력]
          │  음성(16 kHz WAV) + 지금 보고 있는 화면(JPEG)
          ▼
 GenPresso chat/completions  (google/gemini-3.5-flash-lite, 오디오 직접 입력)
          │  { transcript, reply, actions: [create | cancel] }
          ▼
 SplatPressoRoot.StartRun(PlacementRequest)        ← 요청마다 독립 실행, 여러 개 병렬 가능
          │
 ① Capture   URP 원샷 캡처: RGB + 미터 단위 depth + 카메라 포즈
 ② Decide    Gemini: 무엇을 / 어디에(bbox) / 크기 / 이미지 편집 프롬프트
          │
          ├─ Scene-aware (기본) ─────────────────────────────────────
          │   ③ Edit     nano-banana로 현재 화면에 물체를 자연스럽게 그려 넣음
          │   ③' Depth   depth-anything v2 로 편집 이미지의 상대 depth (병렬)
          │   ④ Verify   Gemini: 정말 추가됐는지 확인 + 정밀한 bbox
          │   ⑤ 객체별   SAM-3 분할 → 흰 배경·정면 이미지로 다듬기(enhance)
          │
          └─ Direct ───────────────────────────────────────────────
              ⑤ 객체별   사용자가 말한 설명만으로 오브젝트 이미지 생성 (text-to-image)
          │
 ⑥ 3D        Splat: TripoSplat → model.ply    |    Mesh(실험적): Rodin → model.glb
 ⑦ Place     캡처 depth + 객체 마스크 + 생성 depth → 월드 위치·크기·yaw → 씬에 스폰
```

- 배치할 위치가 정해지면 그 자리에 **반투명 홀로그램 박스**가 먼저 나타나 객체별 진행률을 보여주고,
  완성되면 실제 스플랫으로 바뀝니다.
- 배치는 **캡처 시점의 카메라 포즈**를 기준으로 계산하므로, 생성되는 동안 카메라를 움직여도 결과 위치는 바뀌지 않습니다.
- 모든 단계의 입력·출력은 세션 폴더에 저장되어 **임의의 단계부터 다시 실행**할 수 있습니다(이미 끝난 단계는 재과금 없음).
- 소요 시간 (실측, 객체 1개): 이미지 단계(판단·편집·검증·분할·다듬기) 약 45초 + TripoSplat.
  TripoSplat이 이미 워밍돼 있으면 수십 초(Direct 전체 38초), **콜드 스타트/대기열이면 수 분**(실측 6분 25초)이 걸릴 수 있습니다.
  그래서 TripoSplat 타임아웃 기본값은 600초입니다.

## 요구 사항

| 항목 | 내용 |
|---|---|
| Unity | **6000.0 이상** (0.1.x는 **6000.0.63f1 (URP 17.0.4)** 과 **6000.2.6f2 (URP 17.2)**, Windows D3D12에서 검증) |
| 렌더 파이프라인 | **URP 17 + Render Graph** (Compatibility Mode 꺼짐). Built-in 프로젝트는 Setup이 URP 에셋을 만들어 줍니다. |
| 그래픽 API | Windows **D3D12 / Vulkan**, macOS **Metal**. DX11·OpenGL에서는 스플랫이 렌더링되지 않습니다. |
| Gaussian Splatting 렌더러 | **1.1.0 이상, 2.0 미만** (1.1.0은 Render Graph를 지원하는 첫 버전; 호환이 확인되지 않은 2.x는 안전하게 비활성화). 없으면 부트스트랩이 고정 커밋을 설치합니다(아래). |
| Git | **2.14 이상이 PATH에 있어야** git URL 설치가 됩니다 (없으면 [OpenUPM 대안](#방법-b--git-없이-openupm--로컬-폴더)). |
| GenPresso | API 키 + 크레딧 잔액. **미디어 작업은 잔액 10크레딧 이상**일 때만 접수됩니다. |
| 마이크 | 선택 사항 — 없으면 Enter로 텍스트 요청을 입력할 수 있습니다. |

## 설치

### 방법 A — Git URL (권장)

Unity 에디터에서 `Window > Package Manager` → `+` → **Add package from git URL** →

```
https://github.com/Seonghoon-ban/splatpresso-voice-to-3dgs.git
```

버전을 고정하려면 뒤에 `#v0.1.1`처럼 태그를 붙입니다.

**설치하면 이렇게 진행됩니다.**

1. UPM이 레지스트리 의존 패키지를 자동으로 받습니다:
   URP, Newtonsoft Json, Burst, Collections, Mathematics (+ 내장 모듈 audio / imageconversion / unitywebrequest / imgui).
2. 이 시점에는 패키지 본체가 **아직 컴파일되지 않습니다.** 모든 본체 어셈블리가 "Gaussian Splatting과 URP가 설치돼 있을 때만"
   컴파일되도록 게이트되어 있어서, 렌더러가 없는 상태에서도 컴파일 에러가 나지 않습니다.
   (컴파일 에러가 하나라도 있으면 Unity가 새 코드를 로드하지 않아 설치 스크립트 자체가 실행되지 못하기 때문입니다.)
3. 작은 부트스트랩 스크립트가 aras-p의 렌더러(`org.nesnausk.gaussian-splatting`)가 없는 것을 감지하고 대화상자를 띄웁니다:
   **Install (git)** / **Not now** / **Don't ask again**.
4. **Install (git)** 을 누르면 아래 URL을 프로젝트 `Packages/manifest.json`에 추가하고 설치합니다.
   도메인 리로드가 한 번 일어난 뒤 `SplatPresso` 메뉴가 나타납니다.
   ```
   https://github.com/aras-p/UnityGaussianSplatting.git?path=/package#2c6fed37da67a217367261fcfcd3316d34c73e76
   ```
5. 이미 어떤 방식으로든(git, OpenUPM, 로컬 포크) Gaussian Splatting **1.1.0 이상**이 설치돼 있으면 **절대 건드리지 않습니다.**
   1.1.0 미만(예: 1.0.0, 0.9.1)은 Render Graph에서 스플랫을 그리지 못하므로 SplatPresso 본체가 컴파일되지 않고 비활성 상태로 남습니다.
   이때 부트스트랩이 콘솔에 오류와 교체할 manifest 줄을 출력하고, 대화상자에서 **동의할 때만** 고정 커밋으로 교체합니다.

- **커밋 고정 이유**: `2c6fed3`은 v1.1.1 이후의 upstream HEAD로, 스플랫과 배경의 합성(composite) 셰이더 수정이 들어 있습니다.
  배치·색감 캘리브레이션이 이 버전으로 이뤄졌습니다.
- **Don't ask again** 은 `UserSettings/SplatPresso.Bootstrap.asset`(사용자·프로젝트별, 보통 git 제외 폴더)에 저장됩니다.
  나중에 다시 설치하려면 **SplatPresso > Install or Repair Dependencies** 를 실행하세요. 이 메뉴는 Mesh 모드용 glTFast 설치도 제안합니다.
- **Git 관련 주의**
  - UPM은 시작할 때 한 번만 git 위치를 찾습니다. git을 새로 설치했다면 **Unity와 Unity Hub를 모두 재시작**하세요.
  - Windows에서 프로젝트 경로가 너무 깊으면 clone이 `Filename too long`으로 실패합니다.
    프로젝트를 짧은 경로(대략 150자 이하)로 옮기거나 아래 OpenUPM 방법을 쓰세요.

### 방법 B — Git 없이 (OpenUPM / 로컬 폴더)

- **렌더러**: git이 없으면 부트스트랩 대화상자가 **Install from OpenUPM** 을 제안합니다.
  프로젝트 manifest에 OpenUPM scoped registry를 추가하고 `org.nesnausk.gaussian-splatting` `1.1.1`을 설치합니다(tarball 다운로드, clone 없음).
  - OpenUPM의 1.1.1은 태그 v1.1.1이라 위의 합성 셰이더 수정이 빠져 있습니다. 동작에는 문제가 없고, 스플랫 가장자리의 블렌딩만 약간 다르게 보일 수 있습니다.
- **이 패키지 자체**: GitHub에서 ZIP으로 받아 압축을 푼 뒤
  - 폴더째 `Packages/com.splatpresso.voice-to-3dgs`에 넣거나(embedded package),
  - Package Manager → `+` → **Add package from disk…** 로 `package.json`을 선택합니다.

### 방법 C — manifest.json 직접 편집

`Packages/manifest.json`의 `dependencies`에 두 줄을 넣으면 대화상자 없이 설치됩니다:

```json
{
  "dependencies": {
    "com.splatpresso.voice-to-3dgs": "https://github.com/Seonghoon-ban/splatpresso-voice-to-3dgs.git",
    "org.nesnausk.gaussian-splatting": "https://github.com/aras-p/UnityGaussianSplatting.git?path=/package#2c6fed37da67a217367261fcfcd3316d34c73e76"
  }
}
```

OpenUPM으로 렌더러를 받는 경우:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["org.nesnausk.gaussian-splatting"]
    }
  ],
  "dependencies": {
    "com.splatpresso.voice-to-3dgs": "https://github.com/Seonghoon-ban/splatpresso-voice-to-3dgs.git",
    "org.nesnausk.gaussian-splatting": "1.1.1"
  }
}
```

### CI / 배치 모드

- 배치 모드에서는 대화상자를 띄우지 않고, 기본적으로 manifest도 **수정하지 않습니다**(필요한 manifest 줄을 에러 로그로 출력).
- 커맨드라인에 `-splatpressoInstallDeps`를 주면 렌더러를 설치하고, `-splatpressoExitWhenDone`을 함께 주면
  설치와 도메인 리로드가 끝난 뒤 종료 코드 0(성공)/1(실패)로 에디터를 종료합니다.
- 팀 프로젝트라면 설치 후 생긴 `Packages/manifest.json`과 `Packages/packages-lock.json`을 커밋하세요. 팀원은 clone만 하면 됩니다.

### 선택 패키지

| 패키지 | 용도 | 설치 방법 |
|---|---|---|
| glTFast (`com.unity.cloud.gltfast`) | Mesh 모드(Rodin GLB 런타임 로드) | **SplatPresso > Install or Repair Dependencies** 에서 제안 |
| Input System (`com.unity.inputsystem`) | 필수 아님 — 구 Input Manager / Input System / Both 모두 동작 | 의존성으로 선언하지 않습니다(설치 시 에디터 재시작 대화상자가 뜨기 때문) |

## GenPresso API 키

https://genpresso.ai/ko/developers 에서 발급합니다 (API 문서: https://genpresso.ai/ko/api).

- 키는 `gp_`로 시작하며 **발급 직후 한 번만 표시**되므로 그때 복사해 두세요.
- 텍스트(chat/completions)와 미디어(이미지 편집·분할·3D) 모두 이 키 하나로 호출합니다.
- **크레딧이 있어야 동작합니다.** 미디어 작업은 잔액이 10크레딧 이상이어야 접수되고(부족하면 402), 텍스트 호출은 1크레딧 이상이 필요합니다.
  실패한 요청은 과금되지 않습니다.

### 키 저장 위치 (권장: 사용자 프로필 파일)

**Project Settings > SplatPresso** 의 API Keys 섹션에서 키를 입력하고 **Save** 를 누르세요(사용자 프로필의 키 파일에 저장).
키는 프로젝트 **바깥**의 아래 파일에 저장되므로, 프로젝트를 압축해 공유하거나 커밋해도 키가 새지 않습니다.

```
Windows       %USERPROFILE%\.splatpresso\keys.json
macOS/Linux   ~/.splatpresso/keys.json
```

```json
{ "genpresso": "gp_...", "fal": "...", "openai": "sk-..." }
```

`genpresso` 외의 키는 선택입니다. 키 이름은 대소문자·구분자(`_`, `-`, 공백)를 무시하고 비교합니다.

키는 아래 순서로 찾고, **처음 발견된 값**을 사용합니다:

| 순서 | 위치 | 비고 |
|---|---|---|
| 1 | 코드에서 지정: `ApiKeys.SetOverride(ApiKeyKind.Genpresso, key)` | 자체 백엔드에서 키를 받아 오는 앱용 |
| 2 | 환경 변수 `GENPRESSO_API_KEY` / `FAL_KEY` / `OPENAI_API_KEY` | 에디터·빌드 모두 |
| 3 | `~/.splatpresso/keys.json` | **권장.** 같은 PC의 에디터와 빌드가 함께 사용 |
| 4 | 설정 에셋의 `apiKey` 필드 (GenPresso만) | 평문. **커밋되고 빌드에 포함됩니다** — 비권장 |
| 5 | `Assets/StreamingAssets/splatpresso.keys.json` | 빌드를 다른 사람에게 줄 때만. 빌드 안에 평문으로 들어갑니다 |

- Project Settings에는 각 키의 **실제로 사용 중인 출처와 마스킹된 값**(`gp_****abcd`)이 표시됩니다.
  예전에 설정한 환경 변수가 방금 입력한 키를 덮어쓰고 있지 않은지 여기서 확인하세요.
- 4·5번에 키가 있으면 **SplatPresso > Validate Project** 와 빌드 전 검사가 경고합니다. 로그에는 키 값이 절대 출력되지 않습니다.
- **Test Connection** 은 `GET /models`(연결·모델 확인)와 1토큰짜리 chat 핑(키·잔액·모델 확인)만 보내므로 비용이 사실상 없습니다.

## 씬 설정 / 데모 씬

1. **데모로 바로 시작**: **SplatPresso > Create Demo Scene**
   - `Assets/SplatPresso/Demo/SplatPressoDemo.unity`에 바닥·벽·테이블·소파 모양 블록과 조명이 있는 간단한 방을 만들고,
     눈높이 1.6 m 카메라(FirstPersonCamera)를 둔 뒤 아래 Setup Scene까지 실행합니다.
2. **내 씬에 추가**: 씬을 연 상태에서 **SplatPresso > Setup Scene…** — 체크박스로 항목을 고른 뒤 실행합니다.
3. **Project Settings > SplatPresso** 에서 GenPresso 키 저장 → **Test Connection**.
4. Setup이 그래픽 API를 바꿨다면 **에디터를 재시작**하세요(재시작 전까지 에디터는 이전 API로 동작).
5. Play → **Space를 누른 채** "테이블 위에 작은 선인장 화분 놔줘" → 손을 떼면 시작합니다.

**Setup Scene이 하는 일** (여러 번 실행해도 안전하며, 사용자가 이미 지정한 값은 덮어쓰지 않습니다. Undo 가능):

- URP가 활성 파이프라인인지 확인 — Built-in이면 `Assets/SplatPresso/Rendering/SplatPresso_URP.asset`(+ Renderer)을 만들어 모든 품질 레벨에 지정 (확인 후).
- Render Graph 켜기 (Compatibility Mode 끄기).
- **모든 URP 렌더러**(모든 품질 레벨)에 `GaussianSplatURPFeature` → `SplatCaptureFeature` 순서로 렌더러 기능 등록.
- 설정 에셋 `Assets/SplatPresso/Resources/SplatPressoSettings.asset` 생성 (이미 있으면 그대로 둠).
- Windows 빌드의 첫 그래픽 API가 D3D12/Vulkan이 아니면 D3D12를 맨 앞에 추가.
- 씬에 `SplatPresso` 오브젝트 생성: `SplatPressoRoot`, `CaptureService`, `ObjectSpawnService`, `PlacementPreviewService`,
  `VoiceAgent`, `MicCapture`, `AudioSource`, `AudioStreamPlayer`, `VoiceHud`.
- 카메라 확인(없으면 생성), 선택적으로 `FirstPersonCamera`(카메라 컨트롤러가 없을 때 기본 켜짐)와 `DebugHotkeys`(기본 꺼짐) 추가, AudioListener 확인.
- URP 에셋의 MSAA가 켜져 있으면 경고(캡처는 MSAA 꺼짐이 필요).
- 씬 저장은 사용자에게 묻고, 결과를 Created / Already present / Warnings로 로그에 출력.

**SplatPresso > Validate Project** 는 같은 검사를 읽기 전용으로 수행합니다(빌드 전에도 자동 실행).

## 조작법

| 입력 | 동작 |
|---|---|
| **Space (홀드)** | Push-to-talk. 누르는 동안 녹음(하단 HUD에 REC + 레벨 바), 떼면 현재 화면과 함께 전송. 마이크는 항상 예열돼 있고 0.35초 프리롤이 있어 첫 음절이 잘리지 않습니다. 키는 설정의 `pushToTalkKey` |
| **Enter** | 텍스트 입력창 열기 → 요청 입력 → Enter로 전송, Esc로 닫기. 입력창이 열려 있는 동안 Space·단축키는 무시됩니다 |
| **M** | 생성 모드 전환: Scene-aware ↔ Direct |
| **N** | 표현 방식 전환: Splat ↔ Mesh |
| **V** | 마이크 장치 선택 패널 (클릭 또는 1~9). 레벨 바로 어떤 장치가 목소리를 잡는지 확인. 선택은 저장됩니다 |

M/N 단축키는 `SplatPressoRoot.enableModeHotkeys`로 끌 수 있고, 현재 모드는 HUD의 `[M] Scene-aware | Direct`, `[N] Splat | Mesh` 칩에 표시됩니다.

**선택 컴포넌트 (Extras)**

| 컴포넌트 | 입력 | 동작 |
|---|---|---|
| `FirstPersonCamera` | 마우스 / WASD / Q·E / Shift / Esc / 좌클릭 | 커서 잠금 상태에서 마우스로 시점, WASD 수평 이동(눈높이 유지), Q/E 높이, Shift 가속, Esc 커서 해제, 좌클릭 재잠금(텍스트 입력창·마이크 패널이 열려 있으면 재잠금 안 함). Space는 말하기 전용이라 쓰지 않습니다 |
| `FlyCamera` | 우클릭 드래그 / WASD / Q·E / Shift / 휠 | 우클릭 중 시점 회전, 자유 비행, 휠로 속도 조절 |
| `DebugHotkeys` (옵트인) | **F5** | 음성 없이 고정 요청("캠핑 의자 1개")으로 전체 파이프라인 실행 |
| | **F6 / F7** | 가장 최근 세션을 Decide / ProcessObjects 단계부터 리플레이 |
| | **F8** | 실행 중인 모든 생성 취소 |
| | **F9** | 지정한 `.ply` 파일을 카메라 앞 2 m에 즉시 스폰(클라우드 호출 없음, 렌더러 점검용) |
| | **Tab** | 좌상단 디버그 오버레이 |
| `PlacementNudgeController` | ←→↑↓ / PgUp·PgDn / `[` `]` / `,` `.` | 마지막으로 스폰된 오브젝트를 0.05 m(Shift 0.25 m)씩 이동, 스케일 ×/÷1.05, yaw ±5°. 누적값이 로그에 찍혀 캘리브레이션에 사용 |
| `FocusFrameCap` (옵트인) | — | 창이 포커스를 잃으면 프레임레이트를 제한 (아래 문제 해결 참고) |

## 모드

두 가지 축이 있고, 서로 독립적으로 전환합니다.

**생성 모드 (M)**

| 모드 | 동작 | 특징 |
|---|---|---|
| **Scene-aware** (기본, `SceneContextual`) | 캡처한 화면을 편집해 물체를 그려 넣고 → 검증 → 분할 → 다듬기 → 3D | 씬의 조명·색감·스타일과 어울리는 오브젝트. 단계가 많아 느림(~60–150초) |
| **Direct** (`DirectTextTo3D`) | 캡처는 **위치를 정하는 데만** 사용. 오브젝트 이미지는 **사용자가 말한 설명만으로** 생성 → 3D | 빠르고 저렴(~30–90초). 의도적으로 씬 스타일을 반영하지 않음 |

**표현 방식 (N)**

| 방식 | 3D 모델 | 결과 |
|---|---|---|
| **Splat** (기본, `GaussianSplat`) | TripoSplat → `model.ply` (262,144 가우시안) | 런타임에 `GaussianSplatRenderer` 오브젝트로 스폰 |
| **Mesh** (실험적, `Mesh`) | Rodin v2.5 → `model.glb` (Scene-aware: 이미지→3D, Direct: 텍스트→3D) | glTFast로 런타임 로드. glTFast가 없으면 Mesh 요청은 **시작 즉시 실패**합니다(`RunFailed`, 단계 `Idle`, `Mesh mode needs glTFast…`). 미디어 크레딧은 쓰이지 않지만 아무것도 생성되지 않으므로, Mesh 모드를 쓰기 전에 glTFast를 설치하세요 |

- 이미 진행 중인 생성은 **시작 시점의 모드**를 유지합니다. 리플레이는 그 세션이 만들어진 모드를 따릅니다(`mode.txt`, `representation.txt`).
- 요청은 병렬로 처리됩니다: 동시 실행 최대 `maxConcurrentRuns`(기본 3), 요청당 객체 동시 처리 `maxConcurrentObjects`(기본 4),
  전체 미디어 작업 동시 실행 `maxConcurrentMediaJobs`(기본 6).
- 씬에 남는 생성물은 최대 `maxSpawnedObjects`(기본 24)개입니다. 넘치면 가장 오래된 **보이는** 오브젝트부터 제거합니다
  (비활성화해 둔 오브젝트는 자동 제거하지 않습니다). 262k 스플랫 하나가 대략 20~30 MB 메모리를 씁니다.

## 음성 에이전트

설정의 `voiceBackend`로 고릅니다.

**GenpressoChat (기본, GenPresso 키만 필요)**

- Space를 떼면 녹음(16 kHz WAV)과 현재 화면(`voiceFrameMaxLongSide` 768 px JPEG)을 GenPresso `chat/completions`에
  한 번에 보냅니다(`input_audio`). 모델은 정해진 JSON(`transcript`, `reply`, `actions`)으로만 답합니다.
- `create` 액션 → 생성 시작, `cancel` 액션 → 진행 중인 모든 생성 취소. 응답을 해석하지 못하면 사과 메시지만 보이고 **행동을 추측하지 않습니다**.
- 답변은 **텍스트**(HUD 자막 말풍선)입니다. 현재 GenPresso API에는 음성 합성(TTS) 엔드포인트가 없습니다.
- 대화 기록은 최근 `voiceHistoryTurns`(기본 6)턴만 유지하고, 지난 턴의 오디오·이미지는 텍스트로 바꿔 보냅니다.
- 진행 상황 중 **중요한 순간**(완료, 건너뜀, 불가능 판정)만 에이전트에게 전달되어 자연어로 알려줍니다(`narrationMode`: `Llm` / `Subtitle` / `Off`).
- 한 번의 발화는 최대 `maxUtteranceSeconds`(기본 60초, 최대 85초). GenPresso 요청 본문 상한 4 MB 때문입니다.
- 답변 언어: `replyLanguage` = `Auto`(사용자가 말한 언어로, 불확실하면 `fallbackLanguage`) 또는 특정 언어.
  생성 요청 필드는 언어와 상관없이 항상 영어로 만들어집니다. `customInstructions`로 지시를 덧붙일 수 있습니다.

**OpenAIRealtime (선택, OpenAI 키 필요)**

- `OPENAI_API_KEY`가 있으면 OpenAI Realtime(`realtimeModel` 기본 `gpt-realtime-2.1`, 목소리 `cedar`)으로 **음성 답변**을 들을 수 있습니다.
  키가 없으면 경고와 함께 GenpressoChat으로 대체됩니다. WebGL에서는 사용할 수 없습니다.
- `useSemanticVad`는 기본 꺼짐(push-to-talk). 켜면 말을 자동 감지하지만 스피커 에코에 반응할 수 있으니 헤드폰에서만 쓰세요.

`None`으로 두면 음성 에이전트를 끄고 스크립트 API로만 사용합니다.

## 모델 라우팅과 모델 경로 바꾸기

모든 미디어 단계는 **Project Settings > SplatPresso > Media models** 의 `ModelRoute` 하나로 정의됩니다.
각 라우트는 GenPresso 후보 경로 목록(`genpressoPaths`, 앞에서부터 시도), fal.ai 대체 엔드포인트(`falEndpoint`),
예상 비용(`estimatedCost`, 크레딧), 타임아웃(`timeoutSec`)을 가집니다.

| 단계 | 설정 필드 | GenPresso 후보 (순서대로) | fal 대체 | 예상 크레딧 | 타임아웃 |
|---|---|---|---|---|---|
| 화면 편집 | `edit` | `google/nano-banana-2-lite/edit` → `gp/nano-banana-2/edit` | `google/nano-banana-2-lite/edit` | 1.3 | 120 s |
| 편집 폴백 | `editFallback` | `gp/nano-banana-2/edit` → `gp/nano-banana-pro/edit` | `fal-ai/nano-banana-2/edit` | 1.3 | 150 s |
| 객체 이미지 다듬기 | `enhance` | `google/nano-banana-2-lite/edit` → `gp/nano-banana-2/edit` | `google/nano-banana-2-lite/edit` | 1.3 | 120 s |
| 텍스트→이미지 (Direct) | `textToImage` | `google/nano-banana-2-lite` → `gp/nano-banana-2` → `gp/nano-banana-pro` | `google/nano-banana-2-lite` | 1.3 | 120 s |
| 객체 분할 | `segment` | `gp/sam-3/image` → `gp/sam-3-1/image` | `fal-ai/sam-3/image` | 0.2 | 60 s |
| 배경 제거 (분할 실패 시) | `removeBackground` | `gp/birefnet/v2` → `gp/birefnet` | `fal-ai/birefnet/v2` | 0.1 | 60 s |
| 상대 depth | `depth` | `gp/image-preprocessors/depth-anything/v2` | `fal-ai/image-preprocessors/depth-anything/v2` | 0.2 | 60 s |
| 이미지→스플랫 | `imageToSplat` | `tripo3d/triposplat` → `gp/triposplat` | `tripo3d/triposplat` | 1.5 | 600 s |
| 이미지→메시 | `imageToMesh` | `gp/hyper3d/rodin/v2.5/fast` → `gp/hyper3d/rodin/v2.5` | `fal-ai/hyper3d/rodin/v2.5/fast` | 3.0 | 600 s |
| 텍스트→메시 | `textToMesh` | `gp/hyper3d/rodin/v2.5/text-to-3d/fast` | `fal-ai/hyper3d/rodin/v2.5/text-to-3d/fast` | 3.0 | 600 s |

언어 모델(판단·검증·음성 턴): `chatModel` = `google/gemini-3.5-flash-lite`, 모델 오류 시 한 번 `chatFallbackModel` = `google/gemini-3.8-flash`.

**GenPresso 경로 이름 규칙 (`gp/`)**

- GenPresso는 fal.ai 모델을 재호스팅합니다. fal의 `fal-ai/x` 모델은 GenPresso에서 **`gp/x`** 입니다.
  예: `fal-ai/hyper3d/rodin/v2.5/text-to-3d/fast` → `gp/hyper3d/rodin/v2.5/text-to-3d/fast`.
- `fal-ai/`가 아닌 다른 소유자의 모델(`google/…`, `tripo3d/…`, `bytedance/…`, `openai/…`)은 **이름 그대로** 씁니다.
  예: TripoSplat은 `tripo3d/triposplat`.
- GenPresso에 `fal-ai/…`를 그대로 보내면 404입니다.
- **실제 GenPresso에서 확인한 경로 (2026-09-28)**: 위 표의 모든 기본 후보가 존재합니다. TripoSplat은 `tripo3d/triposplat`(별칭 `gp/triposplat`)이며,
  규칙대로라면 나올 법한 `gp/tripo3d/triposplat`은 **존재하지 않습니다**. TripoSplat은 큐 대기가 몇 분 걸릴 수 있어 타임아웃을 600초로 둡니다.

**자동 해석과 캐시**

- GenPresso 미디어 카탈로그는 키 없이는 조회할 수 없어서, 일부 경로(TripoSplat 등)는 위 규칙으로 도출한 **후보**입니다.
  그래서 경로를 하나로 고정하지 않고 후보 목록을 순서대로 시도합니다.
- GenPresso는 `gp/…` 경로를 **제출 시점에는 거의 다 받아들이고**, 잘못된 경로나 잘못된 입력은 작업이 실행된 뒤에야
  `FAILED`로 알려줍니다(결과 조회 시 404 `Path … not found` 또는 422 검증 오류). 그래서 두 경우 모두 처리합니다:
  - 제출 시 404(예: `Application "x" not found`, `unknown model`) → 다음 후보.
  - 작업 후 `FAILED` + 404 경로 없음 → 다음 후보 (실패한 작업은 과금되지 않으므로 원장의 예상 비용도 되돌립니다).
  - 작업 후 `FAILED` + 422 → 모델은 존재하고 입력만 잘못된 것이므로 재제출하지 않고 오류로 보고합니다.
- **실제로 동작이 확인된 경로만** `<persistentDataPath>/SplatPresso/model_paths.json`에 7일간 캐시되어 다음부터 바로 사용됩니다.
- **Test + Probe Media Models** 버튼(Project Settings)은 모든 후보에 일부러 검증에 실패하는 요청을 보내고, 작업이 끝날 때까지
  (TripoSplat은 대기열 때문에 몇 분 걸릴 수 있음) 기다린 뒤 결과로 존재 여부를 판단합니다. 모든 후보를 동시에 확인합니다.
  검증 실패 요청은 과금되지 않지만, 잔액 10크레딧 이상이 필요합니다.
- 경로를 바꾸려면 해당 라우트의 `genpressoPaths` 목록을 수정하세요. 원하는 경로를 맨 앞에 두면 됩니다.

**fal.ai 대체 경로**

- `falFallbackWhenMissing`(기본 켜짐) + fal 키(`FAL_KEY`)가 있으면, GenPresso 후보가 **전부** 없을 때 그 단계만
  fal.ai 큐(`https://queue.fal.run`)에서 `falEndpoint`로 실행합니다.
- `mediaProvider = FalDirect`로 두면 모든 미디어 단계를 fal.ai에서 실행합니다(텍스트는 계속 GenPresso). fal 요금은 fal 계정에 청구됩니다.

## 비용 (크레딧)

GenPresso는 **작업이 끝난 뒤 실제 사용량으로** 크레딧을 차감합니다. 패키지의 비용 원장(`ledger.json`)은
위 표의 `estimatedCost`로 계산한 **추정치**이며, 목적은 정확한 정산이 아니라 **재시도 폭주를 막는 것**입니다.
추정치는 설정에서 바꿀 수 있습니다.

| 요청 (객체 1개) | 대략의 크레딧 | 구성 |
|---|---|---|
| Scene-aware · Splat | **약 4–6** | 편집 1.3 + depth 0.2 + 분할 0.2 + 다듬기 1.3 + TripoSplat 1.5 + 판단/검증 소액 (재시도 시 증가) |
| 객체 1개 추가될 때마다 | 약 +3 | 분할 + 다듬기 + TripoSplat |
| Direct · Splat | 약 3 | 텍스트→이미지 1.3 + TripoSplat 1.5 + 판단 소액 |
| Scene-aware · Mesh | 약 6–8 | TripoSplat 대신 Rodin 3 |
| Direct · Mesh | 약 3 | 텍스트→메시 3 |
| 음성 턴 1회 | 매우 적음 | 텍스트 호출 (잔액 1크레딧 이상 필요) |

- **런당 상한** `maxCostPerRun` = 25크레딧(추정). 상한에 닿으면 새 미디어 호출을 멈춥니다. 객체 처리 중이면 그 객체만 건너뛰고, 그 외 단계면 런이 실패합니다.
  리플레이는 원장을 새로 시작합니다(파일에는 이력이 누적).
- `enhanceObjectImages`를 끄면 객체당 약 1.3크레딧을 아낄 수 있지만 3D 품질이 눈에 띄게 떨어집니다.
- **SplatPresso > Debug Window** 에서 런별 비용과 원장 항목을 볼 수 있습니다.

## 세션 폴더와 리플레이

모든 런은 세션 폴더 하나에 기록됩니다 (**SplatPresso > Open Sessions Folder**):

```
<persistentDataPath>/SplatPresso/sessions/<yyyyMMdd_HHmmss>/     (설정 sessionsFolder로 변경 가능)
  capture.jpg  capture_depth.bin  capture_meta.json   캡처 (RGB, float32 eye depth, 카메라)
  request.json  mode.txt  representation.txt           요청과 모드
  decision.json                                        Decide 결과
  edited.jpg  edited_url.txt  verification.json        Edit / Verify (Scene-aware)
  depth_gen.png  depth_gen_url.txt                     편집 이미지의 상대 depth
  objects/<id>/cutout.png  enhanced.png  generated.png (+ *_url.txt)
  objects/<id>/model.ply | model.glb  object.json
  result.json  ledger.json
```

- **SplatPresso > Debug Window** (플레이 모드): 세션 선택, 요청 편집·실행, **Decide / Edit / Verify / Objects / Place** 단계부터 리플레이,
  단계별 이미지 미리보기, 원장 표, 세션 삭제, 텍스트 요청 입력, 모드 전환.
- 리플레이는 이전 단계의 결과를 다시 읽으므로 그 단계들은 재과금되지 않습니다. Verify 이전부터 리플레이하면 이전 객체 캐시는 폐기됩니다.
- 저장된 호스팅 URL이 만료됐으면 로컬에 저장된 파일로 한 번 더 시도합니다.

## 문제 해결

| 증상 | 원인 | 해결 |
|---|---|---|
| `Capture timed out: is SplatCaptureFeature on the URP renderer…` / 첫 단계에서 멈춤 | 카메라가 쓰는 URP 렌더러에 캡처 기능이 없음 | **SplatPresso > Setup Scene** 실행(모든 렌더러에 등록). **Validate Project** 로 확인 |
| 스플랫이 전혀 안 보임 | DX11/OpenGL, 또는 렌더러에 `GaussianSplatURPFeature` 없음 | Setup Scene → 에디터 재시작. Player Settings > Windows의 그래픽 API 첫 항목이 Direct3D12(또는 Vulkan)인지 확인 |
| 렌더링·캡처가 모두 안 됨 | Render Graph Compatibility Mode가 켜져 있음 | Setup Scene이 끕니다. 수동: Project Settings > Graphics > URP > Render Graph의 Compatibility Mode 해제 |
| 캡처 실패 "MSAA" | URP 에셋의 MSAA가 켜져 있음 | URP 에셋 Quality > Anti Aliasing(MSAA)를 Disabled로 |
| `capture.jpg`가 상하 반전 | 그래픽 API별 readback 방향 | `SplatCaptureFeature.FlipReadbackOverride`를 `true`/`false`로 지정 (아래 코드) |
| 오브젝트가 눕거나 반대를 보거나 크기가 어긋남 | 3D 모델의 좌표 관례 차이 | `PlacementNudgeController`로 맞춘 뒤 로그의 `Nudge cumulative …` 값을 설정 `placement`에 반영: yaw 누적값을 `yawOffsetDeg`(기본 270)에 더하고, scale 배율을 `uniformScaleFactor`(기본 0.9)에 곱합니다. Mesh는 `meshYawOffsetDeg` / `meshUniformScaleFactor` |
| `No GenPresso media path for 'imageToSplat' is available (tried: …)` | GenPresso에서 해당 경로를 찾지 못함 | **Test + Probe Media Models** 로 존재하는 경로 확인 → `imageToSplat.genpressoPaths` 맨 앞에 지정. 또는 `FAL_KEY`를 설정해 fal 대체 경로 사용, 또는 N으로 Mesh 모드 |
| `GenPresso API key is missing…` / HUD의 키 누락 배너 | 키를 찾지 못함 | Project Settings > SplatPresso에서 저장하거나 `GENPRESSO_API_KEY` 설정. 환경 변수는 에디터를 다시 켜야 반영됩니다 |
| 402 `GenPresso balance too low` | 잔액 부족 (미디어는 10크레딧 이상 필요) | GenPresso에서 크레딧 충전 |
| 401 / 403 | 키가 틀렸거나 다른 키가 우선 사용 중 | Project Settings > SplatPresso에서 실제 사용 중인 출처 확인(오래된 환경 변수 주의) |
| 413 Payload too large | 요청 본문 4 MB 초과 | `maxUtteranceSeconds` 또는 `maxImageLongSide`를 줄이기 |
| `Too many generations running` | 동시 실행 한도 | 잠시 후 다시 요청하거나 `maxConcurrentRuns` 조정 |
| 새로 만들면 예전 오브젝트가 사라짐 | 스폰 상한 초과 | `maxSpawnedObjects` (기본 24) |
| `SplatPresso needs Gaussian Splatting >= 1.1.0` / `SplatPresso` 메뉴에 Install or Repair Dependencies만 있음 | 1.1.0 미만 렌더러가 이미 설치됨 (Render Graph 미지원) | manifest의 렌더러 줄을 위의 고정 커밋 URL로 (또는 **SplatPresso > Install or Repair Dependencies** 대화상자에서 교체 동의) |
| `GaussianSplatting version mismatch: … m_GpuView …` | 고정 버전과 다른 렌더러 설치 | manifest의 렌더러 줄을 위의 고정 커밋 URL로 |
| `Mesh mode needs glTFast` | glTFast 미설치 | **SplatPresso > Install or Repair Dependencies** |
| 빌드에서 메시가 분홍색 | glTFast 셰이더가 빌드에 없음 | glTFast 셰이더 그래프를 Always Included Shaders 또는 Shader Variant Collection에 추가 |
| 설치 시 `No 'git' executable was found` | git 없음 / PATH에 없음 | Git 2.14+ 설치 후 **Unity와 Unity Hub 모두 재시작**, 또는 OpenUPM |
| 설치 시 `Filename too long` | Windows 경로 길이 | 프로젝트를 짧은 경로로 옮기거나 OpenUPM |
| 마이크가 반응 없음 | 다른 장치가 선택됨 / OS 권한 | V로 장치 선택(레벨 바 확인). macOS는 Player Settings의 Microphone Usage Description 필요. `debugSaveMicWav`를 켜면 `<persistentDataPath>/SplatPresso/mic_last.wav`로 마지막 녹음 확인 |
| 창을 가려 둔 빌드가 몇 분 뒤 멈춤 | D3D12 창이 포커스를 잃으면 수백 fps로 돌 수 있음 | `FocusFrameCap` 컴포넌트 추가 또는 `capFrameRateWhenUnfocused` 켜기 |

**캡처 상하 반전 보정 예시** — 정적 값은 플레이 시작 시 초기화되므로 씬 로드 후에 지정합니다:

```csharp
using UnityEngine;
using SplatPresso.Rendering;

static class CaptureFlipFix
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Apply() => SplatCaptureFeature.FlipReadbackOverride = true; // 반대면 false. null = 자동
}
```

## 스크립트 API

```csharp
using System.Collections.Generic;
using UnityEngine;
using SplatPresso;

public class AddChairExample : MonoBehaviour
{
    void Start()
    {
        var root = FindFirstObjectByType<SplatPressoRoot>();

        root.RunCompleted += r => Debug.Log($"{r.runId}: {r.objects.Count} object(s)");
        root.RunFailed += f => Debug.LogWarning($"{f.runId} {f.kind} at {f.stage}: {f.reason}");
        root.RequestRejected += (req, why) => Debug.Log($"rejected: {why}");

        var request = new PlacementRequest
        {
            intentSummary = "Add a single camping chair on the ground in an empty area of the view.",
            objects = new List<RequestedObject>
            {
                new RequestedObject { name = "camping chair",
                                      description = "a folding camping chair, dark red fabric, black metal frame, about 0.8 m tall",
                                      count = 1 }
            },
            placementHint = "on the ground in an empty area"
        };

        root.Mode = GenerationMode.SceneContextual;           // 또는 DirectTextTo3D
        root.Representation = ObjectRepresentation.GaussianSplat;
        string runId = root.StartRun(request);                 // 거절되면 null (RequestRejected 이벤트)

        // 자연어 요청은 음성 에이전트의 LLM 턴을 거칩니다 (말하는 것과 동일).
        root.SubmitText("put a small potted cactus on the table");
    }
}
```

`PlacementRequest`의 필드는 영어로 작성하세요(Decide/Edit 모델이 영어 프롬프트를 기대합니다).

| `SplatPressoRoot` 멤버 | 설명 |
|---|---|
| `string StartRun(PlacementRequest, string sourceUtterance = null)` | 런 시작(비동기, 즉시 반환). runId = 세션 폴더 이름. 거절 시 null |
| `Awaitable<PlacementResult> RunAsync(PlacementRequest, string sourceUtterance, CancellationToken)` | await 가능한 버전. 실패 사유는 `RunFailed`로 전달 |
| `void SubmitText(string)` | 텍스트를 음성 에이전트 턴으로 처리 |
| `void StartReplay(string sessionDir, StartStage from)` | 저장된 세션을 특정 단계부터 재실행 |
| `void Cancel(string runId)` / `void CancelAll()` | 취소 (진행 중인 원격 작업에도 취소 요청을 보냄) |
| `void Narrate(string)` | 에이전트가 사용자에게 전할 메시지 주입 |
| `GenerationMode Mode` / `ObjectRepresentation Representation` | 이후 시작되는 런의 모드 (`ModeChanged` 등 변경 이벤트 제공) |
| `Func<PlacementRequest, bool> RequestGate` | false를 반환하면 요청을 버림(예: 메뉴가 열려 있는 동안). `RequestRejected(…, Gated)` 발생 |
| `int ActiveRunCount`, `PlacementOrchestrator LatestOrchestrator` | 상태 조회 |
| 이벤트 | `RunStarted(RunStartedInfo)`, `RunProgress(PlacementProgress)`, `ObjectUpdated(PlacedObjectResult, string subStage)`, `RunRetry(RetryInfo)`, `RunCompleted(PlacementResult)`, `RunFailed(PlacementFailure)`, `RequestRejected(PlacementRequest, RequestRejectReason)` |

- 모든 이벤트는 메인 스레드에서 호출되고 `runId`를 담고 있습니다. `PlacementFailure.kind`는
  `Cancelled / CostCapExceeded / StageFailed / Error` 중 하나이고, `stage`는 실패한 단계입니다.
- 객체별 하위 단계 문자열: `segmenting`, `cutout`, `enhancing`, `enhanced`, `t2i`, `generating3d`, `downloading`, `ready`, `skipped`.
- 기타:
  - `ObjectSpawnService` (`SplatPresso.Placement`): `Spawned` / `Removed` 이벤트, `SpawnedObjects`, `LastSpawned`,
    `SpawnSplatFromFileAsync(plyPath, position, rotation, uniformScale, label)`, `Remove`, `ClearAll`.
    생성물마다 `GeneratedObject` 컴포넌트(`label`, `runId`, `objectId`, `representation`, `modelPath`, `sessionDir`)가 붙습니다.
  - `VoiceAgent` (`SplatPresso.Voice`): `BeginTalk()` / `EndTalk()` 또는 `ExternalTalkHeld`로 키보드 대신 XR 컨트롤러 등으로 말하기,
    `UserTranscript` / `AgentReply` 이벤트, `SetMicDevice`.
  - `ApiKeys` (`SplatPresso`): `SetOverride`, `Get(kind, out source)`, `SaveToUserProfile`, `Mask`.

## 제한 사항

- **URP 전용**입니다(HDRP·Built-in 미지원). Render Graph가 켜져 있어야 합니다.
- 지원 그래픽 API: Windows D3D12/Vulkan, macOS Metal. Linux(Vulkan)는 테스트하지 않았습니다. 모바일은 테스트하지 않았습니다.
- **WebGL 미지원** (마이크, WebSocket, 스플랫 렌더러 compute 요구 사항).
- HUD는 IMGUI라 **XR 헤드셋에서는 보이지 않습니다.** 캡처는 모노 카메라를 전제로 하므로 XR에서는 별도의 모노 카메라를
  `CaptureService.targetCamera`로 지정하고, 말하기는 `VoiceAgent.BeginTalk/EndTalk`로 연결하세요.
- 캡처는 후처리(톤매핑·컬러그레이딩) **이전** 이미지이며, 화면 공간 UI는 포함되지 않습니다. MSAA는 꺼져 있어야 합니다.
- **Mesh 모드는 실험적**입니다. glTFast가 필요하고, 플레이어 빌드에는 glTFast 셰이더를 포함해야 합니다.
- GenPresso 모델 경로 일부는 명명 규칙에서 도출한 후보입니다([모델 라우팅](#모델-라우팅과-모델-경로-바꾸기) 참고).
- GenpressoChat 백엔드의 답변은 텍스트(자막)입니다. 음성으로 듣으려면 OpenAI Realtime 백엔드가 필요합니다.
- 세션 JSON의 파일 경로는 절대 경로라 세션 폴더를 다른 위치로 옮기면 리플레이가 안 될 수 있습니다.
- 배치 캘리브레이션(`yawOffsetDeg` 270, `uniformScaleFactor` 0.9)은 TripoSplat 출력 기준입니다. 다른 3D 모델로 바꾸면 다시 맞춰야 합니다.

이 패키지는 aras-p의 렌더러를 **수정하지 않습니다.**
depth 캡처는 자체 URP 렌더 패스와 셰이더로 하며, 렌더러 내부 필드 하나(`GaussianSplatRenderer.m_GpuView`, 스플랫별 화면 데이터)만
작은 리플렉션 브리지(`GsInternals`)로 읽습니다. 그래서 새로 설치할 때는 렌더러를 커밋으로 고정합니다.
이미 설치된 렌더러가 1.1.0 미만이면 SplatPresso 본체는 컴파일되지 않고, 부트스트랩이 콘솔에 `SplatPresso needs Gaussian Splatting >= 1.1.0` 오류를 냅니다.
1.1.0 이상인데 이 필드가 없으면(내부 구조가 바뀐 버전·포크) 시작 시와 **Validate Project** 에서 `GaussianSplatting version mismatch` 오류를 냅니다.
런타임 스플랫 에셋은 렌더러의 공개 API(`GaussianSplatAsset`)로 만듭니다.

## 검증 상태 (0.1.1)

| 항목 | 결과 |
|---|---|
| 빈 프로젝트에 git URL 한 줄 설치 → 부트스트랩이 렌더러 자동 설치 → 컴파일 | 6000.0.63f1, 6000.2.6f2 모두 통과 (에러·경고 0) |
| EditMode 테스트 (Bbox, 배치 수학, PLY→런타임 에셋, upstream 임포터와 바이트 단위 레이아웃 비교, 키 해석, 에러 파싱 등) | 96/96 통과 |
| PlayMode 테스트 (실제 GPU 렌더 + RGB/depth 캡처, 실제 GenPresso 동작을 재현한 모의 서버로 Scene-aware·Direct·Mesh 전체 파이프라인, 음성 텍스트 턴) | 20/20 통과 (Input System 전용 프로젝트 포함) |
| Windows 플레이어 빌드 (Mono, Managed Stripping High) | 런타임 에셋·리플렉션 브리지·캡처·전체 파이프라인(모의 서버) 정상 |
| 실제 GenPresso API (0.1.1) | **통과** — 플레이어 빌드(Stripping High)에서 한국어 요청 "소파 옆 바닥에 빨간 캠핑 의자 하나 놔줘" → Gemini 음성 턴 → Scene-aware 전체 파이프라인 → TripoSplat 스플랫이 소파 옆 바닥에 배치(약 4.5크레딧 추정), Direct 모드 화분 배치(약 2.8크레딧 추정, 38초). chat의 `json_schema` + 이미지 + `input_audio` 동시 입력, 모든 기본 미디어 경로 존재 확인. |
| 실제 마이크 음성 턴 / OpenAI Realtime | GenPresso로 오디오+이미지 입력은 확인. 사람이 말하는 마이크 턴과 OpenAI Realtime 실서비스 호출은 미검증 |

## 패키지 구조

```
com.splatpresso.voice-to-3dgs/
  package.json  README.md  CHANGELOG.md  LICENSE.md  Third Party Notices.md
  Editor/Bootstrap/     렌더러 자동 설치 (아무것도 참조하지 않는 에디터 전용 어셈블리)
  Editor/               Setup Scene, 데모 씬, Project Settings, 디버그 창, 프로젝트 검사
  Runtime/Core          SplatPressoRoot, 파이프라인 오케스트레이터, 세션, 비용 원장
  Runtime/Settings      SplatPressoSettings, ModelRoute, PlacementTuning, ApiKeys
  Runtime/Api           GenPresso chat / 미디어 큐 클라이언트, 모델 엔드포인트, Decide/Verify
  Runtime/Voice         음성 에이전트 (GenPresso chat / OpenAI Realtime), 마이크, HUD
  Runtime/Rendering     캡처 렌더러 기능, 런타임 스플랫 에셋, PLY 리더
  Runtime/Placement     캡처 서비스, 배치 수학, 스폰, 홀로그램 프리뷰
  Runtime/Mesh          glTFast 메시 스포너 (glTFast가 있을 때만 컴파일)
  Runtime/Extras        카메라 컨트롤러, 디버그 단축키, 너지, 프레임 제한
  Tests/                EditMode / PlayMode 테스트 (목 GenPresso 서버 포함)
  Documentation~/ARCHITECTURE.md   개발자용 구조 문서
```

사용자 데이터는 패키지 바깥에 저장됩니다(git으로 설치한 패키지는 읽기 전용):

```
Assets/SplatPresso/Resources/SplatPressoSettings.asset   설정 (키는 넣지 않는 것을 권장)
Assets/SplatPresso/Demo/                                 데모 씬
<persistentDataPath>/SplatPresso/sessions/               세션 기록
~/.splatpresso/keys.json                                 API 키
```

## 크레딧 & 라이선스

- 패키지: MIT License, © 2026 Seonghoon Ban — [LICENSE.md](LICENSE.md)
- 렌더러: [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) (MIT, Aras Pranckevičius).
  별도 패키지로 설치되며 이 저장소에 포함되지 않습니다. PLY 리더와 런타임 에셋 데이터 레이아웃 코드는 이 프로젝트에서 가져와 수정했습니다
  — [Third Party Notices.md](Third%20Party%20Notices.md).
- 원격 모델(재배포하지 않음, API로만 호출): TripoSplat (VAST / Tripo, fal.ai·GenPresso 경유), Rodin (Hyper3D),
  nano-banana (Google), SAM-3 (Meta), Depth Anything V2, BiRefNet, Gemini (Google). 생성물의 이용 조건은 각 제공자와 GenPresso 약관을 따릅니다.

---

## Quick start (English)

**SplatPresso Voice To 3DGS** lets you talk to an agent in a Unity (URP) scene; it generates 3D Gaussian Splat objects that
fit what you are looking at and places them at the right position, scale and orientation. One GenPresso API key covers
everything; aras-p's UnityGaussianSplatting renderer is installed automatically and used unmodified.

1. **Requirements**: Unity 6000.0+, URP 17 with Render Graph, D3D12/Vulkan (Windows) or Metal (macOS), Git >= 2.14 on PATH,
   a GenPresso API key with at least 10 credits of balance (media jobs are refused below that).
2. **Install**: Package Manager → `+` → *Add package from git URL* →
   `https://github.com/Seonghoon-ban/splatpresso-voice-to-3dgs.git`.
   Accept the **Install (git)** dialog; it adds aras-p's renderer pinned to commit `2c6fed3`
   (`https://github.com/aras-p/UnityGaussianSplatting.git?path=/package#2c6fed37da67a217367261fcfcd3316d34c73e76`).
   No git? Choose *Install from OpenUPM* (renderer 1.1.1) and add this package from disk. After installing git, restart Unity **and** Unity Hub.
   An existing renderer install is left alone if it is 1.1.0 or newer. Older ones (1.0.0 and below cannot render under Render Graph)
   keep SplatPresso inactive; the bootstrap logs `SplatPresso needs Gaussian Splatting >= 1.1.0` with the manifest line to use instead,
   and replaces it only if you accept its dialog.
   "Filename too long" means the project path is too deep: move it or use OpenUPM.
3. **Key**: create one at https://genpresso.ai/ko/developers (starts with `gp_`, shown once). Save it in
   *Project Settings > SplatPresso* with **Save** (writes the user-profile keys file `~/.splatpresso/keys.json`, outside the project),
   or set `GENPRESSO_API_KEY`. Do not put keys in assets. Click *Test Connection*.
4. **Scene**: *SplatPresso > Create Demo Scene*, or *SplatPresso > Setup Scene…* in your own scene. Restart the editor if Setup
   switched the Windows graphics API to D3D12.
5. **Play**: hold **Space** and speak ("put a camping chair next to the table"), or press **Enter** to type.
   **M** toggles Scene-aware / Direct, **N** toggles Splat / Mesh (Mesh needs glTFast, experimental), **V** picks the microphone.
   Optional components: `FirstPersonCamera` (WASD + mouse), `DebugHotkeys` (F5 canned run, F6/F7 replay, F8 cancel, F9 spawn a .ply, Tab overlay),
   `PlacementNudgeController` (arrows / PgUp / PgDn / `[` `]` / `,` `.` to calibrate).
6. **Cost**: estimates in credits — image edit ~1.3, TripoSplat ~1.5, Rodin ~3; a scene-aware splat object is roughly 4–6 credits.
   Estimates are configurable per model route; `maxCostPerRun` (25) stops runaway retries.
7. **Model paths**: each step tries an ordered list of GenPresso paths (fal `fal-ai/x` is `gp/x` on GenPresso; other owners such as
   `google/…` or `tripo3d/…` pass through unchanged) and caches the first one that exists. If none exists and `FAL_KEY` is set,
   that step runs on fal.ai. Use *Test + Probe Media Models* to see which paths exist.
8. **Scripting**: `SplatPressoRoot.StartRun(PlacementRequest)`, `SubmitText(string)`, `CancelAll()`, and the events
   `RunStarted`, `RunProgress`, `ObjectUpdated`, `RunCompleted`, `RunFailed`, `RequestRejected`.
9. **Troubleshooting**: capture timeout → run *Setup Scene*; nothing renders → D3D12/Vulkan instead of DX11 and Render Graph
   compatibility mode off; upside-down capture → `SplatCaptureFeature.FlipReadbackOverride`; wrong orientation or size →
   nudge, then adjust `placement.yawOffsetDeg` / `uniformScaleFactor`; 402 → top up credits.

Developer notes (assemblies, data flow, session artifacts, extension points, tests): [Documentation~/ARCHITECTURE.md](Documentation~/ARCHITECTURE.md).
