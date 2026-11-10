using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FolderLock
{
    internal sealed class Entry
    {
        public string Path;      // 금고 내부 경로, '/' 구분. 루트 = ""
        public bool IsDir;
        public string BlobId;    // 파일만
        public long Size;
        public DateTime ModifiedUtc;

        public string Name => Vault.NameOf(Path);
        public string Parent => Vault.ParentOf(Path);
    }

    /// <summary>
    /// 금고 폴더 구조
    ///   vault.lock   : 솔트·반복횟수 + 비밀번호로 감싼 마스터 키
    ///   index.dat    : 암호화된 목록 (파일명·폴더 구조·크기·날짜)
    ///   data\xx\*.fl : 암호화된 파일 본문 (이름은 무작위)
    ///   안내 txt     : 이 폴더가 금고라는 표시 (평문, 비밀 없음)
    /// USB 안에는 평문 파일이 절대 저장되지 않는다.
    /// </summary>
    internal sealed class Vault : IDisposable
    {
        public const string LockFile = "vault.lock";
        public const string IndexFile = "index.dat";
        public const string DataDir = "data";

        /// <summary>새로 만드는 안내 파일 이름 (언어별). 예전 이름도 금고 파일로 인식한다.</summary>
        public static string ReadmeFile => L.T("[잠긴 금고] FolderLock.exe로 여세요.txt", "[LOCKED VAULT] open with FolderLock.exe.txt");
        private static readonly HashSet<string> KnownReadmeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "[잠긴 금고] FolderLock.exe로 여세요.txt", "[LOCKED VAULT] open with FolderLock.exe.txt" };

        private static readonly byte[] LockMagic = Encoding.ASCII.GetBytes("FLV1");
        private static readonly byte[] IndexMagic = Encoding.ASCII.GetBytes("FLI1");
        private const int FormatVersion = 1;
        private const string KeyContext = "vault-key";
        private const string IndexContext = "index";
        private const int IoBuffer = 1 << 20; // USB 처리량을 위해 1MB 버퍼

        /// <summary>대량 작업 중 목록(index.dat) 저장 간격. 파일 수가 아니라 시간 기준 → 4만 개여도 저장 횟수가 폭증하지 않음.</summary>
        public static TimeSpan SaveInterval = TimeSpan.FromSeconds(3);

        private static readonly HashSet<string> IgnoredPlainNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "desktop.ini", "Thumbs.db", ".DS_Store" };

        public string Root { get; }
        public string DisplayName => System.IO.Path.GetFileName(Root.TrimEnd('\\', '/'));

        private KeyPair _keys;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        private Vault(string root, KeyPair keys)
        {
            Root = System.IO.Path.GetFullPath(root);
            _keys = keys;
        }

        // ======================= 생성 / 열기 =======================

        public static bool IsVault(string dir) => File.Exists(System.IO.Path.Combine(dir, LockFile));

        public static Vault Create(string dir, string password, int iterations = Crypto.DefaultIterations)
        {
            if (IsVault(dir)) throw new InvalidOperationException(L.T("이미 금고인 폴더입니다.", "This folder is already a vault."));
            Directory.CreateDirectory(dir);
            var mk = Crypto.RandomBytes(64);
            try
            {
                WriteLockFile(dir, mk, password, iterations);
                var v = new Vault(dir, new KeyPair(mk));
                Directory.CreateDirectory(System.IO.Path.Combine(v.Root, DataDir));
                v.Save();
                v.WriteReadme();
                return v;
            }
            finally { Array.Clear(mk, 0, mk.Length); }
        }

        public static Vault Open(string dir, string password)
        {
            byte[] mk = UnwrapMasterKey(dir, password);
            try
            {
                var v = new Vault(dir, new KeyPair(mk));
                v.Load();
                return v;
            }
            finally { Array.Clear(mk, 0, mk.Length); }
        }

        public void ChangePassword(string oldPassword, string newPassword)
        {
            byte[] mk = UnwrapMasterKey(Root, oldPassword); // 기존 비밀번호 확인 겸 마스터 키 획득
            try { WriteLockFile(Root, mk, newPassword, Crypto.DefaultIterations); }
            finally { Array.Clear(mk, 0, mk.Length); }
        }

        private static void WriteLockFile(string dir, byte[] mk, string password, int iterations)
        {
            var salt = Crypto.RandomBytes(16);
            byte[] wrapped;
            using (var kek = Crypto.DeriveFromPassword(password, salt, iterations))
                wrapped = Crypto.EncryptBytes(mk, kek, KeyContext);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(LockMagic);
                w.Write(FormatVersion);
                w.Write(iterations);
                w.Write(salt);
                w.Write(wrapped.Length);
                w.Write(wrapped);
                w.Flush();
                AtomicWrite(System.IO.Path.Combine(dir, LockFile), ms.ToArray());
            }
        }

        private static byte[] UnwrapMasterKey(string dir, string password)
        {
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(dir, LockFile));
            var corrupt = L.T("금고 파일이 손상되었습니다.", "The vault file is corrupted.");
            using (var r = new BinaryReader(new MemoryStream(bytes)))
            {
                if (!r.ReadBytes(4).SequenceEqual(LockMagic)) throw new InvalidDataException(L.T("금고 파일 형식이 아닙니다.", "Not a vault file."));
                int ver = r.ReadInt32();
                if (ver != FormatVersion) throw new InvalidDataException(L.T("지원하지 않는 금고 버전입니다: ", "Unsupported vault version: ") + ver);
                int iter = r.ReadInt32();
                if (iter < 10_000 || iter > 50_000_000) throw new InvalidDataException(corrupt);
                var salt = r.ReadBytes(16);
                int len = r.ReadInt32();
                if (len <= 0 || len > 4096) throw new InvalidDataException(corrupt);
                var wrapped = r.ReadBytes(len);
                using (var kek = Crypto.DeriveFromPassword(password, salt, iter))
                {
                    try { return Crypto.DecryptBytes(wrapped, kek, KeyContext); }
                    catch (TamperedDataException) { throw new WrongPasswordException(); }
                }
            }
        }

        private void WriteReadme()
        {
            if (KnownReadmeNames.Any(n => File.Exists(System.IO.Path.Combine(Root, n)))) return;
            File.WriteAllText(System.IO.Path.Combine(Root, ReadmeFile),
                "이 폴더는 FolderLock 으로 잠긴 금고입니다.\r\n" +
                "안의 파일은 모두 AES-256 으로 암호화되어 있어 탐색기로는 열 수 없습니다.\r\n" +
                "열기: FolderLock.exe 실행 → 비밀번호 입력\r\n" +
                "주의: vault.lock / index.dat / data 폴더를 지우거나 고치면 복구할 수 없습니다.\r\n\r\n" +
                "This folder is a vault locked with FolderLock.\r\n" +
                "All files inside are encrypted with AES-256 and cannot be opened in Explorer.\r\n" +
                "To open: run FolderLock.exe and enter the password.\r\n" +
                "Warning: deleting or editing vault.lock / index.dat / the data folder makes the vault unrecoverable.\r\n",
                new UTF8Encoding(true));
        }

        // ======================= 목록 저장 / 불러오기 =======================

        private string IndexPath => System.IO.Path.Combine(Root, IndexFile);

        private void Load()
        {
            Exception first = null;
            foreach (var candidate in new[] { IndexPath, IndexPath + ".bak", IndexPath + ".tmp" })
            {
                if (!File.Exists(candidate)) continue;
                try { LoadFrom(candidate); return; }
                catch (Exception ex) { if (first == null) first = ex; }
            }
            if (first != null)
                throw new InvalidDataException(L.T("파일 목록(index.dat)이 손상되었습니다: ", "The file list (index.dat) is corrupted: ") + first.Message, first);
            // 목록이 아예 없으면 빈 금고로 시작
            _entries.Clear();
        }

        private void LoadFrom(string path)
        {
            var plain = Crypto.DecryptBytes(File.ReadAllBytes(path), _keys, IndexContext);
            var list = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            using (var r = new BinaryReader(new MemoryStream(plain), Encoding.UTF8))
            {
                if (!r.ReadBytes(4).SequenceEqual(IndexMagic)) throw new InvalidDataException("bad index");
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var e = new Entry
                    {
                        IsDir = r.ReadBoolean(),
                        Path = r.ReadString(),
                        BlobId = r.ReadString(),
                        Size = r.ReadInt64(),
                        ModifiedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
                    };
                    list[e.Path] = e;
                }
            }
            Array.Clear(plain, 0, plain.Length);
            _entries.Clear();
            foreach (var kv in list) _entries[kv.Key] = kv.Value;
        }

        public void Save()
        {
            lock (_sync)
            {
                byte[] plain;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(IndexMagic);
                    w.Write(_entries.Count);
                    foreach (var e in _entries.Values)
                    {
                        w.Write(e.IsDir);
                        w.Write(e.Path);
                        w.Write(e.BlobId ?? "");
                        w.Write(e.Size);
                        w.Write(e.ModifiedUtc.Ticks);
                    }
                    w.Flush();
                    plain = ms.ToArray();
                }
                var blob = Crypto.EncryptBytes(plain, _keys, IndexContext);
                Array.Clear(plain, 0, plain.Length);
                AtomicWrite(IndexPath, blob);
            }
        }

        /// <summary>tmp 에 완전히 쓰고 디스크에 flush 한 뒤 교체 → 중간에 USB 를 뽑아도 이전/새 버전 중 하나는 온전.</summary>
        private static void AtomicWrite(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, path + ".bak", true); return; }
                catch (IOException) { }
                catch (PlatformNotSupportedException) { }
                File.Copy(path, path + ".bak", true);
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        // ======================= 경로 유틸 =======================

        public static string NameOf(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }

        public static string ParentOf(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? "" : path.Substring(0, i);
        }

        public static string Combine(string folder, string name) => string.IsNullOrEmpty(folder) ? name : folder + "/" + name;

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name == "." || name == "..") return false;
            return name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && name.IndexOf('/') < 0;
        }

        private static string InvalidNameMessage => L.T("사용할 수 없는 이름입니다.", "That name is not allowed.");

        private string UniqueName(string folder, string name)
        {
            if (!_entries.ContainsKey(Combine(folder, name))) return name;
            string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
            for (int n = 2; ; n++)
            {
                var cand = $"{stem} ({n}){ext}";
                if (!_entries.ContainsKey(Combine(folder, cand))) return cand;
            }
        }

        private string BlobPath(string id) => System.IO.Path.Combine(Root, DataDir, id.Substring(0, 2), id + ".fl");

        // ======================= 조회 =======================

        public List<Entry> List(string folder)
        {
            lock (_sync)
                return _entries.Values.Where(e => string.Equals(e.Parent, folder, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public bool FolderExists(string folder) { lock (_sync) return folder == "" || (_entries.TryGetValue(folder, out var e) && e.IsDir); }

        public Entry Get(string path) { lock (_sync) return _entries.TryGetValue(path, out var e) ? e : null; }

        public int FileCount { get { lock (_sync) return _entries.Values.Count(e => !e.IsDir); } }

        /// <summary>폴더 아래 모든 파일 (자기 자신이 파일이면 자신).</summary>
        public List<Entry> FilesUnder(Entry e)
        {
            if (!e.IsDir) return new List<Entry> { e };
            lock (_sync)
                return _entries.Values.Where(x => !x.IsDir && x.Path.StartsWith(e.Path + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // ======================= 넣기 =======================

        /// <summary>
        /// 평문 파일 하나를 암호화해서 넣는다. 반환: 새 항목. (Save 는 호출자가)
        /// 덮어쓴 옛 블롭 ID 는 obsoleteBlobs 에 담는다 → 반드시 Save() 이후에 삭제해야 안전.
        /// </summary>
        private Entry ImportFileNoSave(string srcPath, string folder, string name, bool unique, List<string> obsoleteBlobs, OpProgress p = null)
        {
            var info = new FileInfo(srcPath);
            using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, IoBuffer))
            using (var src = new ProgressStream(fs, p))
                return ImportStreamNoSave(src, info.Length, info.LastWriteTimeUtc, folder, name, unique, obsoleteBlobs);
        }

        private Entry ImportStreamNoSave(Stream src, long size, DateTime modifiedUtc, string folder, string name, bool unique, List<string> obsoleteBlobs)
        {
            var id = Crypto.RandomId();
            var dst = BlobPath(id);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
            var tmp = dst + ".tmp";
            try
            {
                using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, IoBuffer))
                {
                    Crypto.Encrypt(src, outFs, _keys, id);
                    outFs.Flush(true);
                }
                File.Move(tmp, dst);
            }
            catch
            {
                // 취소·오류 시 쓰다 만 블롭 정리
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }

            lock (_sync)
            {
                EnsureFolders(folder);
                if (unique) name = UniqueName(folder, name);
                var path = Combine(folder, name);
                if (_entries.TryGetValue(path, out var old))
                {
                    if (old.IsDir) throw new IOException(L.T("같은 이름의 폴더가 있습니다: ", "A folder with the same name exists: ") + path);
                    if (!string.IsNullOrEmpty(old.BlobId)) obsoleteBlobs.Add(old.BlobId);
                }
                var e = new Entry { Path = path, IsDir = false, BlobId = id, Size = size, ModifiedUtc = modifiedUtc };
                _entries[path] = e;
                return e;
            }
        }

        private void EnsureFolders(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (_entries.TryGetValue(folder, out var e))
            {
                if (!e.IsDir) throw new IOException(L.T("같은 이름의 파일이 있습니다: ", "A file with the same name exists: ") + folder);
                return;
            }
            EnsureFolders(ParentOf(folder));
            _entries[folder] = new Entry { Path = folder, IsDir = true, BlobId = "", ModifiedUtc = DateTime.UtcNow };
        }

        public string CreateFolder(string parent, string name)
        {
            if (!IsValidName(name)) throw new ArgumentException(InvalidNameMessage);
            lock (_sync)
            {
                name = UniqueName(parent, name);
                EnsureFolders(Combine(parent, name));
            }
            Save();
            return Combine(parent, name);
        }

        /// <summary>
        /// 외부 파일/폴더를 금고의 folder 안에 넣는다. 원본은 건드리지 않는다.
        /// 취소하면 그때까지 넣은 것은 저장하고 OperationCanceledException 을 던진다.
        /// </summary>
        public void AddPaths(IEnumerable<string> sources, string folder, OpProgress p = null)
        {
            p = p ?? OpProgress.None;
            var jobs = new List<(string src, string dstFolder, string name, long size)>();
            var dirsToCreate = new List<string>();
            foreach (var s in sources)
            {
                p.Check();
                if (Directory.Exists(s))
                {
                    string top;
                    lock (_sync) top = UniqueName(folder, System.IO.Path.GetFileName(s.TrimEnd('\\')));
                    var topPath = Combine(folder, top);
                    dirsToCreate.Add(topPath);
                    var baseLen = s.TrimEnd('\\').Length + 1;
                    var di = new DirectoryInfo(s);
                    foreach (var d in di.EnumerateDirectories("*", SearchOption.AllDirectories))
                        dirsToCreate.Add(Combine(topPath, d.FullName.Substring(baseLen).Replace('\\', '/')));
                    foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        var rel = f.FullName.Substring(baseLen).Replace('\\', '/');
                        jobs.Add((f.FullName, Combine(topPath, ParentOf(rel)).TrimEnd('/'), NameOf(rel), f.Length));
                    }
                }
                else if (File.Exists(s))
                {
                    jobs.Add((s, folder, System.IO.Path.GetFileName(s), new FileInfo(s).Length));
                }
            }
            lock (_sync) foreach (var d in dirsToCreate) EnsureFolders(d);

            p.Begin(jobs.Count, jobs.Sum(j => j.size));
            var obsolete = new List<string>();
            var sinceSave = Stopwatch.StartNew();
            try
            {
                foreach (var j in jobs)
                {
                    p.StartFile(j.name);
                    ImportFileNoSave(j.src, j.dstFolder, j.name, unique: true, obsoleteBlobs: obsolete, p: p);
                    p.FileDone();
                    if (sinceSave.Elapsed >= SaveInterval) { Save(); sinceSave.Restart(); }
                }
            }
            finally
            {
                Save();
                foreach (var b in obsolete) TryDeleteBlob(b);
            }
        }

        // ======================= 기존 평문 파일 → 금고 (변환) =======================

        /// <summary>금고 폴더 안에 있는 (아직 암호화 안 된) 평문 파일 목록.</summary>
        public List<FileInfo> FindPlainFiles() => FindPlainFiles(Root);

        public static List<FileInfo> FindPlainFiles(string root)
        {
            var result = new List<FileInfo>();
            var rootInfo = new DirectoryInfo(root);
            var dataDir = System.IO.Path.Combine(rootInfo.FullName, DataDir);
            foreach (var f in rootInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (IsVaultFile(f.Name) || IgnoredPlainNames.Contains(f.Name)) continue;
                result.Add(f);
            }
            foreach (var d in rootInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(d.FullName, dataDir, StringComparison.OrdinalIgnoreCase)) continue;
                if ((d.Attributes & FileAttributes.System) != 0) continue; // System Volume Information 등
                foreach (var f in d.EnumerateFiles("*", SearchOption.AllDirectories))
                    if (!IgnoredPlainNames.Contains(f.Name)) result.Add(f);
            }
            return result;
        }

        private static bool IsVaultFile(string name) =>
            KnownReadmeNames.Contains(name) ||
            name.StartsWith(LockFile, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(IndexFile, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 평문 파일을 암호화해 넣고, 암호화본 검증 + 목록 저장이 끝난 것만 원본 삭제.
        /// 중간에 끊기거나 취소해도 원본이 먼저 사라지는 일은 없다 (남은 것은 다음에 이어서).
        /// </summary>
        public void EncryptPlainFilesInPlace(OpProgress p = null)
        {
            p = p ?? OpProgress.None;
            var files = FindPlainFiles();
            p.Begin(files.Count, files.Sum(f => f.Length));
            var pending = new List<string>();   // 검증 끝, 목록 저장 대기 중인 원본
            var sinceSave = Stopwatch.StartNew();
            var rootLen = Root.TrimEnd('\\').Length + 1;
            try
            {
                foreach (var f in files)
                {
                    var rel = f.FullName.Substring(rootLen).Replace('\\', '/');
                    p.StartFile(rel);
                    // unique: true → 금고에 같은 이름이 있어도 덮어쓰지 않고 "이름 (2)" 로 넣는다
                    var e = ImportFileNoSave(f.FullName, ParentOf(rel), NameOf(rel), unique: true, obsoleteBlobs: new List<string>(), p: p);
                    if (!Crypto.Verify(BlobPath(e.BlobId), _keys, e.BlobId))
                        throw new IOException(L.T("암호화 검증 실패 (원본은 남겨둡니다): ", "Encryption verification failed (original kept): ") + rel);
                    pending.Add(f.FullName);
                    p.FileDone();
                    if (sinceSave.Elapsed >= SaveInterval)
                    {
                        Save();
                        DeleteOriginals(pending);
                        sinceSave.Restart();
                    }
                }
            }
            finally
            {
                // 정상 종료·취소·오류 모두: 검증까지 끝난 것만 목록 저장 후 원본 삭제
                Save();
                DeleteOriginals(pending);
                RemoveEmptyPlainDirs();
            }
        }

        private static void DeleteOriginals(List<string> files)
        {
            foreach (var f in files)
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            files.Clear();
        }

        private void RemoveEmptyPlainDirs()
        {
            var dataDir = System.IO.Path.Combine(Root, DataDir);
            List<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(Root, "*", SearchOption.TopDirectoryOnly)
                    .Where(d => !string.Equals(d, dataDir, StringComparison.OrdinalIgnoreCase)
                             && (new DirectoryInfo(d).Attributes & FileAttributes.System) == 0)
                    .SelectMany(d => new[] { d }.Concat(Directory.EnumerateDirectories(d, "*", SearchOption.AllDirectories)))
                    .OrderByDescending(d => d.Length).ToList();
            }
            catch { return; }
            foreach (var d in dirs)
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // ======================= 꺼내기 / 읽기 =======================

        public void ExtractTo(Entry e, string destPath, OpProgress p = null)
        {
            if (e.IsDir) throw new InvalidOperationException();
            var tmp = destPath + ".fltmp";
            try
            {
                using (var src = new FileStream(BlobPath(e.BlobId), FileMode.Open, FileAccess.Read, FileShare.Read, IoBuffer))
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, IoBuffer))
                using (var dst = new ProgressStream(fs, p))
                    Crypto.Decrypt(src, dst, _keys, e.BlobId);
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                try { File.SetLastWriteTimeUtc(destPath, e.ModifiedUtc); } catch { }
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        /// <summary>여러 항목(파일·폴더)을 destDir 아래로 꺼내기. 진행률은 바이트 기준.</summary>
        public void ExtractMany(IList<Entry> items, string destDir, OpProgress p = null)
        {
            p = p ?? OpProgress.None;
            var plan = new List<(Entry file, string target)>();
            foreach (var it in items)
            {
                if (!it.IsDir) { plan.Add((it, System.IO.Path.Combine(destDir, it.Name))); continue; }
                var baseDir = System.IO.Path.Combine(destDir, it.Name);
                Directory.CreateDirectory(baseDir);
                List<Entry> all;
                lock (_sync)
                    all = _entries.Values.Where(x => x.Path.StartsWith(it.Path + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var d in all.Where(x => x.IsDir))
                    Directory.CreateDirectory(System.IO.Path.Combine(baseDir, d.Path.Substring(it.Path.Length + 1).Replace('/', '\\')));
                foreach (var f in all.Where(x => !x.IsDir))
                    plan.Add((f, System.IO.Path.Combine(baseDir, f.Path.Substring(it.Path.Length + 1).Replace('/', '\\'))));
            }
            p.Begin(plan.Count, plan.Sum(x => x.file.Size));
            foreach (var (file, target) in plan)
            {
                p.StartFile(file.Name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                ExtractTo(file, target, p);
                p.FileDone();
            }
        }

        /// <summary>폴더째로 꺼내기. destDir 아래에 폴더 이름으로 생성.</summary>
        public void ExtractFolderTo(Entry dir, string destDir, OpProgress p = null) => ExtractMany(new[] { dir }, destDir, p);

        public byte[] ReadAll(Entry e, OpProgress p = null)
        {
            using (var src = new FileStream(BlobPath(e.BlobId), FileMode.Open, FileAccess.Read, FileShare.Read, IoBuffer))
            using (var ms = new MemoryStream((int)Math.Min(e.Size + 16, int.MaxValue)))
            using (var dst = new ProgressStream(ms, p))
            {
                Crypto.Decrypt(src, dst, _keys, e.BlobId);
                return ms.ToArray();
            }
        }

        // ======================= 수정 =======================

        /// <summary>임시로 열어 고친 파일 내용으로 교체 (새 블롭 작성 → 목록 저장 → 옛 블롭 삭제).</summary>
        public void ReplaceContent(string path, string srcFile)
        {
            Entry old = Get(path) ?? throw new FileNotFoundException(path);
            var obsolete = new List<string>();
            ImportFileNoSave(srcFile, old.Parent, old.Name, unique: false, obsoleteBlobs: obsolete);
            Save();                                   // 새 블롭을 가리키는 목록을 먼저 저장하고
            foreach (var b in obsolete) TryDeleteBlob(b); // 그 다음 옛 블롭 삭제
        }

        /// <summary>메모리 내용으로 교체 (텍스트 편집기 저장용 — 디스크에 평문을 쓰지 않음).</summary>
        public void ReplaceContent(string path, byte[] content)
        {
            Entry old = Get(path) ?? throw new FileNotFoundException(path);
            var obsolete = new List<string>();
            using (var ms = new MemoryStream(content, false))
                ImportStreamNoSave(ms, content.Length, DateTime.UtcNow, old.Parent, old.Name, unique: false, obsoleteBlobs: obsolete);
            Save();
            foreach (var b in obsolete) TryDeleteBlob(b);
        }

        public void Rename(Entry e, string newName)
        {
            if (!IsValidName(newName)) throw new ArgumentException(InvalidNameMessage);
            lock (_sync)
            {
                var oldPath = e.Path; // e 자신도 아래에서 경로가 바뀌므로 미리 보관
                var newPath = Combine(e.Parent, newName);
                if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return;
                if (_entries.ContainsKey(newPath) && !string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L.T("같은 이름이 이미 있습니다.", "An item with that name already exists."));
                var moved = _entries.Values.Where(x => x.Path.Equals(oldPath, StringComparison.OrdinalIgnoreCase)
                                                    || x.Path.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var x in moved) _entries.Remove(x.Path);
                foreach (var x in moved)
                {
                    x.Path = newPath + x.Path.Substring(oldPath.Length);
                    _entries[x.Path] = x;
                }
            }
            Save();
        }

        public void Delete(IEnumerable<Entry> items, OpProgress p = null)
        {
            p = p ?? OpProgress.None;
            var blobs = new List<string>();
            lock (_sync)
            {
                foreach (var e in items.ToList())
                {
                    var victims = _entries.Values.Where(x => x.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase)
                                                          || x.Path.StartsWith(e.Path + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var v in victims)
                    {
                        _entries.Remove(v.Path);
                        if (!v.IsDir && !string.IsNullOrEmpty(v.BlobId)) blobs.Add(v.BlobId);
                    }
                }
            }
            Save();                       // 목록에서 먼저 빼고 (이후 취소돼도 금고 상태는 일관됨)
            p.Begin(blobs.Count, 0);
            foreach (var b in blobs)      // 그 다음 본문 삭제
            {
                TryDeleteBlob(b);
                p.FileDone();
            }
        }

        private void TryDeleteBlob(string id)
        {
            try { File.Delete(BlobPath(id)); } catch { }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _keys?.Dispose();
                _keys = null;
                _entries.Clear();
            }
        }
    }
}
