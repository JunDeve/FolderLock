using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FolderLock
{
    /// <summary>암호화 키 한 쌍 (AES-256 암호화 키 + HMAC-SHA256 인증 키).</summary>
    internal sealed class KeyPair : IDisposable
    {
        public readonly byte[] Enc = new byte[32];
        public readonly byte[] Mac = new byte[32];

        public KeyPair(byte[] material64)
        {
            if (material64 == null || material64.Length != 64) throw new ArgumentException("key material must be 64 bytes");
            Buffer.BlockCopy(material64, 0, Enc, 0, 32);
            Buffer.BlockCopy(material64, 32, Mac, 0, 32);
        }

        public void Dispose()
        {
            Array.Clear(Enc, 0, Enc.Length);
            Array.Clear(Mac, 0, Mac.Length);
        }
    }

    internal sealed class WrongPasswordException : Exception
    {
        public WrongPasswordException() : base(L.T("비밀번호가 틀렸습니다.", "Wrong password.")) { }
    }

    internal sealed class TamperedDataException : Exception
    {
        public TamperedDataException(string what) : base(L.T("데이터가 손상되었거나 변조되었습니다: ", "Data is corrupted or has been tampered with: ") + what) { }
    }

    /// <summary>
    /// 블롭 포맷: "FLB1"(4) | IV(16) | AES-256-CBC 암호문 | HMAC-SHA256(32)
    /// HMAC = HMAC(macKey, magic || IV || 암호문 || context)  — Encrypt-then-MAC.
    /// context(블롭 ID)를 MAC 에 묶어서 파일끼리 바꿔치기해도 탐지된다.
    /// </summary>
    internal static class Crypto
    {
        public const int DefaultIterations = 600_000; // PBKDF2-SHA256, OWASP 권장치
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FLB1");
        private const int HeaderLen = 4 + 16;
        private const int TagLen = 32;
        private const int BufSize = 1 << 20; // 1MB: USB 처리량 향상

        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            lock (Rng) Rng.GetBytes(b);
            return b;
        }

        public static string RandomId() => ToHex(RandomBytes(16));

        public static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static KeyPair DeriveFromPassword(string password, byte[] salt, int iterations)
        {
            var pw = Encoding.UTF8.GetBytes(password);
            try
            {
                var material = Pbkdf2Sha256(pw, salt, iterations, 64);
                try { return new KeyPair(material); }
                finally { Array.Clear(material, 0, material.Length); }
            }
            finally { Array.Clear(pw, 0, pw.Length); }
        }

        /// <summary>
        /// PBKDF2-HMAC-SHA256. Windows 내장 CNG(bcrypt.dll) 를 우선 사용 — .NET Framework 의 관리 코드 구현보다 수~10배 빠르다.
        /// 결과는 표준 PBKDF2 와 동일 (실패 시 Rfc2898DeriveBytes 로 대체).
        /// </summary>
        public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int length)
        {
            try { return Pbkdf2Native(password, salt, iterations, length); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (CryptographicException) { }
            return Pbkdf2Managed(password, salt, iterations, length);
        }

        public static byte[] Pbkdf2Managed(byte[] password, byte[] salt, int iterations, int length)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }

        public static byte[] Pbkdf2Native(byte[] password, byte[] salt, int iterations, int length)
        {
            int st = Bcrypt.BCryptOpenAlgorithmProvider(out var alg, "SHA256", null, Bcrypt.BCRYPT_ALG_HANDLE_HMAC_FLAG);
            if (st != 0) throw new CryptographicException("BCryptOpenAlgorithmProvider 0x" + st.ToString("x8"));
            try
            {
                var key = new byte[length];
                // 빈 비밀번호도 허용되도록 길이 0 이면 더미 배열 전달
                var pw = password.Length == 0 ? new byte[1] : password;
                st = Bcrypt.BCryptDeriveKeyPBKDF2(alg, pw, (uint)password.Length, salt, (uint)salt.Length, (ulong)iterations, key, (uint)length, 0);
                if (st != 0) throw new CryptographicException("BCryptDeriveKeyPBKDF2 0x" + st.ToString("x8"));
                return key;
            }
            finally { Bcrypt.BCryptCloseAlgorithmProvider(alg, 0); }
        }

        private static class Bcrypt
        {
            public const uint BCRYPT_ALG_HANDLE_HMAC_FLAG = 0x8;

            [System.Runtime.InteropServices.DllImport("bcrypt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            public static extern int BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, string pszAlgId, string pszImplementation, uint dwFlags);

            [System.Runtime.InteropServices.DllImport("bcrypt.dll")]
            public static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

            [System.Runtime.InteropServices.DllImport("bcrypt.dll")]
            public static extern int BCryptDeriveKeyPBKDF2(IntPtr hPrf, byte[] pbPassword, uint cbPassword, byte[] pbSalt, uint cbSalt,
                ulong cIterations, byte[] pbDerivedKey, uint cbDerivedKey, uint dwFlags);
        }

        private static Aes NewAes(KeyPair k, byte[] iv)
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.KeySize = 256;
            aes.Key = k.Enc;
            aes.IV = iv;
            return aes;
        }

        // ---------------- 암호화 ----------------

        public static void Encrypt(Stream input, Stream output, KeyPair k, string context)
        {
            var iv = RandomBytes(16);
            using (var hmac = new HMACSHA256(k.Mac))
            using (var aes = NewAes(k, iv))
            using (var enc = aes.CreateEncryptor())
            {
                var head = new byte[HeaderLen];
                Buffer.BlockCopy(Magic, 0, head, 0, 4);
                Buffer.BlockCopy(iv, 0, head, 4, 16);
                output.Write(head, 0, head.Length);
                hmac.TransformBlock(head, 0, head.Length, null, 0);

                var tee = new MacTeeStream(output, hmac);
                var cs = new CryptoStream(tee, enc, CryptoStreamMode.Write);
                input.CopyTo(cs, BufSize);
                cs.FlushFinalBlock();
                // cs 를 Dispose 하면 tee → output 까지 닫히므로 Dispose 하지 않는다 (tee 는 output 을 닫지 않음).

                var ctx = Encoding.UTF8.GetBytes(context);
                hmac.TransformFinalBlock(ctx, 0, ctx.Length);
                output.Write(hmac.Hash, 0, TagLen);
            }
        }

        public static byte[] EncryptBytes(byte[] plain, KeyPair k, string context)
        {
            using (var i = new MemoryStream(plain, false))
            using (var o = new MemoryStream())
            {
                Encrypt(i, o, k, context);
                return o.ToArray();
            }
        }

        // ---------------- 복호화 ----------------

        /// <summary>먼저 MAC 을 전부 검증한 뒤에만 복호화한다 (검증 전 평문 출력 없음).</summary>
        public static void Decrypt(Stream input, Stream output, KeyPair k, string context)
        {
            var iv = CheckMac(input, k, context, out long start, out long ctLen);

            // 2차: 복호화
            input.Position = start + HeaderLen;
            using (var aes = NewAes(k, iv))
            using (var dec = aes.CreateDecryptor())
            {
                var cs = new CryptoStream(new NonClosingStream(output), dec, CryptoStreamMode.Write);
                var buf = new byte[BufSize];
                long left = ctLen;
                while (left > 0)
                {
                    int n = input.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new TamperedDataException(context);
                    cs.Write(buf, 0, n);
                    left -= n;
                }
                cs.FlushFinalBlock();
            }
        }

        /// <summary>1차 패스: 헤더 확인 + MAC 전체 검증. 통과하면 IV 를 돌려준다.</summary>
        private static byte[] CheckMac(Stream input, KeyPair k, string context, out long start, out long ctLen)
        {
            long total = input.Length - input.Position;
            if (total < HeaderLen + 16 + TagLen) throw new TamperedDataException(context);
            start = input.Position;
            ctLen = total - HeaderLen - TagLen;

            var head = ReadExact(input, HeaderLen);
            for (int i = 0; i < 4; i++) if (head[i] != Magic[i]) throw new TamperedDataException(context);
            var iv = new byte[16];
            Buffer.BlockCopy(head, 4, iv, 0, 16);

            // 1차: MAC 검증
            using (var hmac = new HMACSHA256(k.Mac))
            {
                hmac.TransformBlock(head, 0, head.Length, null, 0);
                var buf = new byte[BufSize];
                long left = ctLen;
                while (left > 0)
                {
                    int n = input.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new TamperedDataException(context);
                    hmac.TransformBlock(buf, 0, n, null, 0);
                    left -= n;
                }
                var ctx = Encoding.UTF8.GetBytes(context);
                hmac.TransformFinalBlock(ctx, 0, ctx.Length);
                var tag = ReadExact(input, TagLen);
                if (!FixedTimeEquals(tag, hmac.Hash)) throw new TamperedDataException(context);
            }
            return iv;
        }

        public static byte[] DecryptBytes(byte[] blob, KeyPair k, string context)
        {
            using (var i = new MemoryStream(blob, false))
            using (var o = new MemoryStream())
            {
                Decrypt(i, o, k, context);
                return o.ToArray();
            }
        }

        /// <summary>복호화 없이 MAC 만 검증 (원본 삭제 전 확인용).</summary>
        public static bool Verify(string blobPath, KeyPair k, string context)
        {
            try
            {
                // Encrypt-then-MAC: MAC 이 맞으면 암호문이 쓴 그대로라는 뜻 → 복호화 패스는 생략
                using (var fs = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufSize))
                {
                    CheckMac(fs, k, context, out _, out _);
                    return true;
                }
            }
            catch (TamperedDataException) { return false; }
            catch (CryptographicException) { return false; }
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(b, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
            return b;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ---------------- 보조 스트림 ----------------

        /// <summary>쓰는 바이트를 HMAC 에도 흘려보내는 write-only 스트림. 내부 스트림은 닫지 않는다.</summary>
        private sealed class MacTeeStream : Stream
        {
            private readonly Stream _inner;
            private readonly HashAlgorithm _mac;
            public MacTeeStream(Stream inner, HashAlgorithm mac) { _inner = inner; _mac = mac; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                _mac.TransformBlock(buffer, offset, count, null, 0);
                _inner.Write(buffer, offset, count);
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class NonClosingStream : Stream
        {
            private readonly Stream _inner;
            public NonClosingStream(Stream inner) { _inner = inner; }
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class NullStream : Stream
        {
            public override void Write(byte[] buffer, int offset, int count) { }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) { }
        }
    }
}
