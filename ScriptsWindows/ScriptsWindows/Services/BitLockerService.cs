using System.Text;
using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public enum BlState { NotEncrypted, Protected, Suspended, AwaitingActivation, Encrypting, Decrypting, Paused, Locked, Unknown }

public sealed record BlVolume(char Letter, string Label, long Size, bool Removable, bool IsOs, BlState State, double Percent, string Method, string[] Protectors, bool AutoUnlock, bool Limited)
{
    public string StateLabel => State switch
    {
        BlState.NotEncrypted => "Non chiffré",
        BlState.Protected => "Protégé",
        BlState.Suspended => "Protection suspendue",
        BlState.AwaitingActivation => "Chiffré, protection pas encore activée",
        BlState.Encrypting => $"Chiffrement en cours ({Percent:0} %)",
        BlState.Decrypting => $"Déchiffrement en cours ({100 - Percent:0} %)",
        BlState.Paused => $"Opération en pause ({Percent:0} % chiffré)",
        BlState.Locked => "Verrouillé",
        _ => "État inconnu"
    };

    /// <summary>Jeton de couleur du thème (Success / Warning / Danger / Accent / TextMuted).</summary>
    public string Level => State switch
    {
        BlState.Protected => "Success",
        BlState.Suspended or BlState.AwaitingActivation or BlState.Paused => "Warning",
        BlState.Locked => "Danger",
        BlState.Encrypting or BlState.Decrypting => "Accent",
        _ => "TextMuted"
    };

    public bool HasRecoveryKey => Protectors.Contains("RecoveryPassword");
}

/// <summary>
/// BitLocker : état réel de chaque volume (Get-BitLockerVolume, énumérations anglaises indépendantes de la langue ;
/// sans élévation, repli sur la propriété Explorateur System.Volume.BitLockerProtection), clés de récupération,
/// suspension / reprise, déverrouillage d'un disque client, verrouillage, activation avec clé de récupération
/// sauvegardée AVANT le chiffrement, désactivation. Les secrets passent en base64 (jamais insérés tels quels).
/// </summary>
public static class BitLockerService
{
    public static string ProtectorLabel(string p) => p switch
    {
        "Tpm" => "puce TPM",
        "TpmPin" => "TPM + code PIN",
        "TpmStartupKey" or "TpmPinStartupKey" => "TPM + clé USB",
        "RecoveryPassword" => "clé de récupération",
        "Password" => "mot de passe",
        "ExternalKey" => "clé externe (déverrouillage auto)",
        "AdAccountOrGroup" => "compte de domaine",
        "PublicKey" => "carte à puce",
        _ => p
    };

    /// <summary>Édition Famille : BitLocker ne peut pas y être activé (mais un disque chiffré se lit et se déverrouille).</summary>
    public static bool IsHomeEdition()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return (k?.GetValue("EditionID") as string ?? "").StartsWith("Core", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static BlState MapState(string volumeStatus, string protection, string lockStatus, int protectorCount, int shellValue)
    {
        if (volumeStatus.Length == 0)   // lecture limitée (sans élévation) : propriété de l'Explorateur
            return shellValue switch { 1 => BlState.Protected, 2 => BlState.NotEncrypted, 3 => BlState.Encrypting, 4 => BlState.Decrypting, 5 => BlState.Suspended, 6 => BlState.Locked, 0 => BlState.NotEncrypted, _ => BlState.Unknown };
        if (lockStatus == "Locked") return BlState.Locked;
        return volumeStatus switch
        {
            "FullyDecrypted" => BlState.NotEncrypted,
            "EncryptionInProgress" => BlState.Encrypting,
            "DecryptionInProgress" => BlState.Decrypting,
            "EncryptionPaused" or "DecryptionPaused" or "EncryptionSuspended" or "DecryptionSuspended" => BlState.Paused,
            "FullyEncrypted" or "FullyEncryptedWipeInProgress" or "FullyEncryptedWipePaused" =>
                protection == "On" ? BlState.Protected : protectorCount == 0 ? BlState.AwaitingActivation : BlState.Suspended,
            _ => BlState.Unknown
        };
    }

    public static async Task<List<BlVolume>> GetVolumesAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$bl = @{}
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($admin) { try { Get-BitLockerVolume -ErrorAction Stop | ForEach-Object { $bl[$_.MountPoint.TrimEnd('\')] = $_ } } catch { } }   # sans élévation : 5 s pour échouer
$ns = $null; try { $ns = (New-Object -ComObject Shell.Application).NameSpace(17) } catch { }
$sys = $env:SystemDrive
$out = foreach ($d in [IO.DriveInfo]::GetDrives() | Where-Object { [string]$_.DriveType -in 'Fixed','Removable' }) {
    $mp = $d.Name.Substring(0, 2)
    $v = $bl[$mp]
    $sp = -1; if ($ns) { try { $x = $ns.ParseName($mp).ExtendedProperty('System.Volume.BitLockerProtection'); if ($x -ne $null) { $sp = [int]$x } } catch { } }
    $ready = $d.IsReady
    [pscustomobject]@{
        L = $mp.Substring(0, 1); N = if ($ready) { [string]$d.VolumeLabel } else { '' }
        S = if ($ready) { [long]$d.TotalSize } elseif ($v) { [long]($v.CapacityGB * 1GB) } else { [long]0 }
        R = ([string]$d.DriveType -eq 'Removable'); O = ($mp -eq $sys)
        VS = if ($v) { [string]$v.VolumeStatus } else { '' }; P = if ($v) { [string]$v.ProtectionStatus } else { '' }
        K = if ($v) { [string]$v.LockStatus } else { '' }; E = if ($v) { [double]$v.EncryptionPercentage } else { [double]0 }
        M = if ($v) { [string]$v.EncryptionMethod } else { '' }
        KP = @(if ($v) { $v.KeyProtector | ForEach-Object { [string]$_.KeyProtectorType } })
        AU = if ($v) { [bool]$v.AutoUnlockEnabled } else { $false }; SP = $sp
    }
}
ConvertTo-Json -Compress -Depth 3 -InputObject @($out)", ct);
        var list = new List<BlVolume>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        foreach (var x in arr)
        {
            string l = x.GetProperty("L").GetString() ?? "";
            if (l.Length == 0) continue;
            string vs = x.GetProperty("VS").GetString() ?? "";
            var kp = x.GetProperty("KP").ValueKind == JsonValueKind.Array ? x.GetProperty("KP").EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray() : Array.Empty<string>();
            list.Add(new BlVolume(char.ToUpperInvariant(l[0]), x.GetProperty("N").GetString() ?? "", x.GetProperty("S").GetInt64(), x.GetProperty("R").GetBoolean(), x.GetProperty("O").GetBoolean(),
                MapState(vs, x.GetProperty("P").GetString() ?? "", x.GetProperty("K").GetString() ?? "", kp.Length, x.GetProperty("SP").GetInt32()),
                x.GetProperty("E").GetDouble(), x.GetProperty("M").GetString() ?? "", kp, x.GetProperty("AU").GetBoolean(), vs.Length == 0));
        }
        return list.OrderBy(v => v.Letter).ToList();
    }

    private static string Mp(char letter) => char.IsAsciiLetterUpper(char.ToUpperInvariant(letter)) ? $"'{char.ToUpperInvariant(letter)}:'" : throw new ArgumentException("Lettre de lecteur invalide");
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private const string Decode = "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{0}'))";

    /// <summary>Clés de récupération (48 chiffres) du volume : (identifiant du protecteur, clé).</summary>
    public static async Task<List<(string Id, string Key)>> GetRecoveryKeysAsync(char letter, CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync($@"
$v = Get-BitLockerVolume -MountPoint {Mp(letter)} -ErrorAction Stop
ConvertTo-Json -Compress -InputObject @($v.KeyProtector | Where-Object {{ [string]$_.KeyProtectorType -eq 'RecoveryPassword' }} | ForEach-Object {{ [pscustomobject]@{{ I = [string]$_.KeyProtectorId; K = [string]$_.RecoveryPassword }} }})", ct);
        var r = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('[')) return r;
        using var doc = JsonDocument.Parse(json);
        foreach (var x in doc.RootElement.EnumerateArray()) r.Add((x.GetProperty("I").GetString() ?? "", x.GetProperty("K").GetString() ?? ""));
        return r;
    }

    /// <summary>Normalise une clé de récupération saisie (espaces, tirets oubliés) ; null si elle est impossible (48 chiffres, blocs multiples de 11).</summary>
    public static string? NormalizeRecoveryKey(string input)
    {
        var digits = new string((input ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 48) return null;
        var blocks = Enumerable.Range(0, 8).Select(i => digits.Substring(i * 6, 6)).ToArray();
        if (blocks.Any(b => int.Parse(b) % 11 != 0 || int.Parse(b) >= 720896)) return null;
        return string.Join("-", blocks);
    }

    /// <summary>Texte du fichier de sauvegarde d'une clé.</summary>
    public static string KeyFileText(char letter, string id, string key) =>
        $"Clé de récupération BitLocker\r\n\r\nOrdinateur : {Environment.MachineName}\r\nLecteur : {letter}:\r\nDate : {DateTime.Now:dd/MM/yyyy HH:mm}\r\n" +
        $"Identificateur (affiché par l'écran de récupération) : {id.Trim('{', '}')}\r\n\r\nClé de récupération :\r\n{key}\r\n\r\n" +
        "Conservez ce fichier ailleurs que sur le disque chiffré (clé USB, autre PC, coffre-fort de mots de passe).\r\n";

    private static async Task<(bool Ok, string Error)> RunChecked(string body, CancellationToken ct)
    {
        string r = (await PowerShellRunner.RunAsync($"try {{ {body}; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message }}", ct)).Trim();
        string last = r.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return last == "OK" ? (true, "") : (false, last.StartsWith("ERR:") ? last[4..].Trim() : (r.Length > 0 ? r : "aucune réponse"));
    }

    /// <summary>Suspend la protection pour N redémarrages (mise à jour du BIOS, changement de carte mère…).</summary>
    public static Task<(bool Ok, string Error)> SuspendAsync(char letter, int reboots, CancellationToken ct = default)
        => RunChecked($"Suspend-BitLocker -MountPoint {Mp(letter)} -RebootCount {Math.Clamp(reboots, 0, 15)} -ErrorAction Stop | Out-Null", ct);

    public static Task<(bool Ok, string Error)> ResumeAsync(char letter, CancellationToken ct = default)
        => RunChecked($"Resume-BitLocker -MountPoint {Mp(letter)} -ErrorAction Stop | Out-Null", ct);

    /// <summary>Déverrouille un disque avec son mot de passe ou sa clé de récupération ; vérifie qu'il est bien déverrouillé.</summary>
    public static Task<(bool Ok, string Error)> UnlockAsync(char letter, string secret, bool recoveryKey, CancellationToken ct = default)
    {
        string arg = recoveryKey
            ? $"-RecoveryPassword ({string.Format(Decode, B64(secret))})"
            : $"-Password (ConvertTo-SecureString ({string.Format(Decode, B64(secret))}) -AsPlainText -Force)";
        return RunChecked($"Unlock-BitLocker -MountPoint {Mp(letter)} {arg} -ErrorAction Stop | Out-Null; if ([string](Get-BitLockerVolume -MountPoint {Mp(letter)}).LockStatus -ne 'Unlocked') {{ throw 'le disque est resté verrouillé' }}", ct);
    }

    /// <summary>Reverrouille un disque de données (les fichiers ouverts dessus sont fermés de force).</summary>
    public static Task<(bool Ok, string Error)> LockAsync(char letter, CancellationToken ct = default)
        => RunChecked($"Lock-BitLocker -MountPoint {Mp(letter)} -ForceDismount -ErrorAction Stop | Out-Null", ct);

    /// <summary>Lance le déchiffrement (en arrière-plan, le PC reste utilisable).</summary>
    public static Task<(bool Ok, string Error)> DisableAsync(char letter, CancellationToken ct = default)
        => RunChecked($"Disable-BitLocker -MountPoint {Mp(letter)} -ErrorAction Stop | Out-Null", ct);

    /// <summary>Étape 1 de l'activation : ajoute une clé de récupération et la renvoie (à sauvegarder AVANT de chiffrer).</summary>
    public static async Task<(string Id, string Key)?> AddRecoveryKeyAsync(char letter, CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync($@"
try {{
    $v = Add-BitLockerKeyProtector -MountPoint {Mp(letter)} -RecoveryPasswordProtector -WarningAction SilentlyContinue -ErrorAction Stop
    $k = @($v.KeyProtector | Where-Object {{ [string]$_.KeyProtectorType -eq 'RecoveryPassword' }})[-1]
    ConvertTo-Json -Compress -InputObject ([pscustomobject]@{{ I = [string]$k.KeyProtectorId; K = [string]$k.RecoveryPassword }})
}} catch {{ }}", ct);
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return null;
        using var doc = JsonDocument.Parse(json);
        string id = doc.RootElement.GetProperty("I").GetString() ?? "", key = doc.RootElement.GetProperty("K").GetString() ?? "";
        return key.Length > 0 ? (id, key) : null;
    }

    public static Task<(bool Ok, string Error)> RemoveProtectorAsync(char letter, string id, CancellationToken ct = default)
        => RunChecked($"Remove-BitLockerKeyProtector -MountPoint {Mp(letter)} -KeyProtectorId ({string.Format(Decode, B64(id))}) -ErrorAction Stop | Out-Null", ct);

    /// <summary>Étape 2 : chiffre l'espace utilisé (XTS-AES 128), avec la puce TPM pour le disque de Windows ou un mot de passe pour un disque de données.</summary>
    public static Task<(bool Ok, string Error)> EnableAsync(char letter, bool isOs, string? password, CancellationToken ct = default)
    {
        string prot = isOs
            ? "-TpmProtector -SkipHardwareTest"
            : $"-PasswordProtector -Password (ConvertTo-SecureString ({string.Format(Decode, B64(password ?? ""))}) -AsPlainText -Force)";
        return RunChecked($"Enable-BitLocker -MountPoint {Mp(letter)} -EncryptionMethod XtsAes128 -UsedSpaceOnly {prot} -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null", ct);
    }

    public static string Size(long b) => b <= 0 ? "taille inconnue" : b >= 1L << 40 ? $"{b / (double)(1L << 40):0.0} To" : $"{b / (double)(1L << 30):0} Go";
}
