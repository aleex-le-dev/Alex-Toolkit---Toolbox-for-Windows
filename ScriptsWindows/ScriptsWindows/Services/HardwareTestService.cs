using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>
/// Tests interactifs du matériel : écrans branchés, son (bip gauche / droite), micro (niveau + enregistrement),
/// webcams et autorisations de confidentialité. Uniquement des API Windows (winmm, user32), sans dépendance.
/// </summary>
public static class HardwareTestService
{
    // ── Écrans ─────────────────────────────────────────────────────────────

    public sealed record DisplayInfo(string DeviceName, string Monitor, string Adapter, int Width, int Height, int Hz, int X, int Y, bool Primary);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE dm);

    /// <summary>Écrans actifs du bureau (résolution, fréquence, position en pixels physiques).</summary>
    public static List<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((dd.StateFlags & 0x1) == 0) continue;   // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(dd.DeviceName, -1, ref dm)) continue;   // ENUM_CURRENT_SETTINGS
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string monitor = EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString) ? mon.DeviceString : "Écran";
            list.Add(new DisplayInfo(dd.DeviceName, monitor, dd.DeviceString, dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency,
                dm.dmPositionX, dm.dmPositionY, (dd.StateFlags & 0x4) != 0));
        }
        return list.OrderByDescending(d => d.Primary).ThenBy(d => d.X).ToList();
    }

    // ── Confidentialité (Paramètres > Confidentialité > Caméra / Microphone) ──

    /// <summary>Vrai si Windows bloque l'accès (réglage de l'appareil, de l'utilisateur ou des applications de bureau).</summary>
    public static bool IsCapabilityDenied(string capability)
    {
        string path = $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}";
        static bool Deny(RegistryKey root, string p)
        {
            try { using var k = root.OpenSubKey(p); return string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        return Deny(Registry.LocalMachine, path) || Deny(Registry.CurrentUser, path) || Deny(Registry.CurrentUser, path + @"\NonPackaged");
    }

    // ── Webcams ────────────────────────────────────────────────────────────

    public sealed record CameraInfo(string Name, bool Ok);

    public static async Task<List<CameraInfo>> GetCamerasAsync(CancellationToken ct = default)
    {
        // Classe « Camera », ou « Image » avec le pilote vidéo USB (anciennes webcams) : exclut les scanners (WSDScan, usbscan…)
        const string script = @"
Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
  Where-Object { $_.PNPClass -eq 'Camera' -or ($_.PNPClass -eq 'Image' -and $_.Service -eq 'usbvideo') } |
  ForEach-Object { '{0}|{1}' -f $_.Name, $_.Status }";
        string output = await PowerShellRunner.RunAsync(script, ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(l => l.Split('|'))
                     .Where(p => p.Length == 2 && p[0].Length > 0)
                     .Select(p => new CameraInfo(p[0], p[1].Equals("OK", StringComparison.OrdinalIgnoreCase)))
                     .ToList();
    }

    // ── Son : bip de test et lecture d'un enregistrement ────────────────────

    [DllImport("winmm.dll")] private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    private static GCHandle _playing;

    /// <summary>Bip stéréo (440 Hz par défaut) sur la voie gauche, droite ou les deux, avec fondu pour éviter les claquements.</summary>
    public static byte[] CreateToneWav(bool left, bool right, double seconds = 1.2, double frequency = 440, int sampleRate = 44100)
    {
        int samples = (int)(seconds * sampleRate), fade = sampleRate / 50;
        var pcm = new byte[samples * 4];
        for (int i = 0; i < samples; i++)
        {
            double env = Math.Min(1.0, Math.Min(i, samples - 1 - i) / (double)fade);
            short v = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 0.45 * env * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), left ? v : (short)0);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), right ? v : (short)0);
        }
        return CreateWav(pcm, sampleRate, 2);
    }

    /// <summary>En-tête WAV (PCM 16 bits) autour de données brutes.</summary>
    public static byte[] CreateWav(byte[] pcm, int sampleRate, short channels)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels); w.Write(sampleRate);
        w.Write(sampleRate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Joue un WAV en mémoire sur la sortie audio par défaut (asynchrone, le précédent est arrêté).</summary>
    public static void PlayWav(byte[] wav)
    {
        StopSound();
        _playing = GCHandle.Alloc(wav, GCHandleType.Pinned);   // le son est lu depuis cette mémoire : elle ne doit pas bouger
        PlaySound(_playing.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
    }

    public static void StopSound()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_playing.IsAllocated) _playing.Free();
    }
}

/// <summary>
/// Capture du micro (winmm, 16 bits mono 44,1 kHz) : niveau en temps réel et enregistrement court à réécouter.
/// Lecture par interrogation (<see cref="Poll"/> appelé par un minuteur de l'interface), sans rappel natif.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; }

    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInGetDevCapsW(UIntPtr id, ref WAVEINCAPS caps, uint size);
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, uint device, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);

    private const uint WaveMapper = 0xFFFFFFFF, WHDR_DONE = 0x1;
    public const int SampleRate = 44100;
    private const int BufferCount = 4, BufferBytes = SampleRate / 10 * 2;   // 100 ms par tampon
    private static readonly int HeaderSize = Marshal.SizeOf<WAVEHDR>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags));

    private IntPtr _handle;
    private readonly List<IntPtr> _headers = new();
    private List<byte>? _clip;
    private int _clipMax;

    /// <summary>Niveau crête du dernier tampon (0 à 1).</summary>
    public float Level { get; private set; }
    public bool IsOpen => _handle != IntPtr.Zero;
    public bool IsRecordingClip => _clip != null && _clip.Count < _clipMax;
    public double ClipProgress => _clip == null || _clipMax == 0 ? 0 : Math.Min(1.0, _clip.Count / (double)_clipMax);
    public byte[]? LastClipWav { get; private set; }

    /// <summary>Micros présents (index winmm ; -1 = micro par défaut de Windows).</summary>
    public static List<string> GetDevices()
    {
        var list = new List<string>();
        uint n = waveInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new WAVEINCAPS();
            if (waveInGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<WAVEINCAPS>()) == 0) list.Add(caps.szPname);
        }
        return list;
    }

    /// <summary>Ouvre le micro (index ou -1 pour celui par défaut). Renvoie un message d'erreur, ou null si tout va bien.</summary>
    public string? Open(int device)
    {
        Close();
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2 };
        int rc = waveInOpen(out _handle, device < 0 ? WaveMapper : (uint)device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0) { _handle = IntPtr.Zero; return rc == 2 ? "Aucun micro détecté." : $"Micro inaccessible (erreur {rc}) : il est peut-être utilisé par une autre application."; }
        for (int i = 0; i < BufferCount; i++)
        {
            var hdr = new WAVEHDR { lpData = Marshal.AllocHGlobal(BufferBytes), dwBufferLength = BufferBytes };
            IntPtr p = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(hdr, p, false);
            waveInPrepareHeader(_handle, p, (uint)HeaderSize);
            waveInAddBuffer(_handle, p, (uint)HeaderSize);
            _headers.Add(p);
        }
        waveInStart(_handle);
        return null;
    }

    /// <summary>Lit les tampons remplis (niveau, enregistrement en cours) et les rend au micro.</summary>
    public void Poll()
    {
        if (_handle == IntPtr.Zero) return;
        foreach (var p in _headers)
        {
            var hdr = Marshal.PtrToStructure<WAVEHDR>(p);
            if ((hdr.dwFlags & WHDR_DONE) == 0) continue;
            int n = (int)hdr.dwBytesRecorded;
            if (n > 0)
            {
                var data = new byte[n];
                Marshal.Copy(hdr.lpData, data, 0, n);
                int peak = 0;
                for (int i = 0; i + 1 < n; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(data, i)));
                Level = peak / 32768f;
                if (_clip != null && _clip.Count < _clipMax)
                {
                    _clip.AddRange(data.Take(Math.Min(n, _clipMax - _clip.Count)));
                    if (_clip.Count >= _clipMax) { LastClipWav = HardwareTestService.CreateWav(_clip.ToArray(), SampleRate, 1); }
                }
            }
            Marshal.WriteInt32(p, FlagsOffset, (int)(hdr.dwFlags & ~WHDR_DONE));
            waveInAddBuffer(_handle, p, (uint)HeaderSize);
        }
    }

    /// <summary>Commence un enregistrement de <paramref name="seconds"/> secondes (disponible ensuite dans <see cref="LastClipWav"/>).</summary>
    public void StartClip(double seconds)
    {
        LastClipWav = null;
        _clipMax = (int)(seconds * SampleRate) * 2;
        _clip = new List<byte>(_clipMax);
    }

    public void Close()
    {
        if (_handle == IntPtr.Zero) return;
        waveInReset(_handle);
        foreach (var p in _headers)
        {
            waveInUnprepareHeader(_handle, p, (uint)HeaderSize);
            Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(p).lpData);
            Marshal.FreeHGlobal(p);
        }
        _headers.Clear();
        waveInClose(_handle);
        _handle = IntPtr.Zero;
        _clip = null;
        Level = 0;
    }

    public void Dispose() => Close();
}
