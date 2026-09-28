# Changelog

All notable changes to this package are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-09-29

벽을 아는 물체 방향: 벽에 거는 물체와 벽에 붙는 가구는 벽과 평행하게, 벽에 붙여 놓입니다.

### Added
- **`placement.orientationMode` (기본 `SceneAware`)**: 벽에 거는 물체(그림·시계·박제·벽선반 등)와 벽에 붙는 가구(책장·수납장·
  TV장·옷장 등)는 캡처 depth에서 물체 뒤의 벽 평면을 찾아(수직 평면 RANSAC + 품질 게이트) 벽 법선 방향을 보게 돌리고,
  뒷면을 벽에서 `wallGapM`(1 cm) 띄워 붙입니다. 벽에 거는 물체는 물체 중심을 지나는 시선이 벽과 만나는 곳에 걸리고(뒤로 최대
  `wallMaxStandoffM` 0.30 m), 벽에 붙는 가구는 바닥 실루엣의 가운데로 옮겨집니다. 벽 판정에는 VLM 힌트와 depth 기하가 둘 다
  맞아야 하며, 하나라도 실패하면(창문·거울·문, 가구에 가려진 벽, 너무 비스듬한 시야, 평평한 물체, depth 없음 등) 예전처럼
  카메라를 봅니다. 결과는 카메라 방향에서 81° 이상 벗어나지 않으므로 사용자에게 등을 돌리지 않습니다. 크기는 바뀌지 않습니다.
- `Shadow` 모드: 벽 규칙을 계산해 로그로만 남기고 예전 자세를 적용합니다. `CameraFacing`은 이전 동작과 비트 단위로 같습니다
  (설정만 바꾸면 즉시 롤백).
- **`askVlmForOrientation` (기본 켬)**: DECIDE가 `against_wall`(yes/no)을, VERIFY가 물체마다 `support`, `back_against_wall`,
  `front_faces`를 돌려줍니다(추가 호출 없음). 끄면 이전 프롬프트·스키마를 바이트 단위로 그대로 씁니다. 힌트가 없는 이전 세션은
  `resting_surface: wall`과 이름 목록(`wallBackedCategories`)으로 판단합니다.
- 배치된 자세와 판단 근거(`PlacementRecord`: 규칙, 의도, 카메라/벽 yaw, 이동량, 벽 적합 통계)를 `result.json`의 각 물체
  `placement`에 저장합니다. 로그: `Placed '<이름>' at … yaw …`, `Orient '<이름>' rule=… cand=… intent=…`,
  `Verify orientation: …`. 선택한 물체의 기즈모로 벽(청록)·카메라 방향(빨강)·적용 방향(초록)·VLM 방향(노랑)·이동(자홍)을
  보여 줍니다(`orientationGizmos`). 미리보기 상자도 같은 방향으로 돌고 뒷면이 벽에 붙습니다.
- `SplatPresso > Diagnostics > Orientation Replay…`: 저장된 세션을 API 호출 없이 다시 풀어 카메라 방향/벽 규칙을 CSV로
  비교합니다(`-executeMethod SplatPresso.EditorTools.OrientationReplay.RunBatch -replayRoots "a;b"`로 배치 실행 가능).

### Notes
- 저장된 세션의 물체 131개 재생: 벽 의도가 있는 16개 중 13개가 벽에 맞춰졌고(테스트 룸 90.0°/90.1°/180.0°/책장 90.0°,
  DiC 거실 7개 95.5–95.9°, 두 번째 방 2개 60.7°/−119.2°), 나머지 3개(장식 칼·랜턴·소파 등받이에 기댄 그림)와 벽 의도가 없는
  115개는 이전과 비트 단위로 같았습니다. 벽 판단 계산은 p95 22 ms.
- ENHANCE가 물체를 정면 뷰로 다시 그리므로 편집 이미지의 비스듬한 각도는 보존되지 않고, 벽 물체는 벽 방향으로 맞춰집니다.
  의자·화분·램프처럼 벽과 무관한 물체는 계속 카메라를 봅니다.
- 라이브 확인(데모 룸, "왼쪽 벽에 액자를 하나 걸고, 오른쪽 벽에 붙여서 책장도 하나 놔줘"): 액자 139.2° → 90.0°(왼쪽 벽),
  책장 −138.2° → −90.1°(오른쪽 벽), 둘 다 VERIFY 힌트(`wall_mounted`, `back_against_wall=yes`)와 depth 벽 적합이 일치.
- TripoSplat이 평평한 벽 장식에도 두꺼운 뒷면을 지어내는 경우가 많습니다(라이브: 액자 깊이 0.77 = 폭 0.77). 그런 물체는 벽에서
  최대 0.30 m만 띄우고 나머지는 벽 속에 들어갑니다(메시 벽은 가려 줌, 3DGS 벽은 못 가림). 정면 축으로 눌러 평평하게 만드는 방법도
  시험했지만 그림이 번져 보여 넣지 않았습니다.
- 바닥이 기울어진 씬은 지원하지 않습니다.

## [0.3.0] - 2026-09-28

음성 대화를 원본 연구 프로젝트처럼 빠르게: OpenAI 키가 있으면 OpenAI Realtime이 기본입니다.

### Changed
- **`voiceBackend`에 `Auto` 추가, 새 기본값**: OpenAI 키가 있으면 OpenAI Realtime(말을 멈추고 약 1.5초 만에 답변 음성),
  없으면 GenPresso 음성 에이전트(약 13초). 기존 설정 에셋의 옛 기본값 `GenpressoChat`은 한 번 `Auto`로 옮겨집니다
  (OpenAI 키가 없으면 동작은 같음). 이후에 직접 고른 값은 그대로 유지됩니다.
- Realtime: Space를 누르는 순간 화면 스냅샷을 찍어 두고, 떼면 기다리지 않고 바로 응답을 요청합니다.
- Realtime: 생성 요청을 입력 음성 받아쓰기를 기다리지 않고 도구 호출 즉시 시작합니다(최대 2초 지연 제거).
- Realtime: 세션 설정(지시·도구·push-to-talk)이 적용된 뒤에만 준비 상태가 됩니다. 설정이 거부되면 기본 목소리로 한 번 다시 보냅니다.
- Realtime 연결: 네트워크 오류는 계속 재연결(1–15초 → 30초 간격), 15초 연결 타임아웃, 응답이 없는 죽은 연결 감지.
  키 거부(401/403), 모델 접근 불가(404), 할당량 없음, WebSocket 미지원 빌드는 즉시 실패로 판정합니다(Unity 런타임은 거부 상태 코드를
  알려 주지 않으므로 같은 키로 `GET /v1/models/{model}`을 호출해 원인을 확인).
- `Auto`에서 Realtime이 실패하면 GenPresso 음성 에이전트로 자동 전환하고 HUD에 이유를 표시합니다.
- HUD에 동작 중인 음성 에이전트(`Realtime` / `GenPresso voice`)와 재연결 원인을 표시합니다. 무해한 서버 경합 오류는 HUD에 띄우지 않습니다.
- `replyLanguage = Auto`: 진행 상황 안내도 사용자가 마지막으로 말한 언어로 말합니다.
- Test Connection이 OpenAI 키와 Realtime 모델 접근 권한을 확인하고, 실제로 쓰일 음성 에이전트를 표시합니다. Validate Project도 표시합니다.
- 플레이 중에 키를 저장·삭제하면 음성 에이전트가 바로 전환됩니다.

### Fixed
- **플레이어 빌드(Managed Stripping High)에서 Realtime이 연결되지 않던 문제**: `System.Configuration.ExeConfigurationHost` 등
  TLS 핸드셰이크가 이름으로 만드는 타입이 제거됐습니다. 빌드 시 link.xml에 보존합니다(라이브 플레이어로 확인).
- 첫 연결이 한 번 실패한 뒤 성공하면 "재연결"로 처리해 새 세션에 잘못된 안내를 넣던 문제.

### Added
- `OpenAIRealtimeBackend.EndpointOverride`(테스트·프록시용), `RealtimeSocket.ClassifyConnectError` / `InterpretModelProbe` / `ModelProbeUrl`,
  `VoiceAgent.ResolveBackend`. 모의 WebSocket 서버로 Realtime 백엔드를 검사하는 테스트.

## [0.2.0] - 2026-09-28

실사용 테스트 피드백(대기 시간, 한국어 인식, 음성 답변)을 반영했습니다.

### Added
- **답변 음성 (GenPresso TTS)**: GenpressoChat 백엔드의 답변을 GenPresso 음성 합성으로 읽어 줍니다(`speakReplies`, 기본 켜짐).
  새 미디어 경로 `textToSpeech` = `gp/minimax/speech-02-turbo`(24 kHz PCM 요청) → 대체 `gp/elevenlabs/tts/multilingual-v2`(MP3).
  목소리 `ttsVoice`, 속도 `ttsSpeed`. 실측 5~10초, 한국어 발음 확인(합성 음성을 다시 받아쓰기해 원문과 일치). Space로 즉시 중단.
  OpenAI 키는 계속 선택 사항입니다(Realtime 백엔드).
- **무음 가드**: push-to-talk 녹음의 최대 레벨이 `silenceThreshold`(기본 0.01) 미만이면 모델에 보내지 않고 원인과 확인 방법을 표시합니다.
  무음을 받은 모델이 요청을 지어내던 문제("한국어로 말했는데 엉뚱하게 알아들음"의 원인: OS에서 마이크가 무음)를 막습니다.
  녹음 중 1초 이상 무음이면 HUD에 `No sound from the mic`.
- **3D 모델 워밍업** (`warmUpModels`, `warmUpIntervalSec` 180초, `keepWarmMinutes` 15분): 말하기·입력·생성 시작 시 과금되지 않는
  (검증 실패) 요청으로 TripoSplat/Rodin 워커를 미리 깨우고, 활동 중에는 식지 않게 유지합니다. 실측으로 TripoSplat은 4분 쉬면 그대로,
  8분 쉬면 콜드 스타트(5.5분 대기)였습니다.
- `MediaJobClient.UseConcurrencyGate`, `FirstPollIntervalSec`; `AudioStreamPlayer.EnqueueSamples`; `MicCapture.CapturePeak`;
  `VoiceAgent.MicSeemsSilent`, `IsPreparingSpeech`, `IsSilentUtterance`.

### Fixed
- GenPresso의 `model_not_found` 오류 코드도 "경로 없음"으로 인식해 다음 후보로 넘어갑니다.

## [0.1.1] - 2026-09-28

실제 GenPresso API로 전 과정을 검증하면서 발견한 문제를 고쳤습니다.

### Fixed
- **플레이어 빌드에서 음성 턴이 모두 실패하던 문제**: Managed Stripping(High/IL2CPP)이 JSON 응답 타입(`VoiceTurnResponse` 등)의
  기본 생성자를 제거해 역직렬화가 실패했습니다. 빌드 시 생성하는 link.xml이 SplatPresso 런타임 어셈블리를 보존합니다.
- **잘못된 GenPresso 경로 처리**: GenPresso는 `gp/...` 경로를 제출 시점에 거의 다 받아들이고, 없는 경로는 작업 실행 후
  `FAILED` + 404 `Path ... not found`로 알려줍니다. 이제 이 경우도 다음 후보로 넘어가고(과금 안 됨, 원장 추정치도 되돌림),
  실제로 동작이 확인된 경로만 캐시합니다.
- **Test + Probe Media Models**: 제출 수락만으로 "존재"라고 판단하던 것을, 작업이 끝날 때까지 기다려 결과(422 = 존재, 404 = 없음)로
  판단하도록 바꿨고, 모든 후보를 동시에 확인합니다.

### Changed
- TripoSplat 후보 경로: `tripo3d/triposplat` → `gp/triposplat` (실측으로 확인; `gp/tripo3d/triposplat`은 존재하지 않아 제거).
- TripoSplat 타임아웃 300초 → 600초 (콜드 스타트/대기열로 6분 이상 걸린 사례 실측).
- 테스트용 모의 서버가 실제 GenPresso의 비동기 검증·경로 오류·취소 응답을 그대로 재현합니다.

## [0.1.0] - 2026-09-28

첫 배포 버전. 연구용 프로토타입(음성 에이전트 기반 장면 인식 Gaussian Splat 생성·배치)을
**GenPresso API 키 하나**와 **수정하지 않은 aras-p UnityGaussianSplatting**으로 동작하는 범용 패키지로 옮겼습니다.

### Added
- 음성/텍스트 요청 → 캡처 → 판단(Decide) → 화면 편집(Edit) → 검증(Verify) → 객체 분할·다듬기 → TripoSplat 3D → depth 기반 배치까지의 전체 파이프라인.
- 생성 모드 두 가지(`M`): **Scene-aware**(씬에 어울리게 편집) / **Direct**(말한 설명만으로 생성, 캡처는 위치 결정에만 사용).
- 표현 방식 두 가지(`N`): **Splat**(TripoSplat `.ply`) / **Mesh**(Rodin `.glb`, glTFast 필요, 실험적).
- GenPresso chat 기반 음성 에이전트: push-to-talk WAV + 현재 화면을 한 번에 보내고 strict JSON(`transcript`, `reply`, `actions`)으로 응답. 선택적으로 OpenAI Realtime 백엔드.
- IMGUI HUD: 상태 표시, 모드 칩, 답변 자막, Enter 텍스트 입력, `V` 마이크 선택, 런별 진행 상황, 키 누락 배너.
- 모델 라우팅(`ModelRoute`): 단계마다 GenPresso 후보 경로를 순서대로 시도하고 성공한 경로를 7일간 캐시. 모두 없으면 `FAL_KEY`로 fal.ai 대체 실행.
- 미디어 큐 클라이언트: `status_url`/`response_url`/`cancel_url` 그대로 사용, `Retry-After` 존중, 결과가 늦게 오는 구간(200 "in progress") 재시도, COMPLETED 뒤 결과 422 처리, 타임아웃·취소 시 원격 취소 요청, 4 MB 본문 가드.
- 추정 크레딧 기반 런별 비용 원장과 상한(`maxCostPerRun`).
- 의존성 부트스트랩: 렌더러가 없으면 커밋 `2c6fed3`에 고정해 자동 설치(git 없으면 OpenUPM 대안), 배치 모드용 `-splatpressoInstallDeps` / `-splatpressoExitWhenDone`. 렌더러는 1.1.0 이상(Render Graph 지원)이 필요하며, 그보다 낮은 버전이 이미 설치돼 있으면 본체가 컴파일되지 않고 부트스트랩이 오류와 교체 방법을 알립니다.
- 에디터 도구: Setup Scene(URP·Render Graph·렌더러 기능 순서·그래픽 API·씬 구성), Create Demo Scene, Project Settings > SplatPresso(키 저장·출처 표시·Test Connection·Test + Probe Media Models), Debug Window(단계별 리플레이·미리보기·원장), Validate Project + 빌드 전 검사, IL2CPP용 link.xml 생성.
- URP Render Graph 원샷 캡처(RGB + 미터 단위 depth + 카메라 포즈)와 런타임 스플랫 에셋 생성(렌더러 공개 API 사용).
- 선택 컴포넌트: `FirstPersonCamera`, `FlyCamera`, `DebugHotkeys`, `PlacementNudgeController`, `FocusFrameCap`.
- 스크립트 API: `SplatPressoRoot.StartRun` / `RunAsync` / `SubmitText` / `StartReplay` / `Cancel`, `runId`가 담긴 이벤트, `RequestGate`.
- EditMode / PlayMode 테스트(목 GenPresso 서버로 전체 파이프라인 E2E 포함)와 합성 테스트 픽스처 생성 스크립트(`Tools~/make_fixtures.py`).

### Changed (프로토타입 대비)
- 배치 캘리브레이션 기본값을 실측값으로: `yawOffsetDeg` 270, `uniformScaleFactor` 0.9.
- TripoSplat 좌표 보정(`contentRotationEuler`, `contentScale`)을 설정 한 곳에서만 관리(프리팹과의 불일치 제거).
- 비용 단위를 USD에서 GenPresso 크레딧(추정)으로. 원장은 리플레이마다 새로 시작.
- 입력은 Input System / 구 Input Manager 모두 지원(`InputCompat`). Input System 패키지를 의존성으로 강제하지 않음.
- 스폰 상한은 보이는 오브젝트만 제거.

### Fixed (프로토타입의 잠재 버그)
- 렌더러 기능이 없거나 카메라가 렌더링되지 않을 때 캡처가 영원히 대기하던 문제 → 프레임/시간 워치독과 명확한 오류 메시지.
- Edit부터 리플레이할 때 이전 편집의 depth가 재사용되던 문제, depth 재시작 시 이전 결과가 남던 문제.
- Verify 이전부터 리플레이할 때 이전 편집 이미지로 만든 객체 캐시가 재사용되던 문제.
- LLM이 준 객체 id가 중복될 때 폴더·결과가 충돌하던 문제.
- 취소 시 객체 수만큼 반복되던 안내, 불가능 판정 시 두 번 나오던 안내.
- 런타임 스플랫 에셋의 데이터가 해제되지 않던 문제(생성물 삭제 시 함께 해제).
- 만료된 호스팅 URL로 리플레이할 때 실패하던 문제(로컬 파일로 재시도).
- 도메인 리로드를 끈 프로젝트에서 정적 상태가 플레이 세션 사이에 남던 문제.
