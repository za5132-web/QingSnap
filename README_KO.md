# QingSnap

[<kbd>简体中文</kbd>](README.md)　[<kbd>English</kbd>](README_EN.md)　[<kbd>繁體中文</kbd>](README_ZH_TW.md)　[<kbd>日本語</kbd>](README_JA.md)　[<kbd>한국어</kbd>](README_KO.md)

> 정확하게 캡처하고, 길게 이어 붙이고, 바탕 화면에 고정합니다. OCR은 필요할 때만 오프라인으로 설치하세요.

QingSnap은 Windows 10/11용 경량 스크린샷 도구입니다. 스마트 선택, 픽셀 단위 조정, 편집 가능한 주석, 스크롤 캡처, 바탕 화면 핀, 오프라인 인식, 전체 기록 검색을 하나의 흐름으로 제공합니다.

![QingSnap v1.1.1 7단계 시작 가이드](docs/images/qingsnap-first-run-tutorial.png)

[![Release](https://img.shields.io/github/v/release/za5132-web/QingSnap?label=Release&color=67d9ea)](https://github.com/za5132-web/QingSnap/releases/latest)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-1674d1)
![.NET](https://img.shields.io/badge/.NET-8-6f42c1)

## 다운로드

- [QingSnap v1.1.1 다운로드](https://github.com/za5132-web/QingSnap/releases/download/v1.1.1/QingSnap-v1.1.1.zip)
- [v1.1.1 릴리스 노트](docs/RELEASE_NOTES_v1.1.1.md)
- [전체 기능 목록(중국어)](docs/QINGSNAP_COMPLETE_FEATURE_LIST.md)

ZIP 압축을 풀고 `QingSnap.exe`를 실행하세요. [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)이 필요합니다. 릴리스는 자체 서명이므로 GitHub Release에서만 내려받고 SHA-256을 확인하세요.

## 1분 핵심 흐름

첫 실행 시 7단계 애니메이션 가이드가 열리며 설정에서 언제든 다시 볼 수 있습니다.

1. `F1`로 캡처를 시작하고 창을 선택하거나 자유롭게 영역을 지정합니다.
2. X/Y/W/H, 비율, 고정 크기, 1px 이동으로 영역을 정밀 조정합니다.
3. 화살표, 도형, 텍스트를 추가하고 다중 선택과 Undo/Redo로 다시 편집합니다.
4. 자동 스크롤 또는 수동 프레임으로 긴 페이지를 캡처합니다.
5. `F3`로 최근 이미지를 붙이고 `M`으로 화면 가장자리에 보관합니다.
6. 문서처럼 이미지 텍스트를 선택하거나 QR 핫스폿을 사용합니다.
7. 텍스트, 태그, 출처로 기록을 검색하고 다시 붙이거나 같은 위치를 캡처합니다.

## 주요 기능

- **정밀 캡처**: 창 인식, 8방향 조정, 픽셀 확대경, 비율/고정 크기, `W/A/S/D` 미세 이동, 최근 5개 영역 순환.
- **편집 가능한 주석**: 펜, 선, 단/양방향 화살표, 도형, 텍스트, 번호, 모자이크, 강조, 흐림, 전체 Undo/Redo와 다중 선택.
- **긴 스크린샷**: 자동 스크롤, 겹침 매칭, 고정 헤더 제거, 재시도, 수동 보충, 전용 긴 이미지 리더.
- **바탕 화면 핀**: 최근 5개 이미지와 외부 클립보드, 부드러운 확대/축소, 원래 위치 복원, `M` 보관과 호버 펼침.
- **오프라인 OCR / QR**: OCR Tiny/Small은 필요할 때 한 번에 설치하고, QR은 OCR 없이 완전 오프라인으로 동작합니다.
- **전체 기록 검색**: SQLite 메타데이터, 태그, 출처, 페이지 로딩, 가상화, 다중 선택, 일괄 작업과 재캡처.
- **다중 모니터**: Per-Monitor V2, 혼합 DPI, 음수 가상 화면 좌표 지원.

## v1.1.1 업데이트

- 첫 실행 가이드를 7페이지 “1분 핵심 작업 흐름”으로 재구성했습니다.
- 사용자 데이터에 접근하지 않는 내장 시뮬레이션 애니메이션을 모든 페이지에 추가했습니다.
- Keycap, 아이콘 레이어, 모서리와 안내를 통일하고 핀, QR, `Ctrl+C` 잘림을 수정했습니다.
- Windows 애니메이션 줄이기 설정을 지원하고 빠른 페이지 전환 및 종료 후 리소스 정리를 강화했습니다.
- 자동화 테스트 149/149 통과, Release 빌드 0 Warning / 0 Error.

## 단축키

| 작업 | 키 |
| --- | --- |
| 영역 캡처 | `F1` |
| 핀 / 최근 이미지 순환 | `F3` |
| 최근 영역 | `Shift+F1` |
| 최근 5개 영역 순환 | `R` |
| 선택 / 편집 | `F2` |
| 실행 취소 / 다시 실행 | `Ctrl+Z` / `Ctrl+Y` |
| 핀 보관 / 복원 | `M` |
| 확인 / 취소 | `Enter` / `Esc` |

## 개인정보 및 빌드

이미지, OCR 텍스트, QR 내용은 제3자에게 업로드되지 않습니다. 네트워크는 사용자가 시작한 OCR 다운로드, 업데이트 확인, Release 다운로드에만 사용됩니다.

```powershell
dotnet restore QingSnap.sln
dotnet build QingSnap.sln -c Release
dotnet test tests\QingSnap.Tests\QingSnap.Tests.csproj -c Release
```

문제는 [GitHub Issues](https://github.com/za5132-web/QingSnap/issues)에 남겨 주세요. 현재 오픈 소스 라이선스는 부여되지 않았으며, 사용·수정·재배포에는 작성자의 허가가 필요합니다.

