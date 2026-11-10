using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FolderLock;

internal static class Program
{
    const int Iter = 10_000; // 테스트 속도용 (실사용은 600,000)
    static int _pass, _fail;
    static string _work;

    static int Main(string[] args)
    {
        _work = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "fl_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_work);
        try
        {
            Run("PBKDF2 네이티브 = 표준값 = 관리코드", Pbkdf2Matches);
            Run("암복호화 왕복 (여러 크기)", CryptoRoundTrip);
            Run("변조 탐지", TamperDetection);
            Run("금고 생성/열기 + 틀린 비밀번호", CreateOpenWrongPassword);
            Run("파일·폴더 넣기/꺼내기", AddAndExtract);
            Run("USB 안에 평문·파일명 없음", NoPlaintextOnDisk);
            Run("기존 폴더 → 금고 변환", ConvertInPlace);
            Run("변환 시 같은 이름 덮어쓰지 않음", ConvertDoesNotOverwrite);
            Run("이름 바꾸기 / 삭제", RenameAndDelete);
            Run("내용 교체 + 재오픈 유지", ReplaceAndReopen);
            Run("비밀번호 변경", ChangePassword);
            Run("목록 손상 시 백업으로 복구", IndexRecovery);
            Run("0바이트·한글·긴 파일명", EdgeNames);
        }
        finally
        {
            try { Directory.Delete(_work, true); } catch { }
        }
        Console.WriteLine($"\n결과: {_pass} 통과, {_fail} 실패");
        return _fail == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _pass++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _fail++; Console.WriteLine("FAIL  " + name + "\n      " + ex.GetType().Name + ": " + ex.Message); }
    }

    static void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

    static void Throws<T>(Action a, string msg) where T : Exception
    {
        try { a(); }
        catch (T) { return; }
        catch (Exception ex) { throw new Exception(msg + " (다른 예외: " + ex.GetType().Name + ")"); }
        throw new Exception(msg + " (예외 없음)");
    }

    static string NewDir(string n) { var d = Path.Combine(_work, n); Directory.CreateDirectory(d); return d; }
    static byte[] Rand(int n) => Crypto.RandomBytes(n);

    // ------------------------------------------------------------

    static void Pbkdf2Matches()
    {
        // RFC 7914 / 널리 쓰이는 PBKDF2-HMAC-SHA256 테스트 벡터
        var v = Crypto.ToHex(Crypto.Pbkdf2Native(Encoding.ASCII.GetBytes("password"), Encoding.ASCII.GetBytes("salt"), 1, 32));
        Assert(v == "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b", "표준 벡터 불일치: " + v);
        foreach (var pw in new[] { "", "a", "한글비밀번호!@#", new string('x', 200) })
        {
            var p = Encoding.UTF8.GetBytes(pw);
            var s = Rand(16);
            Assert(Crypto.Pbkdf2Native(p, s, 1000, 64).SequenceEqual(Crypto.Pbkdf2Managed(p, s, 1000, 64)), "네이티브≠관리코드: '" + pw + "'");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Crypto.Pbkdf2Native(Encoding.UTF8.GetBytes("pw"), Rand(16), Crypto.DefaultIterations, 64);
        Console.WriteLine($"      (네이티브 {Crypto.DefaultIterations:N0}회: {sw.ElapsedMilliseconds} ms)");
    }

    static void CryptoRoundTrip()
    {
        var k = new KeyPair(Rand(64));
        foreach (var n in new[] { 0, 1, 15, 16, 17, 65535, 65536, 65537, 1_048_579 })
        {
            var p = Rand(n);
            var c = Crypto.EncryptBytes(p, k, "ctx");
            var d = Crypto.DecryptBytes(c, k, "ctx");
            Assert(p.SequenceEqual(d), "왕복 불일치 size=" + n);
        }
    }

    static void TamperDetection()
    {
        var k = new KeyPair(Rand(64));
        var c = Crypto.EncryptBytes(Encoding.UTF8.GetBytes("secret data 1234567890"), k, "a");
        for (int i = 0; i < c.Length; i++)
        {
            var t = (byte[])c.Clone();
            t[i] ^= 0x01;
            Throws<TamperedDataException>(() => Crypto.DecryptBytes(t, k, "a"), "변조 미탐지 at " + i);
        }
        Throws<TamperedDataException>(() => Crypto.DecryptBytes(c, k, "b"), "context 바꿔치기 미탐지");
        Throws<TamperedDataException>(() => Crypto.DecryptBytes(c, new KeyPair(Rand(64)), "a"), "다른 키 미탐지");
        Throws<TamperedDataException>(() => Crypto.DecryptBytes(c.Take(c.Length - 1).ToArray(), k, "a"), "잘림 미탐지");
    }

    static void CreateOpenWrongPassword()
    {
        var d = NewDir("v1");
        using (Vault.Create(d, "correct horse", Iter)) { }
        Assert(Vault.IsVault(d), "IsVault false");
        using (var v = Vault.Open(d, "correct horse")) Assert(v.FileCount == 0, "빈 금고 아님");
        Throws<WrongPasswordException>(() => Vault.Open(d, "wrong"), "틀린 비번 통과");
        Throws<WrongPasswordException>(() => Vault.Open(d, ""), "빈 비번 통과");
        Throws<InvalidOperationException>(() => Vault.Create(d, "x", Iter), "중복 생성 허용");
    }

    static void AddAndExtract()
    {
        var src = NewDir("src1");
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
        var big = Rand(3_000_000);
        File.WriteAllBytes(Path.Combine(src, "sub", "deep", "big.bin"), big);
        var single = Path.Combine(_work, "single.txt");
        File.WriteAllText(single, "single file");

        var d = NewDir("v2");
        using (var v = Vault.Create(d, "pw", Iter))
        {
            v.AddPaths(new[] { src, single }, "");
            Assert(v.List("").Select(e => e.Name).OrderBy(x => x).SequenceEqual(new[] { "single.txt", "src1" }), "루트 목록");
            Assert(v.Get("src1/sub/deep/big.bin") != null, "깊은 파일 없음");
            Assert(v.Get("src1/sub").IsDir, "폴더 아님");
            // 같은 이름 다시 넣기 → (2)
            v.AddPaths(new[] { single }, "");
            Assert(v.Get("single (2).txt") != null, "중복 이름 처리");
        }
        using (var v = Vault.Open(d, "pw"))
        {
            var outDir = NewDir("out2");
            v.ExtractFolderTo(v.Get("src1"), outDir);
            Assert(File.ReadAllText(Path.Combine(outDir, "src1", "a.txt")) == "hello", "a.txt 내용");
            Assert(File.ReadAllBytes(Path.Combine(outDir, "src1", "sub", "deep", "big.bin")).SequenceEqual(big), "big.bin 내용");
            Assert(Encoding.UTF8.GetString(v.ReadAll(v.Get("single.txt"))) == "single file", "ReadAll");
        }
        Assert(File.Exists(single), "원본이 지워짐(AddPaths 는 원본 보존해야)");
    }

    static void NoPlaintextOnDisk()
    {
        var marker = "TOP_SECRET_MARKER_가나다_12345";
        var src = NewDir("src3");
        File.WriteAllText(Path.Combine(src, "비밀문서_신분증.txt"), string.Concat(Enumerable.Repeat(marker, 100)));
        var d = NewDir("v3");
        using (var v = Vault.Create(d, "pw", Iter)) v.AddPaths(new[] { src }, "");

        var needles = new[] { Encoding.UTF8.GetBytes(marker), Encoding.UTF8.GetBytes("비밀문서"), Encoding.Unicode.GetBytes("비밀문서"), Encoding.UTF8.GetBytes("src3") };
        foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
        {
            Assert(!f.Contains("비밀문서") && !f.Contains("src3"), "디스크 파일명에 원래 이름 노출: " + f);
            var bytes = File.ReadAllBytes(f);
            foreach (var n in needles) Assert(IndexOf(bytes, n) < 0, "평문 노출: " + Path.GetFileName(f));
        }
    }

    static void ConvertInPlace()
    {
        var d = NewDir("v4");
        File.WriteAllText(Path.Combine(d, "root.txt"), "R");
        Directory.CreateDirectory(Path.Combine(d, "사진", "2026"));
        var img = Rand(500_000);
        File.WriteAllBytes(Path.Combine(d, "사진", "2026", "img.jpg"), img);
        Directory.CreateDirectory(Path.Combine(d, "빈폴더"));

        Assert(Vault.FindPlainFiles(d).Count == 2, "평문 탐지 개수");
        using (var v = Vault.Create(d, "pw", Iter))
        {
            Assert(v.FindPlainFiles().Count == 2, "생성 후 평문 탐지(안내파일 제외)");
            v.EncryptPlainFilesInPlace();
            Assert(v.FindPlainFiles().Count == 0, "변환 후 평문 남음");
        }
        Assert(!File.Exists(Path.Combine(d, "root.txt")), "원본 root.txt 남음");
        Assert(!Directory.Exists(Path.Combine(d, "사진")), "빈 원본 폴더 남음");
        using (var v = Vault.Open(d, "pw"))
        {
            Assert(Encoding.UTF8.GetString(v.ReadAll(v.Get("root.txt"))) == "R", "root 내용");
            Assert(v.ReadAll(v.Get("사진/2026/img.jpg")).SequenceEqual(img), "img 내용");
        }
    }

    static void ConvertDoesNotOverwrite()
    {
        var d = NewDir("v5");
        using (var v = Vault.Create(d, "pw", Iter))
        {
            var s = Path.Combine(_work, "dup.txt");
            File.WriteAllText(s, "IN_VAULT");
            v.AddPaths(new[] { s }, "");
            File.WriteAllText(Path.Combine(d, "dup.txt"), "PLAIN_NEW"); // 탐색기로 같은 이름을 넣은 상황
            v.EncryptPlainFilesInPlace();
            Assert(Encoding.UTF8.GetString(v.ReadAll(v.Get("dup.txt"))) == "IN_VAULT", "기존 금고 파일이 덮어써짐");
            Assert(Encoding.UTF8.GetString(v.ReadAll(v.Get("dup (2).txt"))) == "PLAIN_NEW", "새 파일 (2) 없음");
        }
    }

    static void RenameAndDelete()
    {
        var src = NewDir("src6");
        Directory.CreateDirectory(Path.Combine(src, "inner"));
        File.WriteAllText(Path.Combine(src, "inner", "x.txt"), "X");
        var d = NewDir("v6");
        using (var v = Vault.Create(d, "pw", Iter))
        {
            v.AddPaths(new[] { src }, "");
            v.Rename(v.Get("src6"), "renamed");
            Assert(v.Get("renamed/inner/x.txt") != null && v.Get("src6/inner/x.txt") == null, "하위 경로 갱신");
            int blobsBefore = Directory.GetFiles(Path.Combine(d, "data"), "*.fl", SearchOption.AllDirectories).Length;
            v.Delete(new[] { v.Get("renamed") });
            int blobsAfter = Directory.GetFiles(Path.Combine(d, "data"), "*.fl", SearchOption.AllDirectories).Length;
            Assert(v.FileCount == 0 && v.List("").Count == 0, "삭제 후 남음");
            Assert(blobsAfter == blobsBefore - 1, "블롭 삭제 안 됨");
            Throws<ArgumentException>(() => v.CreateFolder("", "a/b"), "잘못된 이름 허용");
        }
        using (var v = Vault.Open(d, "pw")) Assert(v.List("").Count == 0, "재오픈 시 삭제 미반영");
    }

    static void ReplaceAndReopen()
    {
        var d = NewDir("v7");
        var s = Path.Combine(_work, "memo.txt");
        File.WriteAllText(s, "v1");
        using (var v = Vault.Create(d, "pw", Iter))
        {
            v.AddPaths(new[] { s }, "");
            v.ReplaceContent("memo.txt", Encoding.UTF8.GetBytes("v2 from memory"));
            File.WriteAllText(s, "v3 from file");
            v.ReplaceContent("memo.txt", s);
            Assert(Directory.GetFiles(Path.Combine(d, "data"), "*.fl", SearchOption.AllDirectories).Length == 1, "옛 블롭 남음");
        }
        using (var v = Vault.Open(d, "pw"))
            Assert(Encoding.UTF8.GetString(v.ReadAll(v.Get("memo.txt"))) == "v3 from file", "교체 내용 유지");
    }

    static void ChangePassword()
    {
        var d = NewDir("v8");
        var s = Path.Combine(_work, "p.txt");
        File.WriteAllText(s, "P");
        using (var v = Vault.Create(d, "old", Iter))
        {
            v.AddPaths(new[] { s }, "");
            Throws<WrongPasswordException>(() => v.ChangePassword("nope", "new"), "기존 비번 확인 안 함");
            v.ChangePassword("old", "new");
        }
        Throws<WrongPasswordException>(() => Vault.Open(d, "old"), "옛 비번 통과");
        using (var v = Vault.Open(d, "new"))
            Assert(Encoding.UTF8.GetString(v.ReadAll(v.Get("p.txt"))) == "P", "새 비번으로 내용");
    }

    static void IndexRecovery()
    {
        var d = NewDir("v9");
        var s = Path.Combine(_work, "r.txt");
        File.WriteAllText(s, "R");
        using (var v = Vault.Create(d, "pw", Iter))
        {
            v.AddPaths(new[] { s }, "");
            v.CreateFolder("", "f"); // 한 번 더 저장 → .bak 에 r.txt 포함된 버전
        }
        var idx = Path.Combine(d, Vault.IndexFile);
        Assert(File.Exists(idx + ".bak"), ".bak 없음");
        // index.dat 를 반쯤 쓰다 만 상태로 손상
        var b = File.ReadAllBytes(idx);
        File.WriteAllBytes(idx, b.Take(b.Length / 2).ToArray());
        using (var v = Vault.Open(d, "pw"))
            Assert(v.Get("r.txt") != null, "백업으로 복구 실패");
    }

    static void EdgeNames()
    {
        var src = NewDir("src10");
        var names = new[] { "빈파일.txt", "공백 있는 이름 (1).docx", new string('가', 120) + ".txt", "emoji_😀.txt" };
        foreach (var n in names) File.WriteAllBytes(Path.Combine(src, n), n == "빈파일.txt" ? new byte[0] : Encoding.UTF8.GetBytes(n));
        var d = NewDir("v10");
        using (var v = Vault.Create(d, "한글비밀번호!@#", Iter)) v.AddPaths(new[] { src }, "");
        using (var v = Vault.Open(d, "한글비밀번호!@#"))
            foreach (var n in names)
            {
                var e = v.Get("src10/" + n);
                Assert(e != null, "없음: " + n);
                var data = v.ReadAll(e);
                Assert(n == "빈파일.txt" ? data.Length == 0 : Encoding.UTF8.GetString(data) == n, "내용: " + n);
            }
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
