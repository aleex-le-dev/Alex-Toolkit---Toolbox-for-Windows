using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Grand reset réseau en étapes visibles (cache DNS, ARP, Winsock, TCP/IP, bail DHCP) suivi d'un test de connexion,
/// puis proposition de redémarrage.
/// </summary>
public partial class NetworkResetDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private readonly List<Step> _steps;
    private bool _running;

    public NetworkResetDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _steps = new()
        {
            new(1, "Vider le cache DNS", "Oublie les adresses de sites mémorisées (corrige les sites qui ne s'ouvrent plus ou pointent vers une ancienne adresse).", "ipconfig /flushdns"),
            new(2, "Vider la table ARP", "Oublie les correspondances entre adresses IP et appareils du réseau local (box, imprimantes).", "arp -d *"),
            new(3, "Réinitialiser Winsock", "Répare la couche réseau de Windows souvent abîmée par un VPN, un antivirus ou un logiciel désinstallé.", "netsh winsock reset"),
            new(4, "Réinitialiser TCP/IP", "Remet les réglages IP de Windows à leur état d'origine.", "netsh int ip reset"),
            new(5, "Libérer l'adresse IP", "Rend l'adresse attribuée par la box (la connexion se coupe un instant).", "ipconfig /release"),
            new(6, "Demander une nouvelle adresse IP", "Redemande une adresse à la box (peut prendre jusqu'à une minute).", "ipconfig /renew"),
            new(7, "Tester la connexion", "Vérifie l'accès à Internet et la résolution des noms de sites.", null),
        };
        ListSteps.ItemsSource = _steps;
        Closing += (_, e) =>
        {
            if (_running && AppMessageBox.Show(this, "La réparation est en cours. Fermer quand même ?\n\nL'étape en cours se terminera en arrière-plan.",
                    "Réparation en cours", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        BtnStart.IsEnabled = false;
        BtnReboot.Visibility = Visibility.Collapsed;
        foreach (var s in _steps) s.Reset();
        _onOutput("[i] Réparation de la connexion Internet : 7 étapes…");

        int warnings = 0, errors = 0;
        try
        {
            foreach (var step in _steps)
            {
                _ct.ThrowIfCancellationRequested();
                step.SetRunning();
                TxtStatus.Text = $"Étape {step.Number} sur {_steps.Count} : {step.Title}…";

                if (step.Command != null)
                {
                    int code = await CommandRunner.RunAndGetExitCodeAsync(step.Command, _ct);
                    // arp -d sur une table vide, netsh int ip reset sur des clés protégées : non bloquant
                    if (code == 0) step.SetDone("Terminé.");
                    else if (step.Number is 2 or 4 or 5) { step.SetWarning($"Terminé partiellement (code {code}) : sans conséquence pour la suite."); warnings++; }
                    else { step.SetError($"Échec (code {code})."); errors++; }
                    _onOutput($"[{(code == 0 ? "OK" : "!")}] {step.Title} ({step.Command}) — code {code}");
                }
                else
                {
                    var (ok, detail) = await TestConnectionAsync(_ct);
                    if (ok) step.SetDone(detail); else { step.SetError(detail); errors++; }
                    _onOutput($"[{(ok ? "OK" : "!")}] Test de connexion : {detail}");
                }
            }

            TxtStatus.Text = errors == 0
                ? "Réparation terminée. Redémarrez le PC pour l'appliquer complètement."
                : $"Réparation terminée avec {errors} problème(s). Redémarrez, puis vérifiez la box et le câble / Wi-Fi.";
            TxtStart.Text = "Relancer";
            BtnReboot.Visibility = Visibility.Visible;
            InfoBox.Background = ThemeManager.GetBrush(errors == 0 ? "SuccessBg" : "WarningBg");
            TxtInfo.Text = errors == 0
                ? "Les réglages réseau ont été réinitialisés. Le redémarrage termine la réinitialisation de Winsock et TCP/IP."
                : "Certaines étapes ont échoué. Après redémarrage, si le problème persiste : redémarrez la box, vérifiez le câble ou le mot de passe Wi-Fi, ou essayez le Centre réseau.";
            _onOutput(errors == 0 ? "[OK] Réseau réinitialisé — redémarrage conseillé." : $"[!] Réparation réseau : {errors} étape(s) en échec.");
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Réparation interrompue.";
        }
        finally
        {
            _running = false;
            BtnStart.IsEnabled = true;
        }
    }

    private static async Task<(bool Ok, string Detail)> TestConnectionAsync(CancellationToken ct)
    {
        // Le bail DHCP peut mettre quelques secondes à revenir après ipconfig /renew
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool ping = false; long ms = 0;
            try
            {
                using var p = new Ping();
                var reply = await p.SendPingAsync(IPAddress.Parse("1.1.1.1"), 2000);
                ping = reply.Status == IPStatus.Success;
                ms = reply.RoundtripTime;
            }
            catch { }

            bool dns = false;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                dns = (await Dns.GetHostAddressesAsync("www.microsoft.com", cts.Token)).Length > 0;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (System.Net.Sockets.SocketException) { }

            if (ping && dns) return (true, $"Internet fonctionne (réponse en {ms} ms, noms de sites résolus).");
            if (attempt == 2)
                return (false, ping ? "Internet répond mais les noms de sites ne sont pas résolus : problème de DNS (essayez le Centre réseau > DNS)."
                                    : "Pas d'accès à Internet : vérifiez la box, le câble ou la connexion Wi-Fi, puis redémarrez.");
            await Task.Delay(3000, ct);
        }
        return (false, "");
    }

    private void BtnReboot_Click(object sender, RoutedEventArgs e)
    {
        if (AppMessageBox.Show(this, "Redémarrer le PC maintenant ?\n\nEnregistrez vos documents ouverts avant de continuer.", "Redémarrer",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"Alex Toolkit : fin de la réparation réseau\"") { CreateNoWindow = true, UseShellExecute = false }); }
        catch (Exception ex) { AppMessageBox.Show(this, $"Redémarrage impossible :\n\n{ex.Message}", "Redémarrer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class Step : INotifyPropertyChanged
    {
        private enum State { Pending, Running, Done, Warning, Error }
        private State _state;

        public int Number { get; }
        public string Title { get; }
        public string Explanation { get; }
        public string? Command { get; }
        public string Result { get; private set; } = "";

        public Step(int number, string title, string explanation, string? command)
        {
            Number = number; Title = title; Explanation = explanation; Command = command;
        }

        public Brush StateBg => ThemeManager.GetBrush(_state switch
        {
            State.Done => "SuccessBg", State.Warning => "WarningBg", State.Error => "DangerBg", State.Running => "AccentDim", _ => "BgInput",
        });
        public Brush StateFg => ThemeManager.GetBrush(_state switch
        {
            State.Done => "SuccessText", State.Warning => "WarningText", State.Error => "DangerText", State.Running => "AccentText", _ => "TextSecondary",
        });
        public string StateGlyph => _state switch { State.Done => "", State.Warning => "", State.Error => "", State.Running => "", _ => "" };
        public Visibility GlyphVisibility => _state == State.Pending ? Visibility.Collapsed : Visibility.Visible;
        public Visibility NumberVisibility => _state == State.Pending ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ResultVisibility => Result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RunningVisibility => _state == State.Running ? Visibility.Visible : Visibility.Collapsed;

        public void Reset() => Set(State.Pending, "");
        public void SetRunning() => Set(State.Running, "");
        public void SetDone(string r) => Set(State.Done, r);
        public void SetWarning(string r) => Set(State.Warning, r);
        public void SetError(string r) => Set(State.Error, r);

        private void Set(State s, string result)
        {
            _state = s; Result = result;
            foreach (var p in new[] { nameof(StateBg), nameof(StateFg), nameof(StateGlyph), nameof(GlyphVisibility), nameof(NumberVisibility), nameof(Result), nameof(ResultVisibility), nameof(RunningVisibility) })
                PropertyChanged?.Invoke(this, new(p));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
