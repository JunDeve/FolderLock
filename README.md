# FolderLock

**English** | [한국어](README.ko.md)

A **folder vault you carry on a USB drive**. Lock a folder with a password and open it only through FolderLock's built-in explorer window.

- Files on the USB are **always stored encrypted**: pulling the drive out or a sudden power-off never leaves plaintext behind
- No installation, no admin rights. One ~300 KB exe that runs on the .NET Framework 4.8 built into Windows 10/11
- Locks immediately when the vault folder disappears (USB removed) and wipes PC-side temp files; also auto-locks after 10 minutes of inactivity
- Photos and text files open straight from memory, never written to disk

> The user interface is currently in **Korean only**.

> ⚠️ **This is a personal project.** It uses only standard cryptographic primitives and is covered by tests, but it has **not** been audited by security professionals.
> It is provided as-is without any warranty (MIT License); you are responsible for any data loss or leakage.
> Always keep a separate backup of important files.

## Usage
1. Run `FolderLock.exe` → **폴더 잠그기 (금고 만들기)** ("Lock a folder / create a vault") → pick a folder → set a password
   - Existing files in the folder are encrypted, then the originals are deleted (only after each encrypted copy has been verified)
2. Afterwards: run → pick the vault → enter the password → built-in explorer
   - Drag and drop files onto the window to encrypt them into the vault
   - Double-click: photos and text open in memory; other files are decrypted to a PC temp folder and opened with the associated app. When you save there, the changes are re-encrypted into the vault automatically
   - **꺼내기** ("Export") decrypts selected items to a location of your choice
3. **잠그기** ("Lock") or close the window → temp files are deleted and the vault is locked

> If you forget the password, there is no way to recover the data.

## Cryptography
| Item | Details |
|---|---|
| Key derivation | PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt (Windows CNG) |
| Content | AES-256-CBC + HMAC-SHA256 (Encrypt-then-MAC), random IV per file |
| Integrity | The blob ID is bound into the MAC, so tampering, swapping or truncating files is detected; nothing is decrypted before the MAC is verified |
| Metadata | File names and folder structure exist only inside the encrypted `index.dat`; on-disk file names are random |

Vault folder layout:
```
vault\
├─ vault.lock     salt, iteration count, master key wrapped with the password
├─ index.dat      encrypted file list (+ .bak)
├─ data\xx\*.fl   encrypted file contents
└─ [잠긴 금고] FolderLock.exe로 여세요.txt   (plain notice: "locked vault, open with FolderLock.exe")
```

Known limitations are listed in [SECURITY_AUDIT.md](SECURITY_AUDIT.md) (written in Korean). The main ones:
- Files opened with an external app leave a temporary decrypted copy on that PC until you lock (or until the next run if the app was killed)
- Originals deleted when converting a folder may still be partly recoverable from flash memory
- A keylogger on the host PC can capture the password

## Build
Requires the .NET SDK (6 or later) and the .NET Framework 4.8 Developer Pack.

```bash
dotnet build FolderLock.csproj -c Release
# output: bin/Release/FolderLock.exe
```

Tests:
```bash
dotnet run --project Tests/Tests.csproj -c Release
```

Regenerate the icon (`FolderLock.ico`):
```bash
powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
```

## License
[MIT](LICENSE)
