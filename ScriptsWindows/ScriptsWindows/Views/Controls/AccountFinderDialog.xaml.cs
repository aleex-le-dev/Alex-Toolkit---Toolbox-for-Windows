using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// « Mes informations en ligne » : un formulaire unique (pseudos, e-mail, téléphone, nom, services, mot de passe — tout facultatif).
/// « Tout vérifier » lance en même temps la recherche des comptes (base Sherlock / maigret) et chaque vérification de fuite ;
/// les résultats sont classés par rubrique, avec une vue d'ensemble. Pour ses propres informations uniquement.
/// </summary>
public partial class AccountFinderDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private readonly ConcurrentQueue<AccountResult> _incoming = new();
    private readonly List<AccountResult> _results = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _runCts;
    private bool _busy, _searching, _multiUser;   // _busy : « Tout vérifier » en cours ; _searching : recherche des pseudos en cours
    private int _total;
    private string _source = "";
    private JustDeleteMeIndex _jdm = new();
    private const string ManualDetail = "ajouté à la main";
    private readonly System.Diagnostics.Stopwatch _clock = new();   // pour le temps restant estimé
    private AccountSearchEntry? _entry;          // recherche en cours / affichée (enregistrée dans l'historique)
    private CancellationToken Ct => _runCts?.Token ?? _ct;

    // ─── Rubriques de résultats ───
    internal enum View { Summary, Found, Uncertain, Breached, Leaked, Email, Phone, Services, Name, Address, Iban, Password, History }
    private View _view = View.Summary;
    private bool _accountsAsked;                                         // des pseudos ont été cherchés (ou une recherche de l'historique est affichée)
    private readonly Dictionary<View, StackPanel> _sections = new();     // un panneau par champ vérifié (rempli par ShowResult)
    private readonly Dictionary<View, Button> _viewButtons = new();
    private TextBlock? _sumAccounts;                                     // ligne « comptes » de la vue d'ensemble, mise à jour en direct
    private readonly Dictionary<View, string> _lastInputs = new();       // valeur vérifiée par rubrique : seules les modifications sont revérifiées
    private string _lastUsersKey = "";                                   // pseudos (+ recherche étendue) de la dernière recherche
    private readonly Dictionary<View, List<ExposureService.LeakSource>> _leaks = new();   // fuites qui vous concernent, par rubrique d'origine
    private (string Masked, string Country, string? Bank)? _ibanInfo;     // IBAN : seule la forme masquée est gardée

    private static readonly (View View, string Title)[] CheckViews =
    {
        (View.Leaked, "Ce qui a fuité"), (View.Email, "E-mail"), (View.Phone, "Téléphone"), (View.Services, "Services"), (View.Name, "Nom"),
        (View.Address, "Adresse"), (View.Iban, "IBAN"), (View.Password, "Mot de passe"),
    };

    public AccountFinderDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 16);   // petits écrans : la fenêtre reste entière
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _flush.Tick += (_, _) => Flush();
        BuildKeyPanel();
        Loaded += (_, _) => { TxtUsers.Focus(); Render(); _ = EnsureLinksAsync(); };
        Closing += (_, _) => _runCts?.Cancel();
    }

    private static TextBlock Txt(string text, double size = 12, string brush = "TextSecondary", bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = ThemeManager.GetBrush(brush), TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private void Field_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && !_busy) BtnSearch_Click(sender, e); }

    private bool _extended;
    private void BtnExtended_Click(object sender, RoutedEventArgs e)
    {
        _extended = !_extended;
        BtnExtended.Content = _extended ? "Recherche étendue : oui" : "Recherche étendue : non";
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        TxtUsers.Clear(); TxtEmail.Clear(); TxtPhone.Clear(); TxtFullName.Clear(); TxtServices.Clear(); PwdCheck.Clear();
        TxtAddress.Clear(); TxtCity.Clear(); TxtIban.Clear();
        Hint("Seuls les champs remplis sont vérifiés.", false);
        TxtUsers.Focus();
    }

    private void Hint(string text, bool error)
    {
        TxtHint.Text = text;
        TxtHint.Foreground = ThemeManager.GetBrush(error ? "DangerText" : "TextSecondary");
    }

    private void SetFormEnabled(bool on)
    {
        foreach (var c in new Control[] { TxtUsers, TxtEmail, TxtPhone, TxtFullName, TxtAddress, TxtCity, TxtIban, TxtServices, PwdCheck, BtnExtended, BtnClear, BtnKey }) c.IsEnabled = on;
    }

    /// <summary>Services saisis : séparés par des virgules, points-virgules ou retours à la ligne (10 au maximum).</summary>
    internal static List<string> ParseServices(string text) =>
        text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();

    private async void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _runCts?.Cancel(); return; }
        await RunAllAsync();
    }

    /// <summary>« Tout vérifier » : valide le formulaire, puis lance en parallèle la recherche des pseudos et chaque vérification remplie.</summary>
    internal async Task RunAllAsync()
    {
        string usersText = TxtUsers.Text.Trim(), email = TxtEmail.Text.Trim(), phone = TxtPhone.Text.Trim();
        string name = string.Join(' ', TxtFullName.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string pwd = PwdCheck.Password;
        string address = string.Join(' ', TxtAddress.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)), city = TxtCity.Text.Trim();
        string ibanText = TxtIban.Text.Trim();
        string? iban = ibanText.Length > 0 ? ExposureService.NormalizeIban(ibanText) : null;
        var services = ParseServices(TxtServices.Text);
        var users = new List<string>();
        string? error = null;
        if (usersText.Length > 0)
        {
            var (valid, rejected) = AccountFinderService.ParseUsernames(usersText);
            if (rejected.Count > 0) error = $"Pseudo(s) refusé(s) : {string.Join(", ", rejected)} — lettres, chiffres, « . », « _ » et « - » seulement.";
            else if (valid.Count > 5) error = "5 pseudos au maximum par recherche.";
            users = valid;
        }
        if (error == null && email.Length > 0 && !ExposureService.IsEmail(email)) error = "Adresse e-mail invalide.";
        if (error == null && phone.Length > 0 && ExposureService.NormalizeFrPhone(phone) == null) error = "Numéro français invalide (10 chiffres, ex. 06 12 34 56 78).";
        if (error == null && name.Length > 0 && (name.Length < 3 || !name.Contains(' '))) error = "Saisissez le prénom et le nom (ex. Jean Dupont).";
        if (error == null && ibanText.Length > 0 && iban == null) error = "IBAN invalide : la clé de contrôle ne correspond pas (faute de frappe ?).";
        if (error == null && users.Count == 0 && email.Length == 0 && phone.Length == 0 && name.Length == 0 && services.Count == 0 && pwd.Length == 0
            && address.Length == 0 && city.Length == 0 && iban == null)
            error = "Remplissez au moins un champ.";
        if (error != null) { Hint(error, true); return; }
        Hint("Seuls les champs remplis sont vérifiés.", false);
        if (email.Length > 0) _knownEmail = email;
        _ibanInfo = iban == null ? null : (ExposureService.MaskIban(iban), ExposureService.IbanCountry(iban), ExposureService.IbanBank(iban));
        iban = null;   // l'IBAN complet n'est pas gardé

        _busy = true;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        // Seul ce qui est nouveau ou modifié est vérifié : ajouter son nom ne relance pas la recherche des pseudos (plusieurs minutes).
        // Si rien n'a changé, « Tout vérifier » refait tout (mise à jour).
        var inputs = new Dictionary<View, string>
        {
            [View.Email] = email.ToLowerInvariant(), [View.Phone] = phone.Length > 0 ? ExposureService.NormalizeFrPhone(phone)! : "",
            [View.Services] = string.Join(",", services.Select(s => s.ToLowerInvariant()).Order()), [View.Name] = name.ToLowerInvariant(),
            [View.Address] = address.Length + city.Length > 0 ? $"{address}|{city}|{name}".ToLowerInvariant() : "",
            [View.Iban] = _ibanInfo?.Masked ?? "",
        };
        string usersKey = users.Count == 0 ? "" : string.Join(" ", users.Select(u => u.ToLowerInvariant()).Order()) + (_extended ? " +étendue" : "");
        bool accountsDone = usersKey.Length > 0 && usersKey == _lastUsersKey && _entry?.Complete == true && _results.Count > 0;
        bool anyChange = pwd.Length > 0 || (usersKey.Length > 0 && !accountsDone) || inputs.Any(kv => kv.Value != (_lastInputs.TryGetValue(kv.Key, out var o) ? o : ""));
        bool refresh = !anyChange;   // rien de neuf : tout est revérifié
        foreach (var (v, value) in inputs)
            if (value.Length == 0) { _sections.Remove(v); _leaks.Remove(v); }   // champ vidé : sa rubrique disparaît
        if (pwd.Length == 0) _sections.Remove(View.Password);
        bool Todo(View v) => inputs[v].Length > 0 && (refresh || !_sections.ContainsKey(v) || inputs[v] != (_lastInputs.TryGetValue(v, out var o) ? o : ""));

        StackPanel Sec(View v) { var p = new StackPanel(); p.Children.Add(Txt("Vérification…", 12, "TextMuted")); _sections[v] = p; _leaks.Remove(v); _lastInputs[v] = inputs.GetValueOrDefault(v, ""); return p; }
        var checks = new List<Task>();
        var started = new List<View>();
        if (Todo(View.Email)) { started.Add(View.Email); checks.Add(Guard(Sec(View.Email), t => CheckEmailAsync(t, email))); }
        if (pwd.Length > 0) { started.Add(View.Password); PwdCheck.Clear(); var p = pwd; checks.Add(Guard(Sec(View.Password), t => CheckPasswordAsync(t, p))); pwd = ""; }   // jamais gardé dans la fenêtre
        if (Todo(View.Phone)) { started.Add(View.Phone); checks.Add(Guard(Sec(View.Phone), t => CheckPhoneAsync(t, phone))); }
        if (Todo(View.Services)) { started.Add(View.Services); checks.Add(CheckServicesAsync(Sec(View.Services), services)); }
        if (Todo(View.Name)) { started.Add(View.Name); checks.Add(Guard(Sec(View.Name), t => { CheckName(t, name, users); return Task.CompletedTask; })); }
        if (Todo(View.Address)) { started.Add(View.Address); checks.Add(Guard(Sec(View.Address), t => { CheckAddress(t, address, city, name); return Task.CompletedTask; })); }
        if (Todo(View.Iban)) { started.Add(View.Iban); Sec(View.Iban); }   // rubrique déduite des fuites trouvées (UpdateDerived)
        foreach (var v in inputs.Keys) if (inputs[v].Length == 0) _lastInputs.Remove(v);
        bool searchAccounts = usersKey.Length > 0 && (refresh || !accountsDone);
        if (searchAccounts) { _lastUsersKey = usersKey; _accountsAsked = true; _results.RemoveAll(r => r.Detail != ManualDetail); while (_incoming.TryDequeue(out _)) { } }

        // Une seule vérification nouvelle : on l'affiche directement ; sinon la vue d'ensemble
        _view = !searchAccounts && started.Count == 1 ? started[0] : View.Summary;
        UpdateDerived();
        SetFormEnabled(false);
        BtnSearch.Content = "Arrêter"; BtnSearch.SetResourceReference(StyleProperty, "DangerActionButton");
        BtnCopy.IsEnabled = BtnExport.IsEnabled = false;
        TxtStatus.Text = "Vérifications en cours…";
        Render();
        var what = (searchAccounts ? new[] { "pseudos" } : Array.Empty<string>()).Concat(started.Select(v => CheckViews.First(c => c.View == v).Title.ToLowerInvariant())).ToList();
        _onOutput("🔎 [MES INFOS] Vérification : " + string.Join(", ", what));
        if (usersKey.Length > 0 && !searchAccounts)
            Hint("Pseudos déjà vérifiés : résultats conservés. Modifiez-les pour relancer leur recherche, ou cliquez « Tout vérifier » sans rien changer pour tout mettre à jour.", false);
        try
        {
            if (searchAccounts) checks.Add(SearchAccountsAsync(users, _runCts.Token));
            await Task.WhenAll(checks);
        }
        catch { /* chaque vérification affiche sa propre erreur */ }
        finally
        {
            _busy = false;
            SetFormEnabled(true);
            BtnSearch.Content = "Tout vérifier"; BtnSearch.SetResourceReference(StyleProperty, "PrimaryActionButton");
            BtnCopy.IsEnabled = BtnExport.IsEnabled = _results.Any(r => r.Status is AccountStatus.Found or AccountStatus.Uncertain);
            if (!searchAccounts) TxtStatus.Text = "Vérifications terminées : le détail est dans chaque rubrique.";
            UpdateDerived();
            Render();
            _ = EnsureLinksAsync();   // fuites connues → badges sur les comptes
        }
    }

    /// <summary>Exécute une vérification dans sa rubrique ; une erreur s'affiche dans la rubrique sans arrêter les autres.</summary>
    private async Task Guard(StackPanel target, Func<StackPanel, Task> check)
    {
        try { await check(target); }
        catch (OperationCanceledException) { ShowResult(target, "Warning", "Vérification arrêtée."); }
        catch (Exception ex) { ShowResult(target, "Danger", "Vérification impossible : " + ex.Message); }
        finally { SectionDone(); }
    }

    private void SectionDone()
    {
        UpdateDerived();
        RefreshViews();
        if (_view == View.Summary) Render();
    }

    // ─── Recherche des pseudos ───
    private async Task SearchAccountsAsync(List<string> users, CancellationToken ct)
    {
        _searching = true; _multiUser = users.Count > 1;
        _entry = new AccountSearchEntry { Usernames = users };
        PanelProgress.Visibility = Visibility.Visible;
        TxtStep.Text = "Téléchargement de la liste des sites…"; TxtPct.Text = ""; SetBar(0);
        _onOutput($"🔎 [COMPTES] Recherche de {string.Join(", ", users)}");
        try
        {
            var jdmTask = _jdm.Count > 0 ? Task.FromResult(_jdm) : JustDeleteMeIndex.LoadAsync(ct);   // liens de suppression, en parallèle
            var (sites, source) = await AccountFinderService.LoadSitesAsync(ct);
            _source = source;
            try { _jdm = await jdmTask; } catch (OperationCanceledException) { throw; } catch { }
            var list = sites.ToList();
            if (_extended)
            {
                TxtStep.Text = "Téléchargement des sites de la recherche étendue…";
                try { list.AddRange(await MaigretSitesService.LoadAsync(sites, ct)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { TxtStatus.Text = $"Recherche étendue indisponible ({ex.Message}) : base principale seulement."; }
            }
            _sitesByName = list.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _total = list.Count * users.Count;
            TxtStep.Text = $"Vérification de {list.Count} sites" + (users.Count > 1 ? $" pour {users.Count} pseudos" : "") + "…";
            _clock.Restart();
            UpdateCounts();
            _flush.Start();
            await AccountFinderService.SearchAsync(users, list, r => _incoming.Enqueue(r), ct);
            Flush();
            _flush.Stop();
            await RetryTransientAsync(ct);   // sites bloqués un instant : un nouvel essai
            RecordStats(users[0]);           // fiabilité des sites pour les prochaines recherches
            _entry.Complete = true;
            int found = _results.Count(r => r.Status == AccountStatus.Found);
            int easy = _results.Count(r => r.Status == AccountStatus.Found && _jdm.Find(r.UrlMain, r.Site)?.Difficulty == "easy");
            TxtStatus.Text = $"Terminé : {found} compte(s) trouvé(s)" + (easy > 0 ? $" dont {easy} faciles à supprimer" : "") +
                             $", {_results.Count(r => r.Status == AccountStatus.Uncertain)} à vérifier. Sites : Sherlock {source} ; suppression : JustDeleteMe ({_jdm.Count} sites).";
            _onOutput($"[OK] Comptes en ligne : {found} trouvé(s) sur {list.Count} sites.");
        }
        catch (OperationCanceledException) { Flush(); TxtStatus.Text = $"Recherche arrêtée ({_results.Count} vérification(s) sur {_total})."; }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; _onOutput($"❌ [ERREUR] Comptes en ligne : {ex.Message}"); }
        finally
        {
            _flush.Stop(); _searching = false;
            if (_entry != null && _results.Count > 0) SaveEntry();
            PanelProgress.Visibility = Visibility.Collapsed;
            if (_view != View.Found && _view != View.Uncertain) SectionDone(); else Render();
        }
    }

    private void SetBar(double pct)
    {
        BarProgress.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        BarProgress.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    private AccountStatus? ViewStatus => _view switch { View.Found => AccountStatus.Found, View.Uncertain => AccountStatus.Uncertain, _ => null };

    private void Flush()
    {
        bool any = false;
        while (_incoming.TryDequeue(out var r))
        {
            _results.Add(r); any = true;
            if (r.Status == ViewStatus) PanelResults.Children.Add(Row(r));   // ajout au fil de l'eau ; tri à la fin
        }
        if (!any) return;
        int done = _results.Count(x => x.Detail != ManualDetail);
        if (_total > 0) { double pct = Math.Min(100, done * 100.0 / _total); TxtPct.Text = $"{pct:0} %"; SetBar(pct); }
        UpdateCounts();
    }

    private string AccountsVerdict(out string level)
    {
        int f = _results.Count(r => r.Status == AccountStatus.Found), u = _results.Count(r => r.Status == AccountStatus.Uncertain);
        if (_searching)
        {
            level = "";
            int done = _results.Count(r => r.Detail != ManualDetail);
            return $"Recherche en cours : {done} / {_total} vérifications · {f} compte(s) trouvé(s)";
        }
        level = f > 0 ? "Warning" : "Success";
        return f == 0 && u == 0 ? "Aucun compte trouvé pour ce(s) pseudo(s)." : $"{f} compte(s) trouvé(s), {u} site(s) à vérifier à la main.";
    }

    private void UpdateCounts()
    {
        RefreshViews();
        if (_sumAccounts != null) { _sumAccounts.Text = AccountsVerdict(out var lv); _sumAccounts.Foreground = ThemeManager.GetBrush(lv.Length > 0 ? lv + "Text" : "TextMuted"); }
        if (_searching)
        {
            int f = _results.Count(r => r.Status == AccountStatus.Found);
            int done = _results.Count(r => r.Detail != ManualDetail);
            string eta = "";
            if (done >= 20 && _total > done)
            {
                var left = TimeSpan.FromSeconds(_clock.Elapsed.TotalSeconds / done * (_total - done));
                eta = left.TotalMinutes >= 1 ? $" · environ {Math.Ceiling(left.TotalMinutes):0} min restante(s)" : " · moins d'une minute";
            }
            TxtStatus.Text = $"{done} / {_total} vérifications · {f} compte(s) trouvé(s){eta}";
        }
    }

    // ─── Barre des rubriques ───
    private static int LevelRank(string level) => level switch { "Danger" => 4, "Warning" => 3, "Accent" => 2, "Success" => 1, _ => 0 };

    /// <summary>Verdict d'une rubrique de vérification (niveau vide = en cours).</summary>
    private (string Level, string Verdict) SectionState(View v)
    {
        if (!_sections.TryGetValue(v, out var p)) return ("", "");
        if (v != View.Services) return p.Tag is ValueTuple<string, string> s ? s : ("", "Vérification…");
        var subs = p.Children.OfType<StackPanel>().ToList();
        if (subs.Count == 0 || subs.Any(x => x.Tag == null)) return p.Tag is ValueTuple<string, string> e ? e : ("", "Vérification…");
        var states = subs.Select(x => (ValueTuple<string, string>)x.Tag).ToList();
        int leaked = states.Count(x => x.Item1 is "Danger" or "Warning");
        string worst = states.OrderByDescending(x => LevelRank(x.Item1)).First().Item1;
        return (worst, leaked == 0 ? $"Aucune fuite connue pour {(subs.Count == 1 ? "ce service" : $"ces {subs.Count} services")}."
                                   : $"{leaked} service(s) sur {subs.Count} ont subi une fuite : " + string.Join(", ", states.Where(x => x.Item1 is "Danger" or "Warning").Select(x => x.Item2.Split('»')[0].TrimStart('«', ' ').Trim())) + ".");
    }

    private (string Level, string Verdict) BreachedState()
    {
        var found = _results.Where(r => r.Status == AccountStatus.Found).ToList();
        if (_breaches == null) return ("", "Comparaison avec les fuites connues…");
        int n = ExposureService.MatchAccounts(found, _breaches).Select(h => h.Account.Site).Distinct().Count();
        return n == 0 ? ("Success", "Aucun de vos comptes trouvés n'est sur un site ayant subi une fuite connue.") : ("Danger", $"{n} de vos comptes sont sur des sites qui ont subi une fuite.");
    }

    private List<View> AvailableViews()
    {
        var views = new List<View>();
        bool acc = _accountsAsked || _results.Count > 0;
        if (acc) { views.Add(View.Found); views.Add(View.Uncertain); }
        if (_results.Any(r => r.Status == AccountStatus.Found)) views.Add(View.Breached);
        views.AddRange(CheckViews.Select(c => c.View).Where(_sections.ContainsKey));
        if (views.Count > 0) views.Insert(0, View.Summary);
        return views;
    }

    private string ViewTitle(View v) => v switch
    {
        View.Summary => "Vue d'ensemble",
        View.Found => $"Comptes trouvés ({_results.Count(r => r.Status == AccountStatus.Found)})",
        View.Uncertain => $"À vérifier ({_results.Count(r => r.Status == AccountStatus.Uncertain)})",
        View.Breached => "Fuites de mes comptes",
        _ => CheckViews.First(c => c.View == v).Title,
    };

    /// <summary>Met à jour la barre des rubriques sans recréer les boutons (un clic n'est jamais perdu pendant la recherche).</summary>
    private void RefreshViews()
    {
        var views = AvailableViews();
        foreach (var v in _viewButtons.Keys.Except(views).ToList()) { PanelViews.Children.Remove(_viewButtons[v]); _viewButtons.Remove(v); }
        for (int i = 0; i < views.Count; i++)
        {
            var v = views[i];
            if (!_viewButtons.TryGetValue(v, out var b))
            {
                b = new Button { Margin = new Thickness(6, 0, 0, 6), Tag = v };
                b.Click += (_, _) => { _view = v; Render(); };
                _viewButtons[v] = b;
            }
            if (PanelViews.Children.IndexOf(b) != i) { PanelViews.Children.Remove(b); PanelViews.Children.Insert(Math.Min(i, PanelViews.Children.Count), b); }
            var (level, verdict) = v switch
            {
                View.Found or View.Uncertain => _searching ? ("", "Recherche en cours") : ("", ""),
                View.Breached => BreachedState(),
                View.Summary => ("", ""),
                _ => SectionState(v),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            bool dot = v is not (View.Summary or View.Found or View.Uncertain);
            if (dot) content.Children.Add(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                                                        Fill = ThemeManager.GetBrush(level.Length > 0 ? level + "Text" : "TextMuted") });
            content.Children.Add(new TextBlock { Text = ViewTitle(v), VerticalAlignment = VerticalAlignment.Center });
            b.Content = content;
            b.ToolTip = verdict.Length > 0 ? verdict : null;
            b.SetResourceReference(StyleProperty, v == _view ? "PrimaryActionButton" : "ActionButton");
        }
        PanelViews.Visibility = views.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnHistory.SetResourceReference(StyleProperty, _view == View.History ? "PrimaryActionButton" : "ActionButton");
    }

    /// <summary>Vue d'ensemble : une ligne par rubrique (verdict coloré + accès au détail).</summary>
    private void RenderSummary()
    {
        _sumAccounts = null;
        var views = AvailableViews().Where(v => v is not (View.Summary or View.Uncertain)).ToList();
        if (views.Count == 0)
        {
            PanelResults.Children.Add(Txt("Remplissez dans le formulaire ce que vous voulez vérifier, puis « Tout vérifier ».", 13, "TextPrimary", true));
            PanelResults.Children.Add(Txt("• Pseudos : vos comptes sur plus de 500 sites (et environ 1 000 en recherche étendue), avec le lien pour supprimer chacun.\n" +
                                          "• E-mail et téléphone : les fuites connues où ils apparaissent (Have I Been Pwned), et où ils sont utilisés.\n" +
                                          "• Services : si un site dont vous êtes client a subi une fuite, et la demande RGPD pour récupérer ou effacer vos données.\n" +
                                          "• Prénom et nom : ce que les moteurs de recherche montrent de vous, et comment le faire retirer.\n" +
                                          "• Mot de passe : s'il circule dans les fuites (il ne quitte jamais ce PC, seuls 5 caractères de son empreinte sont envoyés).", 12));
            return;
        }
        PanelResults.Children.Add(Txt("Vue d'ensemble — cliquez une rubrique pour le détail et les actions (supprimer, demande RGPD…).", 12, "TextMuted"));
        foreach (var v in views)
        {
            string title = v switch { View.Found => "Comptes (pseudos)", View.Breached => "Fuites de mes comptes trouvés", _ => CheckViews.First(c => c.View == v).Title };
            string level, verdict;
            if (v == View.Found) verdict = AccountsVerdict(out level);
            else (level, verdict) = v == View.Breached ? BreachedState() : SectionState(v);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Txt(title, 13, "TextPrimary", true); t.VerticalAlignment = VerticalAlignment.Center;
            var d = Txt(verdict, 12.5, level.Length > 0 ? level + "Text" : "TextMuted", level is "Danger" or "Warning"); d.VerticalAlignment = VerticalAlignment.Center; d.Margin = new Thickness(0, 0, 12, 0);
            if (v == View.Found) _sumAccounts = d;
            Grid.SetColumn(d, 1);
            var go = new Button { Content = "Voir le détail", VerticalAlignment = VerticalAlignment.Center };
            go.SetResourceReference(StyleProperty, "ActionButton");
            go.Click += (_, _) => { _view = v; Render(); };
            Grid.SetColumn(go, 2);
            g.Children.Add(t); g.Children.Add(d); g.Children.Add(go);
            PanelResults.Children.Add(new Border { Child = g, Padding = new Thickness(4, 10, 4, 10), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    // ─── Historique des recherches (sur ce PC) ───
    private void SaveEntry()
    {
        if (_entry == null) return;
        _entry.Results = _results.ToList();
        _entry.SitesChecked = _results.Count(r => r.Detail != ManualDetail);
        try { AccountHistoryService.Save(_entry); } catch (Exception ex) { TxtStatus.Text = $"Historique non enregistré : {ex.Message}"; }
    }

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _view = View.History;
        Render();
    }

    internal void RenderHistory()
    {
        var list = AccountHistoryService.Load();
        var head = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        if (list.Count > 0)
        {
            var clear = new Button { Content = "Tout effacer" };
            clear.SetResourceReference(StyleProperty, "DangerActionButton");
            clear.Click += (_, _) =>
            {
                if (AppMessageBox.Show(this, "Effacer tout l'historique des recherches ?", "Historique", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                AccountHistoryService.Clear(); Render();
            };
            DockPanel.SetDock(clear, Dock.Right); head.Children.Add(clear);
        }
        head.Children.Add(Txt(list.Count == 0 ? "Aucune recherche de pseudos enregistrée." : $"{list.Count} recherche(s) de pseudos enregistrée(s) sur ce PC (les comptes absents, l'e-mail, le numéro et le mot de passe ne sont pas gardés).", 12));
        PanelResults.Children.Add(head);
        foreach (var e in list)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(Txt(string.Join(", ", e.Usernames.DefaultIfEmpty("(sans pseudo)")), 13, "TextPrimary", true));
            int f = e.Results.Count(r => r.Status == AccountStatus.Found), u = e.Results.Count(r => r.Status == AccountStatus.Uncertain);
            int manual = e.Results.Count(r => r.Detail == ManualDetail);
            sp.Children.Add(Txt($"{e.Date:dd/MM/yyyy HH:mm}  ·  {f} trouvé(s)" + (manual > 0 ? $" dont {manual} ajouté(s) à la main" : "") + $", {u} à vérifier" +
                                (e.Complete ? "" : "  ·  recherche arrêtée avant la fin"), 11.5));
            if (f > 0) sp.Children.Add(Txt(string.Join(", ", e.Results.Where(r => r.Status == AccountStatus.Found).Select(r => r.Site).Take(12)) + (f > 12 ? "…" : ""), 11.5, "TextMuted"));
            if (f > 0)
            {
                var states = e.Results.Where(r => r.Status == AccountStatus.Found).Select(StateOf).ToList();
                int nDel = states.Count(s => s == TrackState.Deleted), nReq = states.Count(s => s == TrackState.Requested), nTodo = states.Count(s => s == TrackState.ToDelete);
                sp.Children.Add(Txt($"Suivi : {nDel} supprimé(s) · {nReq} demande(s) envoyée(s) · {nTodo} à supprimer", 11.5, nDel == f ? "SuccessText" : "TextSecondary"));
            }
            g.Children.Add(sp);
            var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var show = new Button { Content = "Afficher", Margin = new Thickness(6, 0, 0, 0) };
            show.SetResourceReference(StyleProperty, "PrimaryActionButton");
            show.Click += (_, _) => ShowEntry(e);
            var del = new Button { Content = "Supprimer", Margin = new Thickness(6, 0, 0, 0) };
            del.SetResourceReference(StyleProperty, "ActionButton");
            del.Click += (_, _) => { AccountHistoryService.Delete(e.Id); if (_entry?.Id == e.Id) _entry = null; Render(); };
            actions.Children.Add(show); actions.Children.Add(del);
            Grid.SetColumn(actions, 1); g.Children.Add(actions);
            PanelResults.Children.Add(new Border { Child = g, Padding = new Thickness(4, 8, 4, 8), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    /// <summary>Réaffiche une recherche passée (ses comptes trouvés et à vérifier) ; un ajout manuel la met à jour.</summary>
    internal void ShowEntry(AccountSearchEntry e)
    {
        _entry = e;
        _results.Clear(); _results.AddRange(e.Results);
        TxtUsers.Text = string.Join(" ", e.Usernames);
        _multiUser = e.Usernames.Count > 1;
        _accountsAsked = true;
        _lastUsersKey = e.Complete ? string.Join(" ", e.Usernames.Select(u => u.ToLowerInvariant()).Order()) + (_extended ? " +étendue" : "") : "";
        _view = View.Found;
        BtnCopy.IsEnabled = BtnExport.IsEnabled = _results.Any(r => r.Status is AccountStatus.Found or AccountStatus.Uncertain);
        Render();
        TxtStatus.Text = $"Recherche du {e.Date:dd/MM/yyyy HH:mm} (historique). Relancez « Tout vérifier » pour une vérification à jour.";
    }

    // ─── Éléments communs des rubriques ───
    private readonly StackPanel _expAccounts = new();
    private Border? _breachedPanel;                      // construit une fois (_expAccounts ne peut avoir qu'un parent)
    private List<Breach>? _breaches;

    private Border Section(string title, string intro, params UIElement[] content)
    {
        var sp = new StackPanel();
        sp.Children.Add(Txt(title, 13.5, "TextPrimary", true));
        if (intro.Length > 0) { var t = Txt(intro, 11.5); t.Margin = new Thickness(0, 2, 0, 8); sp.Children.Add(t); }
        foreach (var c in content) sp.Children.Add(c);
        return new Border { Child = sp, Padding = new Thickness(4, 10, 4, 12) };
    }

    private Button Action(string text, string style, Func<Task> click, string tip = "")
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (tip.Length > 0) b.ToolTip = tip;
        b.SetResourceReference(StyleProperty, style);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await click(); } catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; } finally { b.IsEnabled = true; } };
        return b;
    }

    private static DockPanel Line(FrameworkElement field, params Button[] buttons)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var b in buttons.Reverse()) { DockPanel.SetDock(b, Dock.Right); d.Children.Add(b); }
        d.Children.Add(field);
        return d;
    }

    // ─── Clé d'API Have I Been Pwned (facultative) ───
    private PasswordBox _expKey = null!;
    private string ApiKey() => _expKey.Password.Trim().Length > 0 ? _expKey.Password.Trim() : ExposureService.LoadApiKey();

    private void BtnKey_Click(object sender, RoutedEventArgs e) =>
        PanelKey.Visibility = PanelKey.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void BuildKeyPanel()
    {
        _expKey = new PasswordBox { ToolTip = "Clé payante (~4 € / mois) obtenue sur haveibeenpwned.com/API/Key" }; _expKey.SetResourceReference(StyleProperty, "FieldPasswordBox");
        _expKey.KeyDown += (s, e) => { if (e.Key == Key.Enter) e.Handled = true; };
        var keyState = Txt("", 11, "TextMuted");
        void RefreshKeyState()
        {
            bool has = ExposureService.LoadApiKey().Length > 0;
            keyState.Text = has ? "Clé enregistrée : les fuites de l'e-mail et du numéro s'affichent directement ici." : "Aucune clé enregistrée : la vérification de l'e-mail et du numéro se fait sur le site Have I Been Pwned (gratuit).";
            keyState.Foreground = ThemeManager.GetBrush(has ? "SuccessText" : "TextMuted");
            BtnKey.Content = has ? "Clé Have I Been Pwned : enregistrée" : "Clé Have I Been Pwned";
        }
        var keyLine = Line(_expKey,
            Action("Obtenir une clé", "ActionButton", () => { CommandRunner.OpenUrlAsUser(ExposureService.ApiKeyPage); TxtStatus.Text = "Copiez la clé affichée sur le site, puis cliquez « Coller et retenir »."; return Task.CompletedTask; },
                   "Ouvre la page Have I Been Pwned où l'on obtient sa clé (abonnement payant, ~4 € / mois)"),
            Action("Coller et retenir", "PrimaryActionButton", () =>
            {
                // La clé saisie, sinon celle du presse-papiers : pas besoin de la coller soi-même
                string? key = ExposureService.ParseApiKey(_expKey.Password);
                if (key == null) { try { key = ExposureService.ParseApiKey(Clipboard.GetText()); } catch { } }
                if (key == null) { TxtStatus.Text = "Aucune clé valide (32 caractères) dans le champ ni dans le presse-papiers : copiez-la sur le site d'abord."; return Task.CompletedTask; }
                ExposureService.SaveApiKey(key); _expKey.Clear();
                try { if (ExposureService.ParseApiKey(Clipboard.GetText()) == key) Clipboard.Clear(); } catch { }   // ne pas la laisser traîner
                RefreshKeyState();
                TxtStatus.Text = "Clé enregistrée, chiffrée pour votre compte Windows."; return Task.CompletedTask;
            }, "Prend la clé du champ, ou directement du presse-papiers, et l'enregistre chiffrée (DPAPI)"),
            Action("Oublier", "ActionButton", () => { ExposureService.DeleteApiKey(); RefreshKeyState(); TxtStatus.Text = "Clé supprimée."; return Task.CompletedTask; }));
        PanelKey.Children.Add(Txt("Clé d'API Have I Been Pwned (facultative) : « Obtenir une clé », copiez-la sur le site, puis « Coller et retenir ».", 11, "TextMuted"));
        PanelKey.Children.Add(keyLine);
        PanelKey.Children.Add(keyState);
        RefreshKeyState();
    }

    /// <summary>Carte de résultat dans une rubrique : verdict coloré + contenu + actions. Le verdict est retenu pour la vue d'ensemble.</summary>
    private void ShowResult(StackPanel target, string level, string verdict, params UIElement[] content)
    {
        target.Children.Clear();
        target.Tag = (level, verdict);
        var sp = new StackPanel();
        sp.Children.Add(Txt(verdict, 13, level + "Text", true));
        foreach (var c in content) sp.Children.Add(c);
        target.Children.Add(new Border { Child = sp, Background = ThemeManager.GetBrush(level == "Accent" ? "BgInput" : level + "Bg"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12) });
    }

    private WrapPanel Links(params (string Label, string Url, string Tip)[] links)
    {
        var w = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        foreach (var (l, u, t) in links) w.Children.Add(LinkButton(l, "ActionButton", u, t));
        return w;
    }

    private static TextBlock Sub(string text) { var t = Txt(text, 12, "TextPrimary", true); t.Margin = new Thickness(0, 10, 0, 0); return t; }

    private string _knownEmail = "";   // adresse du formulaire : reprise dans les demandes RGPD

    /// <summary>Adresse du service connue de JustDeleteMe (souvent le contact « vie privée »), sinon vide.</summary>
    private string ServiceEmail(string service) => _jdm.Search(service, 1).FirstOrDefault() is { Email.Length: > 0 } d ? d.Email : "";

    /// <summary>Bouton « Demande RGPD » : e-mail prérempli (accès + effacement) ; onRequested = compte passé en « demande envoyée ».</summary>
    private Button GdprButton(string label, string service, Breach? breach, Action? onRequested = null)
    {
        var b = Action(label, "ActionButton", () =>
        {
            var letter = ExposureService.GdprRequest(service, breach, _knownEmail);
            CommandRunner.OpenUrlAsUser(ExposureService.GdprMailTo(ServiceEmail(service), letter));
            onRequested?.Invoke();
            TxtStatus.Text = ServiceEmail(service).Length > 0 ? $"E-mail préparé pour {service} : relisez-le, signez-le et envoyez-le."
                           : $"E-mail préparé : ajoutez l'adresse du délégué à la protection des données de {service} (bouton « Trouver l'adresse du DPO »).";
            return Task.CompletedTask;
        }, "Prépare un e-mail demandant vos données (art. 15) et leur suppression (art. 17) ; réponse obligatoire sous un mois");
        return b;
    }

    /// <summary>Panneau complet de demande RGPD pour un service : e-mail, copie de la lettre, DPO, CNIL.</summary>
    private StackPanel GdprPanel(string service, Breach? breach)
    {
        var sp = new StackPanel();
        sp.Children.Add(Sub("Demander mes données et leur suppression (RGPD)"));
        sp.Children.Add(Txt("Le service doit répondre sous un mois. Envoyez la demande à son délégué à la protection des données (DPO), dont l'adresse figure dans sa politique de confidentialité ; sans réponse, portez plainte auprès de la CNIL.", 11.5));
        var w = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        w.Children.Add(GdprButton("Préparer l'e-mail", service, breach));
        w.Children.Add(Action("Copier la lettre", "ActionButton", () =>
        {
            var (subject, body) = ExposureService.GdprRequest(service, breach, _knownEmail);
            try { Clipboard.SetText(subject + "\r\n\r\n" + body); TxtStatus.Text = "Lettre copiée : collez-la dans votre messagerie ou le formulaire de contact du service."; } catch { TxtStatus.Text = "Presse-papiers indisponible."; }
            return Task.CompletedTask;
        }, "Pour un formulaire de contact ou un courrier"));
        w.Children.Add(LinkButton("Trouver l'adresse du DPO", "ActionButton", ExposureService.DpoSearchUrl(service), "Recherche de l'adresse du délégué à la protection des données"));
        w.Children.Add(LinkButton("Modèle de la CNIL", "ActionButton", ExposureService.CnilErasureModel, "Modèle officiel de demande de suppression"));
        w.Children.Add(LinkButton("Plainte CNIL (sans réponse sous un mois)", "ActionButton", ExposureService.CnilComplaint, "Saisir la CNIL si le service ne répond pas"));
        sp.Children.Add(w);
        return sp;
    }

    // ─── Vérifications (une rubrique chacune) ───
    private async Task CheckEmailAsync(StackPanel t, string email)
    {
        string key = ApiKey();
        var signup = new StackPanel();
        signup.Children.Add(Sub("Être prévenu des prochaines fuites"));
        signup.Children.Add(Links(("Alerte à chaque nouvelle fuite (Mozilla Monitor)", ExposureService.MozillaMonitor, "Gratuit : surveille votre adresse et vous écrit à chaque nouvelle fuite (s'appuie sur Have I Been Pwned)")));
        signup.Children.Add(Sub("Sur quels sites cette adresse est inscrite"));
        var w = Links(("Chercher les e-mails d'inscription dans Gmail", ExposureService.GmailSearchUrl(email), "E-mails de bienvenue et de confirmation : chacun correspond à un compte créé"));
        var copy = Action("Copier la recherche (Outlook, Thunderbird…)", "ActionButton", () => { try { Clipboard.SetText(ExposureService.SignupMailQuery); TxtStatus.Text = "Recherche copiée."; } catch { } return Task.CompletedTask; });
        w.Children.Add(copy);
        signup.Children.Add(w);
        signup.Children.Add(Links(ExposureService.SocialLoginPages.Select(p => (p.Label, p.Url, "Sites où vous vous êtes inscrit avec ce compte")).ToArray()));
        var (gUrl, gName, gErr) = await ExposureService.GravatarAsync(email, Ct);
        var grav = Txt(gErr.Length > 0 ? "Gravatar : " + gErr : gUrl == null ? "Gravatar : aucun profil public pour cette adresse." :
            $"Gravatar : profil public{(gName.Length > 0 ? $" « {gName} »" : "")} — sa photo et son nom s'affichent sur WordPress, GitHub, Slack… ({gUrl})", 11.5, gUrl == null ? "TextSecondary" : "WarningText");
        grav.Margin = new Thickness(0, 8, 0, 0);

        if (key.Length == 0)
        {
            var site = Action("Vérifier sur Have I Been Pwned", "PrimaryActionButton", () =>
            {
                try { Clipboard.SetText(email); } catch { }
                CommandRunner.OpenUrlAsUser(ExposureService.WebCheckUrl(email)); return Task.CompletedTask;
            }, "Gratuit : ouvre le site avec votre adresse (aussi copiée)");
            site.Margin = new Thickness(0, 6, 0, 0); site.HorizontalAlignment = HorizontalAlignment.Left;
            ShowResult(t, "Accent", "Fuites : à vérifier sur le site Have I Been Pwned (gratuit)",
                Txt("Sans clé d'API, la réponse s'affiche sur leur site ; avec une clé (bouton « Clé Have I Been Pwned » du formulaire), elle s'affiche directement ici.", 11.5), site, grav, signup);
            return;
        }
        var (list, err) = await ExposureService.CheckEmailAsync(email, key, Ct);
        if (list == null) { ShowResult(t, "Warning", "Vérification impossible : " + err, grav, signup); return; }
        _leaks[View.Email] = list.Select(b => new ExposureService.LeakSource(b, "votre e-mail")).ToList();
        if (list.Count == 0) { ShowResult(t, "Success", "Cette adresse n'apparaît dans aucune fuite connue.", grav, signup); return; }
        var rows = new StackPanel();
        foreach (var b in list.OrderByDescending(x => x.Date)) rows.Children.Add(BreachRow(b));
        var requests = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
        foreach (var b in list.OrderByDescending(x => x.Date).Take(12)) requests.Children.Add(GdprButton($"Demande RGPD à {b.Title}", b.Title, b));
        ShowResult(t, "Danger", $"Cette adresse apparaît dans {list.Count} fuite(s) connue(s).",
            Txt("Changez le mot de passe de ces services (et partout où il a été réutilisé), activez la double authentification, méfiez-vous des e-mails qui reprennent vos informations.", 11.5, "DangerText"),
            rows, Sub("Demander à chaque service vos données et leur suppression (RGPD)"), requests,
            Links(("Plainte CNIL (sans réponse sous un mois)", ExposureService.CnilComplaint, "Saisir la CNIL si un service ne répond pas")), grav, signup);
    }

    private async Task CheckPasswordAsync(StackPanel t, string pwd)
    {
        long n = await ExposureService.PasswordCountAsync(pwd, Ct);
        var all = new StackPanel();
        all.Children.Add(Sub("Vérifier tous mes mots de passe enregistrés d'un coup"));
        all.Children.Add(Links(("Google (Chrome, Android)", ExposureService.GooglePasswordCheckup, "Vérification des mots de passe enregistrés dans votre compte Google")));
        all.Children.Add(Txt("Edge : Paramètres → Profils → Mots de passe → « Afficher les alertes de sécurité ». iPhone / Mac : app Mots de passe → « Alertes de sécurité ». Firefox : about:logins. Bitwarden, 1Password : rapport de sécurité / Watchtower.", 11.5));
        if (n == 0) ShowResult(t, "Success", "Ce mot de passe n'apparaît dans aucune fuite connue.", Txt("Ce qui ne garantit pas qu'il soit solide : préférez une longue phrase ou un gestionnaire de mots de passe.", 11.5), all);
        else ShowResult(t, "Danger", $"Ce mot de passe apparaît {n:N0} fois dans des fuites.", Txt("Ne l'utilisez plus nulle part et changez-le partout où il sert : il fait partie des listes essayées automatiquement par les pirates.", 11.5, "DangerText"), all);
    }

    private async Task CheckPhoneAsync(StackPanel t, string input)
    {
        string n = ExposureService.NormalizeFrPhone(input)!;   // validé par le formulaire
        string intl = ExposureService.PhoneIntl(n);
        var web = new StackPanel();
        web.Children.Add(Sub("Où apparaît-il sur le web"));
        web.Children.Add(Links(ExposureService.PhoneSearches(n).Select(s => (s.Label, s.Url, "Recherche sous toutes ses écritures : " + string.Join(", ", ExposureService.PhoneFormats(n).Take(4)))).ToArray()));
        web.Children.Add(Sub("Quels comptes l'utilisent"));
        web.Children.Add(Links(ExposureService.PhoneAccountPages.Select(p => (p.Label, p.Url, "À quoi sert votre numéro dans ce compte")).ToArray()));
        web.Children.Add(Sub("Le protéger"));
        web.Children.Add(Links(ExposureService.PhoneOfficial.Select(p => (p.Label, p.Url, p.Tip)).ToArray()));
        web.Children.Add(Txt("Ne communiquez jamais votre code RIO ni un code reçu par SMS ; demandez à votre opérateur s'il propose un code de sécurité pour les changements de carte SIM ; WhatsApp : activez la validation en deux étapes ; Telegram : Confidentialité → Numéro de téléphone → « Personne ».", 11.5, "TextSecondary"));
        if (_breaches == null) { try { _breaches = await ExposureService.GetBreachesAsync(Ct); } catch (OperationCanceledException) { throw; } catch { } }
        int phoneLeaks = _breaches == null ? 0 : ExposureService.RecentWithPhones(_breaches, 36, true).Count;
        var tip = phoneLeaks > 0 ? Txt($"{phoneLeaks} fuites françaises des 3 dernières années contenaient des numéros de téléphone : ajoutez votre opérateur et vos boutiques dans le champ « Services » du formulaire.", 11.5, "WarningText") : Txt("", 1);
        if (phoneLeaks > 0) tip.Margin = new Thickness(0, 6, 0, 0);

        string key = ApiKey();
        if (key.Length == 0)
        {
            var site = Action("Vérifier sur Have I Been Pwned", "PrimaryActionButton", () =>
            {
                try { Clipboard.SetText(intl); } catch { }
                CommandRunner.OpenUrlAsUser("https://haveibeenpwned.com/");
                TxtStatus.Text = $"Numéro copié au format {intl} : collez-le dans la recherche du site."; return Task.CompletedTask;
            }, "Seule la fuite Facebook 2021 y est cherchable par numéro");
            site.Margin = new Thickness(0, 6, 0, 0); site.HorizontalAlignment = HorizontalAlignment.Left;
            ShowResult(t, "Accent", "Fuites : à vérifier sur Have I Been Pwned (fuite Facebook 2021)",
                Txt($"Le numéro est copié au format {intl} quand vous cliquez : collez-le dans la recherche du site. Avec une clé d'API, la réponse s'affiche ici.", 11.5), site, tip, web);
            return;
        }
        var (list, err) = await ExposureService.CheckPhoneAsync(n, key, Ct);
        if (list == null) { ShowResult(t, "Warning", "Vérification impossible : " + err, tip, web); return; }
        _leaks[View.Phone] = list.Select(b => new ExposureService.LeakSource(b, "votre numéro")).ToList();
        if (list.Count == 0) { ShowResult(t, "Success", "Ce numéro n'est pas dans la fuite Facebook 2021 (seule fuite où un numéro est cherchable).", tip, web); return; }
        var rows = new StackPanel();
        foreach (var b in list) rows.Children.Add(BreachRow(b));
        ShowResult(t, "Danger", $"Ce numéro apparaît dans {list.Count} fuite(s).", Txt("Attendez-vous à des SMS et appels frauduleux ; ne donnez jamais de code reçu par SMS.", 11.5, "DangerText"), rows, tip, web);
    }

    /// <summary>Tous les services saisis : une carte par service dans la rubrique « Services ».</summary>
    private async Task CheckServicesAsync(StackPanel sec, List<string> services)
    {
        try
        {
            if (_breaches == null) _breaches = await ExposureService.GetBreachesAsync(Ct);
            if (_jdm.Count == 0) { try { _jdm = await JustDeleteMeIndex.LoadAsync(Ct); } catch (OperationCanceledException) { throw; } catch { } }
        }
        catch (OperationCanceledException) { ShowResult(sec, "Warning", "Vérification arrêtée."); SectionDone(); return; }
        catch (Exception ex) { ShowResult(sec, "Danger", "Liste des fuites indisponible : " + ex.Message); SectionDone(); return; }
        sec.Children.Clear();
        _leaks[View.Services] = new();
        var subs = services.Select(_ => new StackPanel { Margin = new Thickness(0, 0, 0, 10) }).ToList();
        foreach (var s in subs) sec.Children.Add(s);
        for (int i = 0; i < services.Count; i++)
        {
            var s = services[i];
            await Guard(subs[i], t => CheckServiceAsync(t, s));
        }
    }

    private async Task CheckServiceAsync(StackPanel t, string query)
    {
        if (_breaches == null) _breaches = await ExposureService.GetBreachesAsync(Ct);
        var list = ExposureService.SearchService(_breaches, query);
        var del = _jdm.Search(query, 1).FirstOrDefault();
        var deleteLinks = new WrapPanel { Margin = new Thickness(-6, 8, 0, 0) };
        if (del != null && del.Url.Length > 0 && del.Difficulty != "impossible")
            deleteLinks.Children.Add(LinkButton($"Supprimer mon compte {del.Name} ({del.DifficultyLabel.ToLowerInvariant()})", "ActionButton", del.Url, "Page de suppression du compte (JustDeleteMe)"));
        string asOf = _breaches.Count > 0 ? $"liste Have I Been Pwned à jour du {_breaches.Max(b => b.Date):dd/MM/yyyy}" : "";
        string display = query.Length > 1 ? char.ToUpper(query[0]) + query[1..] : query;
        if (list.Count == 0)
        {
            ShowResult(t, "Success", $"« {display} » : aucune fuite connue.",
                Txt($"D'après la {asOf}. Une fuite peut exister sans y être encore répertoriée ; vérifiez aussi l'orthographe (ex. « Bouygues », « SFR », « Leboncoin »).", 11.5), deleteLinks,
                GdprPanel(display, null));
            await Task.Yield();
            return;
        }
        if (_leaks.TryGetValue(View.Services, out var acc)) acc.AddRange(list.Select(b => new ExposureService.LeakSource(b, $"client de {display}")));
        var rows = new StackPanel();
        foreach (var b in list.Take(10)) rows.Children.Add(BreachRow(b));
        bool pwd = list.Any(b => b.PasswordsLeaked), phone = list.Any(b => b.DataClasses.Contains("Phone numbers"));
        var advice = "Si vous étiez client à ce moment-là, vos données ont probablement fuité. " + (pwd ? "Changez le mot de passe de ce service et partout où il a été réutilisé. " : "") +
                     (phone ? "Méfiez-vous des SMS et appels qui se font passer pour ce service ou votre banque. " : "") + "Ne cliquez pas sur les liens des e-mails qui reprennent vos informations.";
        ShowResult(t, pwd ? "Danger" : "Warning", $"« {list[0].Title} » : {list.Count} fuite(s) connue(s), la plus récente en {list[0].Date:MMMM yyyy}.",
            Txt(advice, 11.5, pwd ? "DangerText" : "WarningText"), rows, Txt(asOf, 11, "TextMuted"), deleteLinks, GdprPanel(list[0].Title, list[0]));
    }

    private void CheckName(StackPanel t, string name, List<string> users)
    {
        var searches = ExposureService.NameSearches(name, users).Select(s => (s.Label, s.Url, "Recherche du nom entre guillemets")).ToArray();
        ShowResult(t, "Accent", "À voir : ce que les moteurs de recherche montrent de vous",
            Txt("Aucun service légal ne permet de chercher un nom dans les fuites de données (ce serait exploiter des données volées). Ce qui est possible : voir ce que les moteurs de recherche montrent à votre sujet, puis demander le retrait.", 11.5),
            Sub("Rechercher"), Links(searches),
            Sub("Faire retirer"), Links(("Google : résultats vous concernant", ExposureService.GoogleResultsAboutYou, "Demander à Google de retirer vos coordonnées"),
                                         ("CNIL : droit au déréférencement", ExposureService.CnilDereferencing, "La démarche expliquée par la CNIL")));
    }

    private void CheckAddress(StackPanel t, string address, string city, string name)
    {
        var searches = ExposureService.AddressSearches(address, city, name).Select(s => (s.Label, s.Url, "Recherche entre guillemets, lancée par vous dans votre navigateur")).ToList();
        searches.Add(("Pages Blanches : cherchez votre nom et votre ville", ExposureService.PagesBlanches, "L'annuaire des particuliers : vérifiez si votre adresse y figure"));
        ShowResult(t, "Accent", "À voir : ce qui est publié avec votre adresse",
            Txt("Aucun service légal ne permet de chercher une adresse dans les fuites de données. Pour savoir si un service a perdu votre adresse, voyez la rubrique « Ce qui a fuité » (adresse postale) ; ici, vérifiez ce qui est publié sur le web et dans les annuaires.", 11.5),
            Sub("Rechercher"), Links(searches.ToArray()),
            Sub("Faire retirer"), Links(("Liste rouge : ne plus figurer dans les annuaires (CNIL)", ExposureService.CnilDirectory, "Gratuit : se demande à votre opérateur"),
                                         ("Google : résultats vous concernant", ExposureService.GoogleResultsAboutYou, "Demander à Google de retirer votre adresse ou votre numéro"),
                                         ("CNIL : droit au déréférencement", ExposureService.CnilDereferencing, "La démarche expliquée par la CNIL")));
    }

    /// <summary>Toutes les fuites qui vous concernent : e-mail, numéro, services dont vous êtes client, comptes trouvés.</summary>
    private List<ExposureService.LeakSource> LeakSources()
    {
        var all = _leaks.Where(kv => _sections.ContainsKey(kv.Key)).SelectMany(kv => kv.Value).ToList();
        var found = _results.Where(r => r.Status == AccountStatus.Found && StateOf(r) != TrackState.Deleted).ToList();
        if (_breaches != null && found.Count > 0)
            all.AddRange(ExposureService.MatchAccounts(found, _breaches).Select(h => new ExposureService.LeakSource(h.Breach, $"compte {h.Account.Site}")));
        return all;
    }

    /// <summary>Rubriques déduites des autres : « Ce qui a fuité » (synthèse par type d'information) et « IBAN ».</summary>
    private void UpdateDerived()
    {
        var sources = LeakSources();
        if (sources.Count == 0) _sections.Remove(View.Leaked);
        else
        {
            if (!_sections.TryGetValue(View.Leaked, out var p)) _sections[View.Leaked] = p = new StackPanel();
            BuildLeaked(p, sources);
        }
        if (_ibanInfo is { } info && _sections.TryGetValue(View.Iban, out var ib)) BuildIban(ib, info, sources);
    }

    private TextBlock Para(string text, string brush = "TextSecondary", bool bold = false) { var t = Txt(text, 11.5, brush, bold); t.Margin = new Thickness(0, 3, 0, 0); return t; }

    private static string Lower(string s) => s.Length > 1 && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string When(Breach b) => b.Date.ToString("MMMM yyyy", new System.Globalization.CultureInfo("fr-FR"));

    private void BuildLeaked(StackPanel t, List<ExposureService.LeakSource> sources)
    {
        var merged = ExposureService.MergeSources(sources);
        var groups = ExposureService.GroupLeaks(sources);
        var content = new List<UIElement>();
        var theft = merged.Where(m => ExposureService.IdentityTheftRisk(m.Breach)).ToList();
        if (theft.Count > 0)
        {
            content.Add(Para($"Usurpation d'identité possible : {string.Join(", ", theft.Select(m => $"{m.Breach.Title} ({When(m.Breach)})"))} contenai{(theft.Count > 1 ? "en" : "")}t ensemble votre nom, votre date de naissance ou votre adresse, et vos coordonnées bancaires ou une pièce d'identité.", "DangerText", true));
            content.Add(Links(("Usurpation d'identité : que faire (service-public)", ExposureService.ServicePublicIdentityTheft, "Démarches officielles"),
                              ("Consulter mes fichiers Banque de France (FICP, FCC)", ExposureService.BanqueFranceFiles, "Vérifier qu'aucun crédit ou incident n'est à votre nom")));
        }
        foreach (var (cat, src) in groups)
        {
            content.Add(Sub(cat.Title));
            content.Add(Para("Dans : " + string.Join(" ; ", src.Select(s => $"{s.Breach.Title} ({When(s.Breach)}, {s.Via})")), cat.Level + "Text", true));
            content.Add(Para(cat.Risk));
            content.Add(Para("À faire : " + cat.Advice, "TextPrimary"));
            if (cat.Links.Length > 0) content.Add(Links(cat.Links));
        }
        content.Add(Sub("Fuite par fuite : ce qui a fuité ensemble"));
        foreach (var (b, via) in merged)
        {
            content.Add(Para($"{b.Title} — {When(b)} — {via}", "TextPrimary", true));
            content.Add(Para("Ensemble : " + string.Join(", ", ExposureService.CategoriesOf(b).Select(c => Lower(c.Title)))));
            var w = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
            w.Children.Add(GdprButton("Demander exactement mes données (RGPD)", b.Title, b));
            content.Add(w);
        }
        string note = "Ces informations viennent de la liste publique des fuites : elles disent ce que chaque fuite contenait, pas une copie de vos données. " +
                      "Pour savoir précisément ce qu'un service détenait sur vous et ce qui a fuité, faites-lui une demande RGPD (réponse obligatoire sous un mois).";
        if (_sections.ContainsKey(View.Email) && !_leaks.ContainsKey(View.Email)) note += " Les fuites de votre e-mail ne sont pas comptées : il faut une clé Have I Been Pwned (bouton du formulaire).";
        content.Add(Para(note, "TextMuted"));
        content.Add(Links(("Fuite de données : que faire (Cybermalveillance)", ExposureService.CyberLeakSheet, "Fiche réflexe officielle")));
        bool severe = groups.Any(g => g.Category.Level == "Danger");
        ShowResult(t, severe ? "Danger" : "Warning",
            $"{groups.Count} type(s) d'information vous concernant ont fuité, dans {merged.Count} fuite(s) : " + string.Join(", ", groups.Select(g => Lower(g.Category.Title))) + ".",
            content.ToArray());
    }

    private void BuildIban(StackPanel t, (string Masked, string Country, string? Bank) info, List<ExposureService.LeakSource> sources)
    {
        var bank = ExposureService.LeakCategories.First(c => c.Key == "bank");
        var bankLeaks = ExposureService.MergeSources(sources).Where(m => bank.Classes.Any(m.Breach.DataClasses.Contains)).ToList();
        var head = Para($"IBAN {info.Masked} — {info.Country}{(info.Bank != null ? $", {info.Bank}" : "")} — clé de contrôle correcte. Il n'a été ni envoyé ni enregistré.", "TextMuted");
        var protect = new StackPanel();
        protect.Children.Add(Sub("Se protéger dans tous les cas"));
        protect.Children.Add(Para(bank.Advice, "TextPrimary"));
        protect.Children.Add(Links(bank.Links));
        if (bankLeaks.Count == 0)
        {
            ShowResult(t, "Success", "Aucune fuite connue de vos services ne contenait de coordonnées bancaires.", head,
                Para("D'après les services, l'e-mail, le numéro et les comptes vérifiés ici. Ajoutez dans « Services » votre banque, votre opérateur, vos fournisseurs d'énergie et les boutiques où vous avez payé pour une vérification plus complète."),
                protect);
            return;
        }
        var asks = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
        foreach (var (b, _) in bankLeaks.Take(6)) asks.Children.Add(GdprButton($"Demander à {b.Title} si mon IBAN a fuité", b.Title, b));
        ShowResult(t, "Danger", "Votre IBAN a probablement fuité : " + string.Join(", ", bankLeaks.Select(m => $"{m.Breach.Title} ({When(m.Breach)})")) + ".", head,
            Para(string.Join(" ; ", bankLeaks.Select(m => $"{m.Breach.Title} : {m.Via}")), "DangerText", true),
            Para(bank.Risk),
            Sub("Le savoir avec certitude"), Para("Seul le service peut confirmer si votre IBAN faisait partie des données volées : la demande RGPD l'y oblige sous un mois."), asks,
            protect);
    }

    internal async Task RenderBreachedAccountsAsync()
    {
        _expAccounts.Children.Clear();
        var found = _results.Where(r => r.Status == AccountStatus.Found).ToList();
        if (found.Count == 0) { _expAccounts.Children.Add(Txt("Saisissez un pseudo dans le formulaire puis « Tout vérifier » : vos comptes trouvés seront comparés aux fuites connues.", 12, "TextMuted")); return; }
        _expAccounts.Children.Add(Txt("Comparaison avec les fuites connues…", 12, "TextMuted"));
        try { _breaches ??= await ExposureService.GetBreachesAsync(_ct); }
        catch (Exception ex) { _expAccounts.Children.Clear(); _expAccounts.Children.Add(Txt($"Liste des fuites indisponible : {ex.Message}", 12, "DangerText")); return; }
        _expAccounts.Children.Clear();
        var hits = ExposureService.MatchAccounts(found, _breaches);
        if (hits.Count == 0) { _expAccounts.Children.Add(Txt($"Aucun de vos {found.Select(r => r.Site).Distinct().Count()} comptes trouvés n'est sur un site ayant subi une fuite connue.", 12.5, "SuccessText", true)); return; }
        _expAccounts.Children.Add(Txt($"{hits.Select(h => h.Account.Site).Distinct().Count()} de vos comptes sont sur des sites qui ont subi une fuite. Changez le mot de passe (et partout où vous l'avez réutilisé), ou supprimez le compte.", 12, "WarningText", true));
        // Une carte par compte : ses fuites, son état de suivi et les actions de suppression (mêmes données que la rubrique Comptes trouvés)
        foreach (var grp in hits.GroupBy(h => h.Account.Site + "|" + h.Account.Username)
                               .OrderBy(g => StateRank(StateOf(g.First().Account))).ThenByDescending(g => g.Any(h => h.Breach.PasswordsLeaked)))
        {
            var acc = grp.First().Account;
            var state = StateOf(acc);
            var card = new StackPanel();
            var head = new WrapPanel();
            head.Children.Add(Txt(acc.Site + (_multiUser ? $"  —  {acc.Username}" : ""), 13, "TextPrimary", true));
            head.Children.Add(Badge(AccountTrackingService.StateLabel(state), state == TrackState.Deleted ? "Success" : state == TrackState.Requested ? "Accent" : state == TrackState.Keep ? "TextMuted" : "Warning"));
            card.Children.Add(head);
            foreach (var (_, b) in grp) card.Children.Add(BreachRow(b, acc.Site));
            var acts = new WrapPanel { Margin = new Thickness(-6, 2, 0, 0) };
            var del = _jdm.Find(acc.UrlMain, acc.Site);
            if (state is TrackState.ToDelete or TrackState.Requested && del != null && del.Url.Length > 0 && del.Difficulty != "impossible") acts.Children.Add(DeleteButton(acc, del));
            acts.Children.Add(LinkButton("Ouvrir le profil", "ActionButton", acc.Url, "Ouvre la page dans votre navigateur"));
            if (state is TrackState.ToDelete or TrackState.Requested)
                acts.Children.Add(GdprButton("Demande RGPD", acc.Site, grp.Select(h => h.Breach).OrderByDescending(b => b.Date).First(),
                                             () => { if (StateOf(acc) == TrackState.ToDelete) SetTrack(acc, TrackState.Requested); }));
            card.Children.Add(acts);
            card.Children.Add(TrackButtons(acc));
            _expAccounts.Children.Add(new Border { Child = card, Padding = new Thickness(0, 6, 0, 10), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    private Border BreachRow(Breach b, string? site = null)
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Txt((site != null && !site.Equals(b.Title, StringComparison.OrdinalIgnoreCase) ? $"{site} — fuite « {b.Title} »" : b.Title) + $"  ·  {b.Year}  ·  {b.PwnCount:N0} comptes", 12.5, "TextPrimary", true));
        if (b.PasswordsLeaked)
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(10, 0, 0, 0), Background = ThemeManager.GetBrush("DangerBg") };
            badge.Child = Txt("mots de passe exposés", 11, "DangerText", true);
            head.Children.Add(badge);
        }
        sp.Children.Add(head);
        sp.Children.Add(Txt("Données : " + b.DataFr, 11.5));
        return new Border { Child = sp, Padding = new Thickness(0, 6, 0, 6) };
    }

    private string FirstUser() => AccountFinderService.ParseUsernames(TxtUsers.Text).Valid.FirstOrDefault() ?? "";

    // ─── Liens entre suivi, fiabilité, fuites et suppression ───
    private Dictionary<string, TrackEntry> _track = AccountTrackingService.LoadTracking();
    private Dictionary<string, SiteStat> _stats = AccountTrackingService.LoadStats();
    private readonly Dictionary<string, List<Breach>> _breachCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AccountSite>? _sitesByName;   // sites de la dernière recherche (revérification, nouvel essai)

    private TrackState StateOf(AccountResult r) => AccountTrackingService.StateOf(_track, r);
    private TrackEntry? TrackOf(AccountResult r) => _track.TryGetValue(AccountTrackingService.Key(r.Site, r.Username), out var t) ? t : null;

    private void SetTrack(AccountResult r, TrackState s)
    {
        try { AccountTrackingService.SetState(r, s); } catch (Exception ex) { TxtStatus.Text = $"Suivi non enregistré : {ex.Message}"; return; }
        _track = AccountTrackingService.LoadTracking();
        TxtStatus.Text = $"{r.Site} : {AccountTrackingService.StateLabel(s).ToLowerInvariant()}.";
        UpdateDerived();
        Render();
    }

    /// <summary>Fuites connues du site de ce compte (liste Have I Been Pwned, croisement local).</summary>
    private List<Breach> BreachesOf(AccountResult r)
    {
        if (_breaches == null) return new();
        if (!_breachCache.TryGetValue(r.Site, out var list))
            _breachCache[r.Site] = list = ExposureService.MatchAccounts(new[] { r with { Status = AccountStatus.Found } }, _breaches).Select(x => x.Breach).ToList();
        return list;
    }

    /// <summary>Charge en arrière-plan les liens de suppression et la liste des fuites, puis réaffiche.</summary>
    private async Task EnsureLinksAsync()
    {
        bool changed = false;
        if (_jdm.Count == 0) { try { _jdm = await JustDeleteMeIndex.LoadAsync(_ct); changed = true; } catch { } }
        if (_breaches == null) { try { _breaches = await ExposureService.GetBreachesAsync(_ct); _breachCache.Clear(); changed = true; } catch { } }
        if (changed) UpdateDerived();
        if (changed && !_searching) Render();
    }

    private static int StateRank(TrackState s) => s switch { TrackState.ToDelete => 0, TrackState.Requested => 1, TrackState.Keep => 2, _ => 3 };

    private IEnumerable<AccountResult> Visible()
    {
        var status = ViewStatus ?? AccountStatus.Found;
        var rows = _results.Where(r => r.Status == status);
        if (status == AccountStatus.Uncertain)   // le plus probable d'abord : sites habituellement fiables
            return rows.OrderByDescending(r => _stats.TryGetValue(r.Site, out var st) ? st.Reliability : 0.5).ThenBy(r => r.Site, StringComparer.OrdinalIgnoreCase);
        // À traiter d'abord : à supprimer, mots de passe exposés, suppression facile
        return rows.OrderBy(r => StateRank(StateOf(r)))
                   .ThenBy(r => BreachesOf(r).Any(b => b.PasswordsLeaked) ? 0 : 1)
                   .ThenBy(r => _jdm.Find(r.UrlMain, r.Site)?.Rank ?? 5)
                   .ThenBy(r => r.Site, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Username);
    }

    internal void Render()
    {
        PanelResults.Children.Clear();
        _sumAccounts = null;
        if (!AvailableViews().Contains(_view) && _view != View.History) _view = View.Summary;
        RefreshViews();
        switch (_view)
        {
            case View.History: RenderHistory(); return;
            case View.Summary: RenderSummary(); return;
            case View.Breached:
                _breachedPanel ??= Section("Mes comptes trouvés touchés par une fuite", "Vos comptes trouvés comparés aux fuites connues (aucune donnée envoyée) : changez le mot de passe ou supprimez le compte.", _expAccounts);
                PanelResults.Children.Add(_breachedPanel);
                _ = RenderBreachedAccountsAsync();
                return;
            case View.Found or View.Uncertain: break;
            default:
                if (_sections.TryGetValue(_view, out var sec))
                {
                    var title = Txt(CheckViews.First(c => c.View == _view).Title, 13.5, "TextPrimary", true); title.Margin = new Thickness(0, 4, 0, 8);
                    PanelResults.Children.Add(title);
                    PanelResults.Children.Add(sec);
                }
                return;
        }
        var rows = Visible().ToList();
        bool found = _view == View.Found;
        if (found && !_searching) PanelResults.Children.Add(TrackingSummary(rows));
        if (rows.Count == 0)
        {
            PanelResults.Children.Add(Txt(_searching ? "Recherche en cours…" : found ? "Aucun compte trouvé pour ce(s) pseudo(s)." : "Rien à vérifier.", 12.5));
            return;
        }
        if (!found)
            PanelResults.Children.Add(Txt("Ces sites n'ont pas pu être vérifiés automatiquement (protection anti-robots, refus, lenteur) : ouvrez le lien pour voir si le profil existe. Les sites habituellement fiables sont en tête.", 11.5, "WarningText"));
        foreach (var r in rows) PanelResults.Children.Add(Row(r));
    }

    /// <summary>Bandeau de suivi : où en sont les suppressions, fuites, et revérification.</summary>
    private Border TrackingSummary(List<AccountResult> rows)
    {
        int todo = rows.Count(r => StateOf(r) == TrackState.ToDelete), req = rows.Count(r => StateOf(r) == TrackState.Requested);
        int done = rows.Count(r => StateOf(r) == TrackState.Deleted), keep = rows.Count(r => StateOf(r) == TrackState.Keep);
        int leaked = rows.Count(r => StateOf(r) != TrackState.Deleted && BreachesOf(r).Any(b => b.PasswordsLeaked));
        int toRecheck = _track.Values.Count(t => t.State is TrackState.Requested or TrackState.Deleted);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(Txt($"Suivi : {todo} à supprimer · {req} demande(s) envoyée(s) · {done} supprimé(s) · {keep} à garder", 12.5, "TextPrimary", true));
        if (leaked > 0) sp.Children.Add(Txt($"{leaked} compte(s) sur des sites dont les mots de passe ont fuité : à traiter en priorité (en tête de liste).", 11.5, "DangerText", true));
        if (_breaches == null) sp.Children.Add(Txt("Comparaison avec les fuites connues en cours de chargement…", 11, "TextMuted"));
        g.Children.Add(sp);
        if (toRecheck > 0)
        {
            var b = new Button { Content = $"Revérifier ({toRecheck})", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                                 ToolTip = "Réinterroge les comptes « demande envoyée » et « supprimé » pour confirmer qu'ils ont disparu" };
            b.SetResourceReference(StyleProperty, "ActionButton");
            b.Click += async (_, _) => { b.IsEnabled = false; try { await RecheckAsync(); } finally { b.IsEnabled = true; } };
            Grid.SetColumn(b, 1); g.Children.Add(b);
        }
        return new Border { Child = g, Background = ThemeManager.GetBrush("BgInput"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 6) };
    }

    private Button LinkButton(string text, string style, string url, string tip)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = tip };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => { try { CommandRunner.OpenUrlAsUser(url); } catch (Exception ex) { TxtStatus.Text = $"Ouverture impossible : {ex.Message}"; } };
        return b;
    }

    private static Border Badge(string text, string level)
    {
        var b = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 0, 0),
                             Background = ThemeManager.GetBrush(level == "TextMuted" ? "BgInput" : level + "Bg") };
        b.Child = Txt(text, 11, level == "TextMuted" ? "TextMuted" : level + "Text", true);
        return b;
    }

    /// <summary>Boutons d'état de suivi (cliquer l'état actif revient à « à supprimer »).</summary>
    private WrapPanel TrackButtons(AccountResult r)
    {
        var p = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        var cur = StateOf(r);
        foreach (var s in new[] { TrackState.Requested, TrackState.Deleted, TrackState.Keep })
        {
            var b = new Button { Content = AccountTrackingService.StateLabel(s), Margin = new Thickness(6, 0, 0, 0), FontSize = 11.5, Padding = new Thickness(10, 3, 10, 3) };
            b.SetResourceReference(StyleProperty, s == cur ? "PrimaryActionButton" : "ActionButton");
            var target = s == cur ? TrackState.ToDelete : s;
            b.ToolTip = s == cur ? "Cliquer pour remettre « à supprimer »" : $"Marquer : {AccountTrackingService.StateLabel(s).ToLowerInvariant()}";
            b.Click += (_, _) => SetTrack(r, target);
            p.Children.Add(b);
        }
        return p;
    }

    /// <summary>Bouton « Supprimer le compte » relié au suivi : l'ouvrir passe le compte en « demande envoyée ».</summary>
    private Button DeleteButton(AccountResult r, DeletionInfo del)
    {
        var b = LinkButton("Supprimer le compte", "DangerActionButton", del.Url, "Ouvre la page de suppression (le compte passe en « demande envoyée »)");
        b.Click += (_, _) => { if (StateOf(r) == TrackState.ToDelete) SetTrack(r, TrackState.Requested); };
        return b;
    }

    private Border Row(AccountResult r)
    {
        var del = _jdm.Find(r.UrlMain, r.Site);
        var state = StateOf(r); var tr = TrackOf(r);
        var breaches = BreachesOf(r);
        var g = new Grid { Opacity = state is TrackState.Deleted or TrackState.Keep ? 0.75 : 1 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var head = new WrapPanel();
        head.Children.Add(Txt(r.Site + (_multiUser ? $"  —  {r.Username}" : ""), 13, "TextPrimary", true));
        if (r.Status == AccountStatus.Found && state != TrackState.ToDelete)
            head.Children.Add(Badge(AccountTrackingService.StateLabel(state), state == TrackState.Deleted ? "Success" : state == TrackState.Requested ? "Accent" : "TextMuted"));
        if (del != null) head.Children.Add(Badge(del.DifficultyLabel, del.Level));
        foreach (var b in breaches.OrderByDescending(x => x.Date).Take(2))   // les plus récentes d'abord
        {
            var bb = Badge($"fuite {b.Year}" + (b.PasswordsLeaked ? " · mots de passe" : ""), b.PasswordsLeaked ? "Danger" : "Warning");
            bb.ToolTip = $"{b.Title} ({b.Date:dd/MM/yyyy}, {b.PwnCount:N0} comptes) : {b.DataFr}";
            head.Children.Add(bb);
        }
        if (breaches.Count > 2)
        {
            var more = Badge($"+{breaches.Count - 2} fuite(s)", "Warning");
            more.ToolTip = string.Join("\n", breaches.OrderByDescending(x => x.Date).Skip(2).Select(x => $"{x.Title} ({x.Date:dd/MM/yyyy}) : {x.DataFr}"));
            head.Children.Add(more);
        }
        info.Children.Add(head);
        info.Children.Add(Txt(r.Url + (r.Detail.Length > 0 ? $"  ·  {r.Detail}" : ""), 11.5));
        if (r.Status == AccountStatus.Uncertain && _stats.TryGetValue(r.Site, out var st) && st.Label.Length > 0)
            info.Children.Add(Txt($"Historique de ce site : {st.Label}.", 11, st.Reliability >= 0.8 ? "SuccessText" : "TextMuted"));
        if (breaches.Any(b => b.PasswordsLeaked) && state != TrackState.Deleted)
            info.Children.Add(Txt("Mot de passe exposé dans une fuite : changez-le sur ce site et partout où vous l'avez réutilisé, ou supprimez le compte.", 11.5, "DangerText"));
        if (tr != null && state != TrackState.ToDelete)
        {
            string line = $"{AccountTrackingService.StateLabel(state)} le {tr.Changed:dd/MM/yyyy}";
            if (tr.Rechecked is DateTime rc) line += tr.StillOnline == true ? $" · revérifié le {rc:dd/MM} : encore en ligne" : $" · revérifié le {rc:dd/MM} : disparu";
            else if (state == TrackState.Deleted && DateTime.Now - tr.Changed < TimeSpan.FromDays(30)) line += $" · beaucoup de sites gardent le compte 30 jours : revérifier après le {tr.Changed.AddDays(30):dd/MM}";
            info.Children.Add(Txt(line, 11, tr.StillOnline == true ? "WarningText" : "TextMuted"));
        }
        if (del?.Notes is { Length: > 0 } notes && state is TrackState.ToDelete or TrackState.Requested)
            info.Children.Add(Txt(notes.Length > 260 ? notes[..260] + "…" : notes, 11.5, "TextMuted"));
        if (r.Status == AccountStatus.Found) info.Children.Add(TrackButtons(r));
        g.Children.Add(info);

        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(LinkButton("Ouvrir le profil", "ActionButton", r.Url, "Ouvre la page dans votre navigateur (avec vos comptes connectés)"));
        if ((r.Status == AccountStatus.Found || r.Status == AccountStatus.Uncertain) && state is TrackState.ToDelete or TrackState.Requested)
        {
            if (del != null && del.Url.Length > 0 && del.Difficulty != "impossible") actions.Children.Add(DeleteButton(r, del));
            if (del != null && del.Email.Length > 0)
            {
                var m = LinkButton("E-mail de suppression", "ActionButton", del.MailTo(r.Username), $"Prépare un e-mail de demande de suppression à {del.Email} (le compte passe en « demande envoyée »)");
                m.Click += (_, _) => { if (r.Status == AccountStatus.Found && StateOf(r) == TrackState.ToDelete) SetTrack(r, TrackState.Requested); };
                actions.Children.Add(m);
            }
            if (del == null) actions.Children.Add(LinkButton("Comment le supprimer ?", "ActionButton", JustDeleteMeIndex.SearchUrl(r.Site), "Ce site n'est pas dans la base JustDeleteMe : recherche web"));
        }
        Grid.SetColumn(actions, 1); g.Children.Add(actions);
        return new Border { Child = g, Padding = new Thickness(4, 8, 4, 8), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    /// <summary>Sites (base principale + étendue) pour revérifier ou réessayer, chargés si besoin.</summary>
    private async Task<Dictionary<string, AccountSite>> SitesAsync(bool withExtended)
    {
        if (_sitesByName != null && (!withExtended || _sitesByName.Values.Any(s => s.Extended))) return _sitesByName;
        var (sites, _) = await AccountFinderService.LoadSitesAsync(_ct);
        var list = sites.ToList();
        if (withExtended) { try { list.AddRange(await MaigretSitesService.LoadAsync(sites, _ct)); } catch { } }
        // Complète la liste en mémoire (sans remplacer les sites déjà connus)
        var merged = _sitesByName ?? new Dictionary<string, AccountSite>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in list) merged.TryAdd(s.Name, s);
        return _sitesByName = merged;
    }

    /// <summary>Revérifie les comptes « demande envoyée » et « supprimé » (toutes recherches confondues).</summary>
    internal async Task RecheckAsync()
    {
        var todo = AccountTrackingService.LoadTracking().Values.Where(t => t.State is TrackState.Requested or TrackState.Deleted).ToList();
        if (todo.Count == 0) { TxtStatus.Text = "Aucun compte à revérifier."; return; }
        TxtStatus.Text = $"Revérification de {todo.Count} compte(s)…";
        Dictionary<string, AccountSite> sites;
        try { sites = await SitesAsync(withExtended: false); if (todo.Any(t => !sites.ContainsKey(t.Site))) sites = await SitesAsync(withExtended: true); }
        catch (Exception ex) { TxtStatus.Text = $"Revérification impossible : {ex.Message}"; return; }
        int gone = 0, online = 0, unknown = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _ct }, async (t, token) =>
        {
            if (!sites.TryGetValue(t.Site, out var s)) { Interlocked.Increment(ref unknown); return; }
            var res = await AccountFinderService.CheckAsync(s, t.Username, token);
            var probe = new AccountResult(t.Site, t.Username, t.Url, s.UrlMain, res.Status, "");
            if (res.Status == AccountStatus.NotFound) { AccountTrackingService.SaveRecheck(probe, false); Interlocked.Increment(ref gone); }
            else if (res.Status == AccountStatus.Found) { AccountTrackingService.SaveRecheck(probe, true); Interlocked.Increment(ref online); }
            else Interlocked.Increment(ref unknown);
        });
        _track = AccountTrackingService.LoadTracking();
        TxtStatus.Text = $"Revérification : {gone} compte(s) disparu(s)" + (online > 0 ? $", {online} encore en ligne (demande à relancer ou délai de 30 jours en cours)" : "") + (unknown > 0 ? $", {unknown} non vérifiable(s)" : "") + ".";
        _onOutput($"[i] Comptes : revérification — {gone} disparu(s), {online} encore en ligne, {unknown} non vérifiable(s).");
        Render();
    }

    /// <summary>Après la recherche : nouvel essai des sites temporairement bloqués (HTTP 429 / 503, délai dépassé).</summary>
    private async Task RetryTransientAsync(CancellationToken ct)
    {
        static bool Transient(AccountResult r) => r.Status == AccountStatus.Uncertain && (r.Detail.Contains("HTTP 429") || r.Detail.Contains("HTTP 503") || r.Detail.StartsWith("pas de réponse"));
        var retry = _results.Where(Transient).ToList();
        if (retry.Count == 0 || _sitesByName == null) return;
        TxtStep.Text = $"Nouvel essai de {retry.Count} site(s) temporairement bloqué(s) dans 15 s…";
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        TxtStep.Text = $"Nouvel essai de {retry.Count} site(s)…";
        var controls = AccountFinderService.LoadControls();
        var replaced = new ConcurrentDictionary<AccountResult, AccountResult>();
        await Parallel.ForEachAsync(retry, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (r, token) =>
        {
            if (!_sitesByName.TryGetValue(r.Site, out var s)) return;
            var n = await AccountFinderService.CheckAsync(s, r.Username, token);
            if (controls.TryGetValue(s.Name, out var c)) n = AccountFinderService.Adjust(n, c.FoundForAnyone, c.DetectionBroken, s.Extended);
            if (n.Status != AccountStatus.Uncertain) replaced[r] = n;
        });
        foreach (var (old, n) in replaced) { int i = _results.IndexOf(old); if (i >= 0) _results[i] = n; }
        if (!replaced.IsEmpty) TxtStatus.Text = $"{replaced.Count} site(s) résolu(s) au nouvel essai ({replaced.Values.Count(x => x.Status == AccountStatus.Found)} compte(s) trouvé(s) en plus).";
    }

    /// <summary>Bilan de fiabilité par site (premier pseudo) pour les recherches suivantes.</summary>
    private void RecordStats(string firstUser)
    {
        static bool Unreliable(AccountResult r) => r.Detail.Contains("répond comme") || r.Detail.Contains("ne marche plus") || r.Detail.Contains("non fiable");
        try
        {
            AccountTrackingService.RecordSearch(_results.Where(r => r.Username == firstUser && r.Detail != ManualDetail && r.Detail != AccountFinderService.ManualOnlyDetail && !(r.Status == AccountStatus.Skipped && r.Detail.StartsWith("pseudo impossible")))
                .Select(r => (r.Site, r.Status == AccountStatus.Uncertain && !Unreliable(r), Unreliable(r))));
            _stats = AccountTrackingService.LoadStats();
        }
        catch { }
    }

    /// <summary>Texte de suivi et de fuite d'un compte (copie des liens, rapport).</summary>
    private string NoteOf(AccountResult r)
    {
        var parts = new List<string>();
        if (r.Status == AccountStatus.Found) parts.Add(AccountTrackingService.StateLabel(StateOf(r)));
        var b = BreachesOf(r);
        if (b.Count > 0) parts.Add("fuite " + string.Join(", ", b.Select(x => x.Year + (x.PasswordsLeaked ? " (mots de passe)" : ""))));
        if (r.Detail.Length > 0) parts.Add(r.Detail);
        return string.Join(" · ", parts);
    }


    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        var lines = _results.Where(r => r.Status == AccountStatus.Found).OrderBy(r => r.Site).Select(r => $"{r.Site}\t{r.Username}\t{r.Url}\t{NoteOf(r)}").ToList();
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); TxtStatus.Text = $"{lines.Count} lien(s) copié(s)."; }
        catch { TxtStatus.Text = "Presse-papiers indisponible."; }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Comptes_en_ligne_{DateTime.Now:yyyy-MM-dd}.html", Filter = "Page web|*.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, AccountFinderService.BuildHtml(_results, _source, r => _jdm.Find(r.UrlMain, r.Site), NoteOf));
            TxtStatus.Text = $"Rapport enregistré : {dlg.FileName}";
        }
        catch (Exception ex) { TxtStatus.Text = $"Enregistrement impossible : {ex.Message}"; }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
