# Changelog

All notable changes to this package are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
