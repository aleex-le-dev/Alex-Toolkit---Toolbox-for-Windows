<#
  Alex Toolkit - Forcer la suppression (menu contextuel)
  Supprime définitivement des fichiers ou dossiers récalcitrants :
    - ferme les programmes et services qui les verrouillent (sauf processus vitaux de Windows) ;
    - passe outre les autorisations (privilèges Sauvegarde / Restauration, propriétaire = Administrateurs) ;
    - retire les attributs lecture seule / caché / système, gère les chemins longs ;
    - supprime les liens et jonctions SANS toucher à leur cible ;
    - planifie au prochain redémarrage ce qui résiste encore.
  Installé par Alex Toolkit dans C:\ProgramData\AlexToolkit (dossier modifiable par les administrateurs seulement).
  -Silent : sans interface ni élévation (automatisation et tests).
#>
param(
    [string]$Path,
    [string]$Paths64,
    [switch]$Silent,
    [string]$Snapshot,   # tests : capture la fenêtre en PNG puis ferme (sans élévation)
    [switch]$AutoRun     # tests : avec -Snapshot, lance la suppression et capture le résultat
)

$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ─────────────────────────────────────────────────────────────────────────────
# 1. Sans droits admin : regroupe la sélection multiple puis relance en admin (une seule invite UAC)
# ─────────────────────────────────────────────────────────────────────────────
if (-not $isAdmin -and -not $Silent -and -not $Snapshot) {
    if (-not $Path) { exit 0 }
    $queue = Join-Path $env:TEMP 'AlexToolkit_ForceDelete_queue.txt'
    for ($i = 0; $i -lt 50; $i++) {
        try { [IO.File]::AppendAllText($queue, $Path + "`n", [Text.Encoding]::UTF8); break } catch { Start-Sleep -Milliseconds 40 }
    }
    $mutex = New-Object Threading.Mutex($false, 'Local\AlexToolkitForceDelete')
    try { [void]$mutex.WaitOne(20000) } catch [Threading.AbandonedMutexException] { }
    $items = @()
    try {
        if (Test-Path -LiteralPath $queue) {
            # Explorer lance une instance par élément sélectionné : on attend qu'elles aient toutes écrit.
            $last = -1; $stable = 0
            while ($stable -lt 3) {
                Start-Sleep -Milliseconds 200
                $len = (Get-Item -LiteralPath $queue).Length
                if ($len -eq $last) { $stable++ } else { $stable = 0; $last = $len }
            }
            $items = @([IO.File]::ReadAllLines($queue, [Text.Encoding]::UTF8) | Where-Object { $_ } | Select-Object -Unique)
            Remove-Item -LiteralPath $queue -Force
        }
    } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
    if ($items.Count -eq 0) { exit 0 }   # déjà pris en charge par une autre instance
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(($items -join "`n")))
    try {
        Start-Process -FilePath "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -WindowStyle Hidden `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -Paths64 $enc"
    } catch { }   # invite UAC refusée : rien à faire
    exit 0
}

# ─────────────────────────────────────────────────────────────────────────────
# 2. Moteur natif (C#) : privilèges, déverrouillage, suppression, Restart Manager
# ─────────────────────────────────────────────────────────────────────────────
$cs = @'
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

public class AlexFDScan
{
    public bool Exists, IsDir, IsLink;
    public long Files, Dirs, Links, Bytes, Denied;
    public List<string> Sample = new List<string>();
}

public static class AlexFD
{
    public static ConcurrentQueue<string> Log = new ConcurrentQueue<string>();
    public static int Deleted;
    public static volatile bool Cancel;

    const uint DIR = 0x10, REPARSE = 0x400, NORMAL = 0x80, INVALID_ATTR = 0xFFFFFFFF;
    const uint DELETE = 0x10000, READ_CONTROL = 0x20000, WRITE_DAC = 0x40000, WRITE_OWNER = 0x80000;
    const uint READ_ATTR = 0x80, WRITE_ATTR = 0x100;
    // Ouverture native : contrairement à CreateFile, NtOpenFile ne demande QUE les droits indiqués
    // (CreateFile ajoute SYNCHRONIZE et FILE_READ_ATTRIBUTES, souvent refusés par une règle « Refuser tout »).
    const uint OPEN_FOR_BACKUP = 0x4000, OPEN_REPARSE_POINT = 0x200000, OBJ_CASE_INSENSITIVE = 0x40;
    static readonly IntPtr BAD = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FIND_DATA
    {
        public uint Attr;
        public System.Runtime.InteropServices.ComTypes.FILETIME C, A, W;
        public uint SizeHigh, SizeLow, R0, R1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alt;
    }
    [StructLayout(LayoutKind.Sequential)] struct BASIC_INFO { public long C, A, W, X; public uint Attr; }
    [StructLayout(LayoutKind.Sequential)] struct DISPO_EX { public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIV { public uint Count; public LUID Luid; public uint Attr; }
    [StructLayout(LayoutKind.Sequential)] struct RM_UNIQUE { public int Pid; public System.Runtime.InteropServices.ComTypes.FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_INFO
    {
        public RM_UNIQUE Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string App;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int Type; public uint Status; public uint Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFileExW(string n, int lvl, out FIND_DATA d, int op, IntPtr f, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextFileW(IntPtr h, out FIND_DATA d);
    [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string p);
    [StructLayout(LayoutKind.Sequential)] struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct OBJECT_ATTRIBUTES { public int Length; public IntPtr Root; public IntPtr Name; public uint Attributes; public IntPtr Sd; public IntPtr Qos; }
    [StructLayout(LayoutKind.Sequential)] struct IO_STATUS { public IntPtr Status; public IntPtr Info; }
    [DllImport("ntdll.dll")] static extern int NtOpenFile(out IntPtr h, uint access, ref OBJECT_ATTRIBUTES oa, out IO_STATUS io, uint share, uint options);
    [DllImport("ntdll.dll")] static extern int RtlNtStatusToDosError(int status);
    static int lastOpenError;

    // Ouvre fichier, dossier ou lien (sans le suivre) avec exactement les droits demandés.
    static IntPtr Open(string lp, uint access)
    {
        string nt = lp.StartsWith(@"\\?\UNC\") ? @"\??\UNC\" + lp.Substring(8) : @"\??\" + lp.Substring(4);
        IntPtr buf = Marshal.StringToHGlobalUni(nt);
        IntPtr us = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UNICODE_STRING)));
        try
        {
            UNICODE_STRING u = new UNICODE_STRING();
            u.Length = (ushort)(nt.Length * 2); u.MaximumLength = (ushort)(nt.Length * 2 + 2); u.Buffer = buf;
            Marshal.StructureToPtr(u, us, false);
            OBJECT_ATTRIBUTES oa = new OBJECT_ATTRIBUTES();
            oa.Length = Marshal.SizeOf(typeof(OBJECT_ATTRIBUTES)); oa.Name = us; oa.Attributes = OBJ_CASE_INSENSITIVE;
            IntPtr h; IO_STATUS io;
            int st = NtOpenFile(out h, access, ref oa, out io, 7, OPEN_FOR_BACKUP | OPEN_REPARSE_POINT);
            if (st < 0) { lastOpenError = RtlNtStatusToDosError(st); return BAD; }
            return h;
        }
        finally { Marshal.FreeHGlobal(us); Marshal.FreeHGlobal(buf); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(IntPtr h, int cls, ref BASIC_INFO i, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(IntPtr h, int cls, ref DISPO_EX i, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(IntPtr h, int cls, ref byte i, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileExW(string src, string dst, uint flags);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll.dll")] static extern int NtSetSecurityObject(IntPtr h, uint info, byte[] sd);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint acc, out IntPtr t);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValueW(string s, string n, out LUID l);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool all, ref TOKEN_PRIV s, uint len, IntPtr prev, IntPtr ret);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint h, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint h);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint h, uint nf, string[] f, uint na, IntPtr a, uint ns, string[] s);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint h, out uint needed, ref uint count, [In, Out] RM_INFO[] info, ref uint reasons);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    public static string Long(string p)
    {
        p = p.Replace('/', '\\');
        if (p.StartsWith(@"\\?\")) return p;
        if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
        return @"\\?\" + p;
    }
    public static string Short(string p)
    {
        if (p.StartsWith(@"\\?\UNC\")) return @"\\" + p.Substring(8);
        if (p.StartsWith(@"\\?\")) return p.Substring(4);
        return p;
    }
    public static string ErrMsg(int code) { return new Win32Exception(code).Message.Trim(); }

    public static void DarkTitle(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref v, 4);
    }

    // Active les privilèges qui permettent d'ignorer les autorisations (admin uniquement).
    public static int EnablePrivileges()
    {
        IntPtr tok; int ok = 0;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out tok)) return 0;
        foreach (string p in new string[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege", "SeSecurityPrivilege" })
        {
            LUID l;
            if (!LookupPrivilegeValueW(null, p, out l)) continue;
            TOKEN_PRIV tp = new TOKEN_PRIV(); tp.Count = 1; tp.Luid = l; tp.Attr = 2;
            if (AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0) ok++;
        }
        CloseHandle(tok);
        return ok;
    }

    public static bool Exists(string path) { return GetFileAttributesW(Long(path)) != INVALID_ATTR; }

    // ── Analyse (lecture seule) ──
    public static AlexFDScan Scan(string path)
    {
        AlexFDScan r = new AlexFDScan();
        string lp = Long(path);
        uint a = GetFileAttributesW(lp);
        if (a == INVALID_ATTR) return r;
        r.Exists = true; r.IsDir = (a & DIR) != 0; r.IsLink = (a & REPARSE) != 0;
        if (!r.IsDir || r.IsLink)
        {
            FIND_DATA d; IntPtr h = FindFirstFileExW(lp, 1, out d, 0, IntPtr.Zero, 0);
            if (h != BAD) { r.Bytes = ((long)d.SizeHigh << 32) + d.SizeLow; FindClose(h); }
            if (!r.IsDir) { r.Files = 1; AddSample(r, lp); }
            return r;
        }
        Stack<string> stack = new Stack<string>();
        stack.Push(lp);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            List<FIND_DATA> entries; int err = List(dir, out entries);
            if (err != 0) { r.Denied++; continue; }
            foreach (FIND_DATA e in entries)
            {
                string full = dir + "\\" + e.Name;
                if ((e.Attr & REPARSE) != 0) { r.Links++; continue; }
                if ((e.Attr & DIR) != 0) { r.Dirs++; stack.Push(full); continue; }
                r.Files++; r.Bytes += ((long)e.SizeHigh << 32) + e.SizeLow;
                AddSample(r, full);
            }
        }
        return r;
    }
    static void AddSample(AlexFDScan r, string lp)
    {
        string s = Short(lp);
        if (r.Sample.Count < 3000 && s.Length < 260) r.Sample.Add(s);
    }

    static int List(string dir, out List<FIND_DATA> entries)
    {
        entries = new List<FIND_DATA>();
        FIND_DATA d;
        IntPtr h = FindFirstFileExW(dir + "\\*", 1, out d, 0, IntPtr.Zero, 2);
        if (h == BAD)
        {
            int e = Marshal.GetLastWin32Error();
            return (e == 2 || e == 18) ? 0 : e;
        }
        try
        {
            do { if (d.Name != "." && d.Name != "..") entries.Add(d); } while (FindNextFileW(h, out d));
        }
        finally { FindClose(h); }
        return 0;
    }

    // ── Déverrouillage : propriétaire = Administrateurs, contrôle total (sans suivre les liens) ──
    static byte[] sdOwner, sdFile, sdDir;
    static byte[] Sd(string sddl) { RawSecurityDescriptor s = new RawSecurityDescriptor(sddl); byte[] b = new byte[s.BinaryLength]; s.GetBinaryForm(b, 0); return b; }
    public static bool Unlock(string lp, bool isDir)
    {
        if (sdOwner == null)
        {
            string me = WindowsIdentity.GetCurrent().User.Value;
            sdOwner = Sd("O:BA");
            sdFile = Sd("D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;" + me + ")");
            sdDir = Sd("D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;FA;;;" + me + ")");
        }
        IntPtr h = Open(lp, WRITE_OWNER);
        if (h != BAD) { NtSetSecurityObject(h, 1, sdOwner); CloseHandle(h); }
        h = Open(lp, WRITE_DAC);
        if (h == BAD) return false;
        int st = NtSetSecurityObject(h, 4 | 0x80000000, isDir ? sdDir : sdFile);
        CloseHandle(h);
        return st == 0;
    }

    // ── Suppression d'un élément (fichier, dossier vide ou lien) ──
    static int DeleteItem(string lp)
    {
        IntPtr h = Open(lp, DELETE | WRITE_ATTR);
        if (h == BAD) h = Open(lp, DELETE);
        if (h == BAD) return lastOpenError;
        try
        {
            BASIC_INFO bi = new BASIC_INFO(); bi.Attr = NORMAL;
            SetFileInformationByHandle(h, 0, ref bi, (uint)Marshal.SizeOf(typeof(BASIC_INFO)));
            DISPO_EX dx = new DISPO_EX(); dx.Flags = 0x1 | 0x2 | 0x10;   // DELETE | POSIX | IGNORE_READONLY
            if (SetFileInformationByHandle(h, 21, ref dx, 4)) return 0;
            int e = Marshal.GetLastWin32Error();
            if (e == 145) return 145;
            byte del = 1;
            if (SetFileInformationByHandle(h, 4, ref del, 1)) return 0;
            return Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }
    static int DeleteWithUnlock(string lp, bool isDir)
    {
        int r = DeleteItem(lp);
        if (r == 5 || r == 1314) { Unlock(lp, isDir); r = DeleteItem(lp); }
        return r;
    }

    // Supprime path (récursivement si dossier). Retourne les échecs « chemin|code|estDossier » dans l'ordre enfants → parents.
    public static List<string> DeleteTree(string path)
    {
        List<string> fails = new List<string>();
        string lp = Long(path);
        uint a = GetFileAttributesW(lp);
        if (a == INVALID_ATTR) return fails;
        bool isDir = (a & DIR) != 0, isLink = (a & REPARSE) != 0;
        if (isDir && !isLink) ProcessDir(lp, fails);
        if (Cancel) return fails;
        int r = DeleteWithUnlock(lp, isDir && !isLink);
        if (r == 0) Deleted++; else fails.Add(lp + "|" + r + "|" + (isDir ? 1 : 0));
        return fails;
    }
    static void ProcessDir(string dir, List<string> fails)
    {
        List<FIND_DATA> entries;
        int err = List(dir, out entries);
        if (err == 5) { Unlock(dir, true); err = List(dir, out entries); }
        if (err != 0) { Log.Enqueue("[!] Lecture impossible : " + Short(dir) + " (" + ErrMsg(err) + ")"); return; }
        foreach (FIND_DATA e in entries)
        {
            if (Cancel) return;
            string full = dir + "\\" + e.Name;
            bool d = (e.Attr & DIR) != 0, link = (e.Attr & REPARSE) != 0;
            if (d && !link) ProcessDir(full, fails);   // un lien n'est jamais parcouru : sa cible reste intacte
            if (Cancel) return;
            int r = DeleteWithUnlock(full, d && !link);
            if (r == 0) Deleted++; else fails.Add(full + "|" + r + "|" + (d ? 1 : 0));
        }
    }

    public static int ScheduleOnReboot(string lp)
    {
        return MoveFileExW(lp, null, 4) ? 0 : Marshal.GetLastWin32Error();
    }

    // ── Programmes et services qui verrouillent ces fichiers (Restart Manager) ──
    public static List<string> GetLockers(string[] files)
    {
        List<string> res = new List<string>();
        if (files == null || files.Length == 0) return res;
        uint h; StringBuilder key = new StringBuilder(64);
        if (RmStartSession(out h, 0, key) != 0) return res;
        try
        {
            for (int i = 0; i < files.Length; i += 500)
            {
                int n = Math.Min(500, files.Length - i);
                string[] chunk = new string[n]; Array.Copy(files, i, chunk, 0, n);
                RmRegisterResources(h, (uint)n, chunk, 0, IntPtr.Zero, 0, null);
            }
            uint needed = 0, count = 0, reasons = 0;
            int rc = RmGetList(h, out needed, ref count, null, ref reasons);
            if (rc == 234 && needed > 0)
            {
                RM_INFO[] arr = new RM_INFO[needed]; count = needed;
                rc = RmGetList(h, out needed, ref count, arr, ref reasons);
                if (rc == 0)
                    for (int i = 0; i < count; i++)
                        res.Add(arr[i].Process.Pid + "|" + arr[i].Type + "|" + arr[i].App + "|" + arr[i].Service);
            }
        }
        finally { RmEndSession(h); }
        return res;
    }
}
'@

# Compilation une seule fois, mise en cache à côté du script (dossier protégé) quand c'est possible.
if (-not ('AlexFD' -as [type])) {
    $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($cs))).Replace('-', '').Substring(0, 12)
    $dll = Join-Path $PSScriptRoot "ForceDelete_$hash.dll"
    try {
        if (-not (Test-Path -LiteralPath $dll)) {
            Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'ForceDelete_*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
            Add-Type -TypeDefinition $cs -OutputAssembly $dll -OutputType Library
        }
        Add-Type -LiteralPath $dll
    } catch {
        Add-Type -TypeDefinition $cs
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# 3. Éléments demandés et garde-fous
# ─────────────────────────────────────────────────────────────────────────────
$targets = @()
if ($Paths64) { $targets = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Paths64)) -split "`n" | Where-Object { $_ } }
elseif ($Path) { $targets = @($Path) }
if ($targets.Count -eq 0) { exit 0 }

function Test-Under([string]$p, [string]$parent) {
    $parent = $parent.TrimEnd('\')
    return $p.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Test-Same([string]$a, [string]$b) { return [string]::Equals($a.TrimEnd('\'), $b.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) }

$selfDir = $PSScriptRoot
$sysDrive = $env:SystemDrive
$protectedExact = New-Object Collections.Generic.List[string]
foreach ($p in @("$sysDrive\Users", "$sysDrive\Users\Public", "$sysDrive\Users\Default", "$sysDrive\Users\Default User", "$sysDrive\Users\All Users",
                 $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramData, "$env:ProgramFiles\WindowsApps",
                 "$sysDrive\Boot", "$sysDrive\Recovery", "$sysDrive\EFI", "$sysDrive\Documents and Settings")) { if ($p) { $protectedExact.Add($p) } }
$profiles = @()
try { $profiles = @(Get-CimInstance Win32_UserProfile -ErrorAction Stop | ForEach-Object { $_.LocalPath } | Where-Object { $_ }) } catch { }
foreach ($pr in $profiles) {
    $protectedExact.Add($pr)
    foreach ($sub in 'AppData', 'AppData\Local', 'AppData\Roaming', 'AppData\LocalLow', 'Desktop', 'Documents', 'Downloads', 'Pictures', 'Music', 'Videos', 'OneDrive') { $protectedExact.Add("$pr\$sub") }
}

function Get-ProtectionReason([string]$full) {
    $f = $full.TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($f)
    if (-not $root -or (Test-Same $f $root)) { return "racine d'un lecteur" }
    $win = $env:windir.TrimEnd('\')
    if (Test-Same $f $win) { return 'dossier Windows' }
    if ((Test-Under $f $win) -and -not (Test-Under $f "$win\Temp")) { return 'fichier système de Windows' }
    foreach ($p in $protectedExact) { if (Test-Same $f $p) { return 'dossier essentiel de Windows ou du profil' } }
    $parent = [IO.Path]::GetDirectoryName($f)
    if ($parent -and (Test-Same $parent $root)) {
        $leaf = [IO.Path]::GetFileName($f)
        if (@('System Volume Information', '$Recycle.Bin', 'pagefile.sys', 'hiberfil.sys', 'swapfile.sys', 'bootmgr', 'BOOTNXT', 'DumpStack.log.tmp') -contains $leaf) { return 'élément système du lecteur' }
    }
    if ((Test-Same $f $selfDir) -or (Test-Under $selfDir $f) -or (Test-Under $f $selfDir)) { return "l'outil Forcer la suppression lui-même" }
    return $null
}

function Format-Size([double]$b) {
    if ($b -ge 1GB) { return '{0:N1} Go' -f ($b / 1GB) }
    if ($b -ge 1MB) { return '{0:N1} Mo' -f ($b / 1MB) }
    if ($b -ge 1KB) { return '{0:N0} Ko' -f ($b / 1KB) }
    return '{0:N0} octets' -f $b
}

[void][AlexFD]::EnablePrivileges()

$items = @()
foreach ($t in $targets) {
    $full = $t
    try { $full = [IO.Path]::GetFullPath($t) } catch { }
    $full = $full.TrimEnd('\')
    $reason = Get-ProtectionReason $full
    $scan = $null
    if (-not $reason) {
        $scan = [AlexFD]::Scan($full)
        if (-not $scan.Exists) { $reason = 'introuvable (déjà supprimé ?)' }
    }
    $detail = ''
    if ($scan -and $scan.Exists) {
        if ($scan.IsLink) { $detail = 'Lien / jonction : seul le lien sera supprimé, pas sa cible' }
        elseif ($scan.IsDir) {
            $detail = '{0:N0} fichier(s) · {1:N0} sous-dossier(s) · {2}' -f $scan.Files, $scan.Dirs, (Format-Size $scan.Bytes)
            if ($scan.Links -gt 0) { $detail += (' · {0:N0} lien(s) (cibles conservées)' -f $scan.Links) }
            if ($scan.Denied -gt 0) { $detail += (' · {0:N0} dossier(s) verrouillé(s), débloqué(s) à la suppression' -f $scan.Denied) }
        }
        else { $detail = Format-Size $scan.Bytes }
    }
    $items += [pscustomobject]@{ Path = $full; Reason = $reason; Scan = $scan; Detail = $detail }
}
$valid = @($items | Where-Object { -not $_.Reason })
$scanByPath = @{}
foreach ($v in $valid) { $scanByPath[$v.Path] = $v.Scan }

# Programmes / services qui bloquent : Restart Manager + exécutables lancés depuis ces dossiers
function Get-Blockers($paths) {
    $files = New-Object Collections.Generic.List[string]
    foreach ($p in $paths) {
        $s = $scanByPath[$p]; if (-not $s) { $s = [AlexFD]::Scan($p) }
        foreach ($f in $s.Sample) { if ($files.Count -lt 3000) { $files.Add($f) } }
    }
    $list = @{}
    foreach ($l in [AlexFD]::GetLockers($files.ToArray())) {
        $parts = $l -split '\|', 4
        $procId = [int]$parts[0]
        if ($procId -eq $PID) { continue }
        $list["$procId|$($parts[3])"] = [pscustomobject]@{ Pid = $procId; Type = [int]$parts[1]; Name = $parts[2]; Service = $parts[3] }
    }
    try {
        foreach ($proc in Get-CimInstance Win32_Process -ErrorAction Stop) {
            if (-not $proc.ExecutablePath -or $proc.ProcessId -eq $PID) { continue }
            foreach ($p in $paths) {
                if ((Test-Same $proc.ExecutablePath $p) -or (Test-Under $proc.ExecutablePath $p)) {
                    $k = "$($proc.ProcessId)|"
                    if (-not $list.ContainsKey($k)) { $list[$k] = [pscustomobject]@{ Pid = [int]$proc.ProcessId; Type = 1; Name = $proc.Name; Service = '' } }
                }
            }
        }
    } catch { }
    return @($list.Values)
}

$critProc = @('system', 'idle', 'registry', 'smss', 'csrss', 'wininit', 'winlogon', 'services', 'lsass', 'lsaiso', 'svchost', 'fontdrvhost', 'dwm', 'memory compression', 'secure system', 'sihost', 'ctfmon')
$critSvc = @('RpcSs', 'RpcEptMapper', 'DcomLaunch', 'LSM', 'SamSs', 'PlugPlay', 'Power', 'BrokerInfrastructure', 'SystemEventsBroker', 'CoreMessagingRegistrar', 'gpsvc', 'ProfSvc', 'UserManager', 'EventLog', 'Schedule', 'StateRepository', 'Winmgmt', 'CryptSvc', 'Dhcp', 'Dnscache', 'nsi', 'BFE', 'mpssvc', 'WinDefend')

function Get-BlockerLabel($b) {
    $exe = ''
    try { $exe = (Get-Process -Id $b.Pid -ErrorAction Stop).ProcessName } catch { }
    if ($b.Service) {
        if ($critSvc -contains $b.Service) { return "Service « $($b.Name) » — service vital, ne sera pas arrêté" }
        return "Service « $($b.Name) » ($($b.Service)) — sera arrêté"
    }
    if ($exe -eq 'explorer' -or $b.Type -eq 4) { return "Explorateur Windows — sera redémarré automatiquement" }
    if ($b.Type -eq 1000 -or $critProc -contains $exe.ToLower()) { return "$($b.Name) (PID $($b.Pid)) — processus vital, ne sera pas fermé" }
    return "$($b.Name) (PID $($b.Pid)) — sera fermé"
}

# ─────────────────────────────────────────────────────────────────────────────
# 4. Traitement (exécuté en arrière-plan pour garder l'interface fluide)
# ─────────────────────────────────────────────────────────────────────────────
$work = {
    param($paths, $S, $critProc, $critSvc)
    function L($m) { [AlexFD]::Log.Enqueue($m) }
    function Test-Under([string]$p, [string]$parent) { return $p.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }
    function Test-Same([string]$a, [string]$b) { return [string]::Equals($a.TrimEnd('\'), $b.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) }
    function Stop-Blockers($blockers) {
        foreach ($b in $blockers) {
            if ($b.Service) {
                if ($critSvc -contains $b.Service) { L "[i] Service vital conservé : $($b.Name)"; continue }
                try { Stop-Service -Name $b.Service -Force -ErrorAction Stop; L "[OK] Service arrêté : $($b.Name)" }
                catch { L "[!] Service impossible à arrêter : $($b.Name) ($($_.Exception.Message))" }
                continue
            }
            $p = $null; try { $p = Get-Process -Id $b.Pid -ErrorAction Stop } catch { continue }
            $n = $p.ProcessName.ToLower()
            if ($n -eq 'explorer') { $S.Explorer = $true; continue }
            if ($b.Type -eq 1000 -or $critProc -contains $n) { L "[i] Processus vital conservé : $($b.Name)"; continue }
            try { Stop-Process -Id $b.Pid -Force -ErrorAction Stop; L "[OK] Programme fermé : $($b.Name) (PID $($b.Pid))" }
            catch { L "[!] Impossible de fermer $($b.Name) : $($_.Exception.Message)" }
        }
        if ($S.Explorer -and -not $S.ExplorerStopped) {
            $S.ExplorerStopped = $true
            try { Get-Process explorer -ErrorAction Stop | Stop-Process -Force; L "[OK] Explorateur Windows fermé (redémarrage automatique)" } catch { }
        }
        Start-Sleep -Milliseconds 600
    }

    try {
        if ($S.Blockers.Count -gt 0) { L "Fermeture de ce qui bloque…"; Stop-Blockers $S.Blockers }

        L "Prise de contrôle et suppression…"
        $fails = New-Object Collections.Generic.List[string]
        foreach ($p in $paths) {
            if ([AlexFD]::Cancel) { break }
            foreach ($f in [AlexFD]::DeleteTree($p)) { $fails.Add($f) }
        }

        # Deuxième passe : de nouveaux verrous ont pu apparaître (antivirus, indexation…)
        if ($fails.Count -gt 0 -and -not [AlexFD]::Cancel) {
            $lockedFiles = @($fails | ForEach-Object { [AlexFD]::Short(($_ -split '\|')[0]) } | Where-Object { $_.Length -lt 260 })
            $more = @()
            foreach ($l in [AlexFD]::GetLockers($lockedFiles)) {
                $pp = $l -split '\|', 4
                if ([int]$pp[0] -ne $PID) { $more += [pscustomobject]@{ Pid = [int]$pp[0]; Type = [int]$pp[1]; Name = $pp[2]; Service = $pp[3] } }
            }
            if ($more.Count -gt 0) { L "Nouveaux verrous détectés : fermeture…"; Stop-Blockers $more }
            Start-Sleep -Milliseconds 500
            $retry = New-Object Collections.Generic.List[string]
            foreach ($p in $paths) { foreach ($f in [AlexFD]::DeleteTree($p)) { $retry.Add($f) } }
            $fails = $retry
        }

        # Ce qui résiste encore : suppression au prochain redémarrage
        $pending = 0; $errors = New-Object Collections.Generic.List[string]
        foreach ($f in $fails) {
            $parts = $f -split '\|'
            $lp = $parts[0]; $code = [int]$parts[1]
            if ([AlexFD]::Cancel) { break }
            if ([AlexFD]::ScheduleOnReboot($lp) -eq 0) { $pending++ }
            else { $errors.Add($lp) }
            if ($code -ne 145) { L ("[!] " + [AlexFD]::Short($lp) + " : " + [AlexFD]::ErrMsg($code)) }
        }
        $S.Pending = $pending
        $S.Errors = $errors.Count
        $S.Remaining = @($paths | Where-Object { [AlexFD]::Exists($_) }).Count
    } catch {
        L "[ERREUR] $($_.Exception.Message)"
        $S.Errors = 1
    } finally {
        if ($S.ExplorerStopped) {
            Start-Sleep -Seconds 3
            if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) {
                try { Start-Process "$env:windir\System32\runas.exe" -ArgumentList '/trustlevel:0x20000', "$env:windir\explorer.exe" -WindowStyle Hidden } catch { }
            }
        }
        $S.Done = $true
    }
}

$S = [hashtable]::Synchronized(@{ Done = $false; Pending = 0; Errors = 0; Remaining = 0; Explorer = $false; ExplorerStopped = $false; Blockers = @() })
$validPaths = @($valid | ForEach-Object { $_.Path })

# ─────────────────────────────────────────────────────────────────────────────
# 5. Mode silencieux (sans interface)
# ─────────────────────────────────────────────────────────────────────────────
if ($Silent) {
    foreach ($it in $items) { if ($it.Reason) { Write-Output "[REFUS] $($it.Path) : $($it.Reason)" } else { Write-Output "[CIBLE] $($it.Path) : $($it.Detail)" } }
    if ($validPaths.Count -eq 0) { exit 1 }
    $S.Blockers = @(Get-Blockers $validPaths)
    foreach ($b in $S.Blockers) { Write-Output ("[BLOQUE] " + (Get-BlockerLabel $b)) }
    & $work $validPaths $S $critProc $critSvc
    $m = $null; while ([AlexFD]::Log.TryDequeue([ref]$m)) { Write-Output $m }
    Write-Output ("[BILAN] supprimés : {0} · restants : {1} · au redémarrage : {2} · erreurs : {3}" -f [AlexFD]::Deleted, $S.Remaining, $S.Pending, $S.Errors)
    if ($S.Remaining -eq 0) { exit 0 } elseif ($S.Errors -eq 0) { exit 2 } else { exit 1 }
}

# ─────────────────────────────────────────────────────────────────────────────
# 6. Interface (thème de l'appli : Sombre / Clair)
# ─────────────────────────────────────────────────────────────────────────────
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$dark = $true
try {
    $tf = Join-Path $env:LOCALAPPDATA 'ScriptsWindows\theme.txt'
    if (Test-Path -LiteralPath $tf) { $dark = ((Get-Content -LiteralPath $tf -Raw).Trim() -ne 'light') }
    else { $dark = ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction Stop).AppsUseLightTheme -eq 0) }
} catch { }

if ($dark) {
    $C = @{ BgDeep = '#0F1115'; BgCard = '#1A1D23'; BgInput = '#23272E'; Border = '#2A2E36'; TextPrimary = '#E6E8EB'; TextSecondary = '#A0A6AF'; TextMuted = '#6E7681'
            Danger = '#DA3633'; DangerText = '#FF7B72'; DangerBg = '#26DA3633'; Success = '#2EA043'; SuccessText = '#56D364'; SuccessBg = '#262EA043'
            Warning = '#BB8009'; WarningText = '#E3B341'; WarningBg = '#26BB8009'; OnAccent = '#FFFFFF' }
} else {
    $C = @{ BgDeep = '#F5F6F8'; BgCard = '#FFFFFF'; BgInput = '#F0F2F5'; Border = '#E2E5EA'; TextPrimary = '#1F2328'; TextSecondary = '#59636E'; TextMuted = '#8C959F'
            Danger = '#CF222E'; DangerText = '#CF222E'; DangerBg = '#1ACF222E'; Success = '#1A7F37'; SuccessText = '#1A7F37'; SuccessBg = '#1A1A7F37'
            Warning = '#9A6700'; WarningText = '#9A6700'; WarningBg = '#1A9A6700'; OnAccent = '#FFFFFF' }
}
$bc = New-Object Windows.Media.BrushConverter
function B($k) { return $bc.ConvertFromString($C[$k]) }

$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Forcer la suppression — Alex Toolkit" Width="660" SizeToContent="Height" MaxHeight="820"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize" Background="@@BgDeep@@"
        Foreground="@@TextPrimary@@" FontFamily="Segoe UI Variable Text, Segoe UI" FontSize="13" UseLayoutRounding="True">
  <Window.Resources>
    <Style x:Key="Btn" TargetType="Button">
      <Setter Property="Foreground" Value="@@TextPrimary@@"/>
      <Setter Property="Background" Value="@@BgInput@@"/>
      <Setter Property="BorderBrush" Value="@@Border@@"/>
      <Setter Property="Padding" Value="16,8"/>
      <Setter Property="Margin" Value="8,0,0,0"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.85"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key="Card" TargetType="Border">
      <Setter Property="Background" Value="@@BgCard@@"/>
      <Setter Property="BorderBrush" Value="@@Border@@"/>
      <Setter Property="BorderThickness" Value="1"/>
      <Setter Property="CornerRadius" Value="8"/>
      <Setter Property="Padding" Value="16,12"/>
      <Setter Property="Margin" Value="0,0,0,10"/>
    </Style>
    <Style x:Key="Label" TargetType="TextBlock">
      <Setter Property="FontSize" Value="11"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Foreground" Value="@@TextSecondary@@"/>
      <Setter Property="Margin" Value="0,0,0,8"/>
    </Style>
  </Window.Resources>
  <Grid Margin="24,20,24,20">
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <DockPanel Margin="0,0,0,16">
      <Border Width="40" Height="40" CornerRadius="8" Background="@@DangerBg@@" Margin="0,0,14,0" VerticalAlignment="Top">
        <TextBlock Text="&#xE74D;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="18" Foreground="@@DangerText@@" HorizontalAlignment="Center" VerticalAlignment="Center"/>
      </Border>
      <StackPanel VerticalAlignment="Center">
        <TextBlock Text="Forcer la suppression" FontSize="20" FontWeight="SemiBold"/>
        <TextBlock Text="Suppression définitive, sans passer par la Corbeille." Foreground="@@TextSecondary@@" Margin="0,2,0,0"/>
      </StackPanel>
    </DockPanel>
    <StackPanel Grid.Row="1">
      <Border Style="{StaticResource Card}">
        <StackPanel>
          <TextBlock x:Name="LblItems" Text="ÉLÉMENTS" Style="{StaticResource Label}"/>
          <ScrollViewer MaxHeight="190" VerticalScrollBarVisibility="Auto"><StackPanel x:Name="PanelItems"/></ScrollViewer>
        </StackPanel>
      </Border>
      <Border x:Name="CardBlockers" Style="{StaticResource Card}">
        <StackPanel>
          <TextBlock Text="CE QUI BLOQUE" Style="{StaticResource Label}"/>
          <ScrollViewer MaxHeight="130" VerticalScrollBarVisibility="Auto"><StackPanel x:Name="PanelBlockers"/></ScrollViewer>
        </StackPanel>
      </Border>
      <Border x:Name="CardSteps" Style="{StaticResource Card}">
        <StackPanel>
          <TextBlock Text="CE QUI VA ÊTRE FAIT" Style="{StaticResource Label}"/>
          <TextBlock x:Name="TxtSteps" TextWrapping="Wrap" Foreground="@@TextSecondary@@" LineHeight="20"/>
        </StackPanel>
      </Border>
      <Border x:Name="CardLog" Style="{StaticResource Card}" Visibility="Collapsed">
        <StackPanel>
          <TextBlock Text="DÉROULEMENT" Style="{StaticResource Label}"/>
          <ScrollViewer x:Name="LogScroll" MaxHeight="190" VerticalScrollBarVisibility="Auto">
            <TextBlock x:Name="TxtLog" TextWrapping="Wrap" FontFamily="Cascadia Mono, Consolas" FontSize="12" Foreground="@@TextSecondary@@"/>
          </ScrollViewer>
        </StackPanel>
      </Border>
      <Border x:Name="Banner" CornerRadius="8" Padding="14,10" Margin="0,0,0,10" Visibility="Collapsed" BorderThickness="1">
        <TextBlock x:Name="TxtBanner" TextWrapping="Wrap" FontWeight="SemiBold"/>
      </Border>
    </StackPanel>
    <DockPanel Grid.Row="2" Margin="0,6,0,0" LastChildFill="True">
      <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
        <Button x:Name="BtnRestart" Content="Redémarrer maintenant" Style="{StaticResource Btn}" Visibility="Collapsed"/>
        <Button x:Name="BtnCancel" Content="Annuler" Style="{StaticResource Btn}"/>
        <Button x:Name="BtnGo" Style="{StaticResource Btn}" Background="@@Danger@@" BorderBrush="@@Danger@@" Foreground="@@OnAccent@@">
          <StackPanel Orientation="Horizontal">
            <TextBlock Text="&#xE74D;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" Margin="0,1,8,0" VerticalAlignment="Center"/>
            <TextBlock Text="Forcer la suppression" FontWeight="SemiBold"/>
          </StackPanel>
        </Button>
      </StackPanel>
      <TextBlock x:Name="TxtStatus" VerticalAlignment="Center" Foreground="@@TextSecondary@@" TextTrimming="CharacterEllipsis"/>
    </DockPanel>
  </Grid>
</Window>
'@
$xaml = [regex]::Replace($xaml, '@@(\w+)@@', { param($m) $C[$m.Groups[1].Value] })
$win = [Windows.Markup.XamlReader]::Parse($xaml)
$ui = @{}
foreach ($n in 'LblItems', 'PanelItems', 'CardBlockers', 'PanelBlockers', 'CardSteps', 'CardLog', 'LogScroll', 'TxtLog', 'Banner', 'TxtBanner', 'BtnRestart', 'BtnCancel', 'BtnGo', 'TxtStatus', 'TxtSteps') { $ui[$n] = $win.FindName($n) }
$ui.TxtSteps.Text = @(
    '1. Fermeture des programmes et services qui bloquent (jamais les processus vitaux de Windows)',
    '2. Prise de possession et contrôle total, quelles que soient les autorisations',
    '3. Retrait des attributs lecture seule, caché et système',
    '4. Suppression définitive (les liens et jonctions sont retirés sans toucher à leur cible)',
    '5. Ce qui résiste encore est supprimé au prochain redémarrage') -join "`n"


function New-Row([string]$glyph, $glyphBrush, [string]$title, [string]$sub, $subBrush) {
    $g = New-Object Windows.Controls.Grid
    $g.Margin = New-Object Windows.Thickness(0, 0, 0, 8)
    $c1 = New-Object Windows.Controls.ColumnDefinition; $c1.Width = [Windows.GridLength]::Auto
    [void]$g.ColumnDefinitions.Add($c1)
    [void]$g.ColumnDefinitions.Add((New-Object Windows.Controls.ColumnDefinition))
    $ic = New-Object Windows.Controls.TextBlock
    $ic.Text = $glyph; $ic.FontSize = 16; $ic.Foreground = $glyphBrush
    $ic.Margin = New-Object Windows.Thickness(0, 1, 10, 0)
    $ic.VerticalAlignment = [Windows.VerticalAlignment]::Top
    $ic.FontFamily = New-Object Windows.Media.FontFamily('Segoe Fluent Icons, Segoe MDL2 Assets')
    $sp = New-Object Windows.Controls.StackPanel
    [Windows.Controls.Grid]::SetColumn($sp, 1)
    $t1 = New-Object Windows.Controls.TextBlock
    $t1.Text = $title; $t1.TextWrapping = [Windows.TextWrapping]::Wrap; $t1.Foreground = B 'TextPrimary'
    [void]$sp.Children.Add($t1)
    if ($sub) {
        $t2 = New-Object Windows.Controls.TextBlock
        $t2.Text = $sub; $t2.TextWrapping = [Windows.TextWrapping]::Wrap; $t2.FontSize = 12; $t2.Foreground = $subBrush
        $t2.Margin = New-Object Windows.Thickness(0, 2, 0, 0)
        [void]$sp.Children.Add($t2)
    }
    [void]$g.Children.Add($ic); [void]$g.Children.Add($sp)
    return $g
}

$ui.LblItems.Text = if ($items.Count -gt 1) { "ÉLÉMENTS ($($items.Count))" } else { 'ÉLÉMENT' }
foreach ($it in $items) {
    if ($it.Reason) { [void]$ui.PanelItems.Children.Add((New-Row ([string][char]0xE711) (B 'DangerText') $it.Path "Refusé : $($it.Reason)" (B 'DangerText'))) }
    else {
        $glyph = if ($it.Scan.IsLink) { [char]0xE71B } elseif ($it.Scan.IsDir) { [char]0xE8B7 } else { [char]0xE8A5 }
        [void]$ui.PanelItems.Children.Add((New-Row ([string]$glyph) (B 'TextSecondary') $it.Path $it.Detail (B 'TextMuted')))
    }
}

if ($validPaths.Count -eq 0) {
    $ui.CardBlockers.Visibility = 'Collapsed'; $ui.CardSteps.Visibility = 'Collapsed'; $ui.BtnGo.Visibility = 'Collapsed'
    $ui.BtnCancel.Content = 'Fermer'
    $ui.TxtStatus.Text = 'Rien à supprimer : éléments protégés ou introuvables.'
} else {
    $ui.TxtStatus.Text = 'Recherche de ce qui bloque…'
}

$S.Blockers = @()
$win.Add_SourceInitialized({ [AlexFD]::DarkTitle((New-Object Windows.Interop.WindowInteropHelper($win)).Handle, $dark) })
$win.Add_ContentRendered({
    $win.Topmost = $true; $win.Activate(); $win.Topmost = $false
    if ($validPaths.Count -eq 0) {
        if ($Snapshot) { [void]$win.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ContextIdle, [Action] { Save-Snapshot $Snapshot; $win.Close() }) }
        return
    }
    if ($S.Scanned) { return }
    $S.Scanned = $true
    $win.Dispatcher.Invoke([Windows.Threading.DispatcherPriority]::Background, [Action] {})
    $S.Blockers = @(Get-Blockers $validPaths)
    $ui.PanelBlockers.Children.Clear()
    if ($S.Blockers.Count -eq 0) {
        [void]$ui.PanelBlockers.Children.Add((New-Row ([string][char]0xE73E) (B 'SuccessText') 'Aucun programme ne bloque ces éléments pour l''instant.' $null $null))
    } else {
        foreach ($b in $S.Blockers) { [void]$ui.PanelBlockers.Children.Add((New-Row ([string][char]0xE7BA) (B 'WarningText') (Get-BlockerLabel $b) $null $null)) }
    }
    $ui.TxtStatus.Text = 'Prêt. La suppression est définitive.'
    if ($Snapshot) {
        if ($AutoRun -and $validPaths.Count -gt 0) { $ui.BtnGo.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Primitives.ButtonBase]::ClickEvent))) }
        else { [void]$win.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ContextIdle, [Action] { Save-Snapshot $Snapshot; $win.Close() }) }
    }
})

function Save-Snapshot([string]$file) {
    $win.UpdateLayout()
    $el = $win.Content
    $w = [int][Math]::Ceiling($el.ActualWidth); $h = [int][Math]::Ceiling($el.ActualHeight)
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $rect = New-Object Windows.Rect(0, 0, $w, $h)
    $dc.DrawRectangle($win.Background, $null, $rect)
    $dc.DrawRectangle((New-Object Windows.Media.VisualBrush($el)), $null, $rect)
    $dc.Close()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $fs = [IO.File]::Create($file); $enc.Save($fs); $fs.Close()
}

$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(150)
$timer.Add_Tick({
    $m = $null; $added = $false
    while ([AlexFD]::Log.TryDequeue([ref]$m)) { if ($ui.TxtLog.Text) { $ui.TxtLog.Text += "`n" }; $ui.TxtLog.Text += $m; $added = $true }
    if ($added) { $ui.LogScroll.ScrollToEnd() }
    if (-not $S.Done) { $ui.TxtStatus.Text = ('Suppression en cours… {0:N0} élément(s) supprimé(s)' -f [AlexFD]::Deleted); return }
    $timer.Stop()
    $script:running = $false
    $ui.BtnCancel.Content = 'Fermer'; $ui.BtnCancel.IsEnabled = $true
    $ui.Banner.Visibility = 'Visible'
    if ([AlexFD]::Cancel) {
        $ui.Banner.Background = B 'WarningBg'; $ui.Banner.BorderBrush = B 'Warning'; $ui.TxtBanner.Foreground = B 'WarningText'
        $ui.TxtBanner.Text = 'Arrêté. Une partie des éléments a déjà été supprimée.'
    } elseif ($S.Remaining -eq 0) {
        $ui.Banner.Background = B 'SuccessBg'; $ui.Banner.BorderBrush = B 'Success'; $ui.TxtBanner.Foreground = B 'SuccessText'
        $ui.TxtBanner.Text = ('Supprimé définitivement ({0:N0} élément(s)).' -f [AlexFD]::Deleted)
    } elseif ($S.Errors -eq 0) {
        $ui.Banner.Background = B 'WarningBg'; $ui.Banner.BorderBrush = B 'Warning'; $ui.TxtBanner.Foreground = B 'WarningText'
        $ui.TxtBanner.Text = "Certains éléments sont utilisés par Windows : ils seront supprimés au prochain redémarrage ($($S.Pending) élément(s))."
        $ui.BtnRestart.Visibility = 'Visible'
    } else {
        $ui.Banner.Background = B 'DangerBg'; $ui.Banner.BorderBrush = B 'Danger'; $ui.TxtBanner.Foreground = B 'DangerText'
        $ui.TxtBanner.Text = "Suppression incomplète : $($S.Errors) élément(s) n'ont pas pu être supprimés ni planifiés (voir le déroulement)."
        if ($S.Pending -gt 0) { $ui.BtnRestart.Visibility = 'Visible' }
    }
    $ui.TxtStatus.Text = ''
    if ($Snapshot) { [void]$win.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ContextIdle, [Action] { Save-Snapshot $Snapshot; $win.Close() }) }
})

$script:running = $false
$script:job = $null
$ui.BtnGo.Add_Click({
    $script:running = $true
    $ui.BtnGo.Visibility = 'Collapsed'; $ui.CardSteps.Visibility = 'Collapsed'; $ui.CardLog.Visibility = 'Visible'
    $ui.BtnCancel.Content = 'Arrêter'
    $rs = [runspacefactory]::CreateRunspace(); $rs.ApartmentState = 'MTA'; $rs.Open()
    $script:job = [powershell]::Create()
    $script:job.Runspace = $rs
    [void]$script:job.AddScript($work).AddArgument($validPaths).AddArgument($S).AddArgument($critProc).AddArgument($critSvc)
    [void]$script:job.BeginInvoke()
    $timer.Start()
})
$ui.BtnCancel.Add_Click({
    if ($script:running) { [AlexFD]::Cancel = $true; $ui.BtnCancel.IsEnabled = $false; $ui.TxtStatus.Text = 'Arrêt en cours…'; return }
    $win.Close()
})
$ui.BtnRestart.Add_Click({ Start-Process shutdown.exe -ArgumentList '/r', '/t', '5', '/c', '"Alex Toolkit : fin de la suppression forcée"' -WindowStyle Hidden; $win.Close() })
$win.Add_Closing({ param($s, $e) if ($script:running) { $e.Cancel = $true } })

[void]$win.ShowDialog()
