# FolderLock

[English](README.md) | **한국어**

USB에 넣어 다니는 **폴더 금고**. 폴더를 비밀번호로 잠그고, 전용 탐색기 창에서만 엽니다.

- USB 안에는 **항상 암호화된 상태로만** 저장 → 그냥 뽑거나 PC가 꺼져도 안전
- 설치·관리자 권한 불필요, 단일 exe (약 300KB, Windows 10/11 기본 내장 .NET Framework 4.8)
- 금고 폴더가 사라지면(USB 분리) 즉시 잠금 + PC 임시파일 삭제, 10분 무입력 시 자동 잠금
- 사진·텍스트는 디스크에 풀지 않고 메모리에서 바로 보기/편집
- 수만 개 규모 폴더도 처리: 처리량·남은 시간 표시, **취소** 가능 (취소해도 처리된 것은 금고에, 나머지 원본은 그대로)
- 화면은 **한국어/영어** 지원 — Windows 표시 언어를 따라가며, `FolderLock.exe --lang=en` / `--lang=ko` 로 강제 가능

> ⚠️ **개인 프로젝트입니다.** 표준 암호 알고리즘만 사용하고 테스트를 거쳤지만, 전문 보안 감사를 받지 않았습니다.
> 어떠한 보증도 없이 제공되며(MIT 라이선스), 데이터 손실·유출에 대한 책임은 사용자에게 있습니다.
> 중요한 파일은 반드시 별도 백업을 두고 사용하세요.

## 사용법
1. `FolderLock.exe` 실행 → **폴더 잠그기** → 폴더 선택 → 비밀번호 설정
   - 폴더 안 기존 파일은 암호화 후 원본 삭제 (암호화본 검증이 끝난 것만)
2. 다음부터는 실행 → 잠긴 폴더 선택 → **폴더 열기** → 비밀번호 → 전용 탐색기
   - USB 안의 잠긴 폴더는 목록에 자동으로 뜹니다. **새로고침**은 USB 전체(와 꽂혀 있는 다른 USB)를 다시 찾고, 그 밖의 위치에 있는 잠긴 폴더는 시작 화면에 끌어다 놓으면 열립니다
   - 파일을 창에 끌어다 놓으면 암호화되어 들어감
   - 더블클릭: 사진·텍스트는 메모리에서, 그 외는 PC 임시폴더에 풀어서 연결 프로그램으로 열고, 수정·저장하면 자동으로 다시 암호화
   - **꺼내기**: 복호화해서 다른 곳에 저장
3. **잠그기** 또는 창 닫기 → 임시파일 삭제 후 잠금

> 비밀번호를 잊으면 복구할 방법이 없습니다.

## 암호 방식
| 항목 | 내용 |
|---|---|
| 키 유도 | PBKDF2-HMAC-SHA256, 600,000회, 16바이트 솔트 (Windows CNG) |
| 본문 | AES-256-CBC + HMAC-SHA256 (Encrypt-then-MAC), 파일별 무작위 IV |
| 무결성 | 블롭 ID를 MAC에 포함 → 변조·바꿔치기·잘림 탐지, MAC 검증 전 복호화 안 함 |
| 메타데이터 | 파일명·폴더 구조는 암호화된 `index.dat`에만 존재, 디스크 파일명은 무작위 |

금고 폴더 구조:
```
금고\
├─ vault.lock     솔트·반복횟수 + 비밀번호로 감싼 마스터 키
├─ index.dat      암호화된 파일 목록 (+ .bak)
├─ data\xx\*.fl   암호화된 파일 본문
└─ [잠긴 금고] FolderLock.exe로 여세요.txt
```

알려진 한계는 [SECURITY_AUDIT.md](SECURITY_AUDIT.md) 참고 (외부 프로그램으로 연 파일의 PC 임시본, 플래시 메모리에서 지운 원본의 잔존 가능성 등).

## 빌드
.NET SDK(6 이상)와 .NET Framework 4.8 Developer Pack 필요.

```bash
dotnet build FolderLock.csproj -c Release
# 결과: bin/Release/FolderLock.exe
```

테스트:
```bash
dotnet run --project Tests/Tests.csproj -c Release
```

아이콘(`FolderLock.ico`) 다시 만들기:
```bash
powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
```

## 라이선스
[MIT](LICENSE)
