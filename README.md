# Codex Limit Viewer

Windows 화면 위에 Codex Plus의 5시간·주간 한도와 각 초기화 시각을 표시하는 가벼운 오버레이입니다.

![Codex Limit Viewer](assets/codex-quota-icon.png)

## 바로 실행

1. Codex 데스크톱 앱 또는 Codex CLI에서 ChatGPT 계정으로 로그인합니다.
2. [dist/CodexQuotaOverlay.exe](dist/CodexQuotaOverlay.exe)를 다운로드해 더블 클릭합니다.
3. 화면 우하단에 오버레이가 표시됩니다. 상단을 드래그하면 위치를 옮길 수 있고, `↻`는 즉시 새로고침, `×` 또는 `Esc`는 종료입니다.

Windows SmartScreen이 표시되면 파일의 출처를 확인한 뒤 `추가 정보 → 실행`을 선택할 수 있습니다. 현재 EXE는 코드 서명되지 않았습니다.

## 동작 방식

- 공식 Codex App Server의 `account/rateLimits/read`를 호출합니다.
- `usedPercent`에서 남은 비율을 계산하고, 약 5시간(300분) 창과 약 7일(10,080분) 창을 별도 게이지로 표시합니다.
- 5시간 창을 현재 계정 응답에서 받을 수 없는 경우에는 해당 게이지에 `정보 없음`을 표시합니다.
- 계정 토큰을 읽거나 복사하지 않습니다. 사용자의 기존 Codex 로그인 상태를 사용합니다.
- Microsoft Store판 Codex는 첫 실행에 필요한 공식 `codex.exe` 복사본을 `%LOCALAPPDATA%\CodexQuotaOverlay\runtime`에 준비할 수 있습니다.
- 오버레이 위치와 오류 로그는 `%LOCALAPPDATA%\CodexQuotaOverlay`에만 저장됩니다.

## 설치 도우미

바탕 화면 바로가기를 만들려면 PowerShell에서 아래 명령을 실행합니다.

```powershell
.\Install.ps1
```

Windows 로그인 시 자동 실행도 등록하려면:

```powershell
.\Install.ps1 -StartWithWindows
```

바로가기를 제거하려면:

```powershell
.\Install.ps1 -Remove
```

## 문제 해결

- 오버레이가 `오프라인`으로 표시되면 Codex에 로그인되어 있는지와 네트워크 연결을 확인한 뒤 `↻`를 누릅니다.
- `Codex 실행 파일을 찾지 못했습니다`가 나오면 Codex 데스크톱 앱 또는 CLI를 설치한 뒤 한 번 실행합니다.
- 다른 `codex.exe`를 사용하려면 실행 전 환경 변수를 지정합니다.

```powershell
$env:CODEX_QUOTA_CODEX_PATH = 'C:\path\to\codex.exe'
.\dist\CodexQuotaOverlay.exe
```

- 오류 세부 정보는 `%LOCALAPPDATA%\CodexQuotaOverlay\overlay.log`에서 확인할 수 있습니다.

## 개발 및 빌드

Windows와 .NET Framework C# 컴파일러(`csc.exe`)가 필요합니다. 다음 명령으로 EXE를 다시 만듭니다.

```powershell
.\Build-Exe.ps1
```

EXE 구성 확인:

```powershell
.\dist\CodexQuotaOverlay.exe --validate
```

현재 Codex 로그인으로 실제 한도 조회 확인:

```powershell
.\dist\CodexQuotaOverlay.exe --live-check
```

`assets/codex-quota-icon.png`를 수정했다면 Python과 Pillow를 준비한 뒤 아이콘을 다시 생성합니다.

```powershell
python .\scripts\build_icon.py
.\Build-Exe.ps1
```

## 프로젝트 구성

- `dist/CodexQuotaOverlay.exe`: 바로 실행 가능한 Windows 앱
- `Bootstrap.cs`: 네이티브 WinForms 오버레이 및 Codex App Server 통신 구현
- `Build-Exe.ps1`: EXE 빌드 스크립트
- `Install.ps1`: 바로가기 및 시작 프로그램 등록 도구
- `assets/`: 앱 아이콘 원본과 Windows ICO

## 배포 참고

배포 시에는 GitHub Releases에 `CodexQuotaOverlay.exe`와 SHA-256 해시를 첨부하는 방식을 권장합니다. 버전별 실행 파일을 명확히 구분할 수 있고, 소스 코드 변경과 배포 파일을 분리할 수 있습니다.
