using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Onglet « Son et micro » : bip gauche / droite, niveau du micro en direct, enregistrement de 5 s à réécouter.</summary>
public partial class AudioTestView : UserControl
{
    private readonly Action<string> _onOutput;
    private readonly MicRecorder _mic = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _shownLevel;
    private bool _booted, _recording;

    public AudioTestView(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) =>
        {
            if (!_booted)
            {
                _booted = true;
                PanelMicDenied.Visibility = HardwareTestService.IsCapabilityDenied("microphone") ? Visibility.Visible : Visibility.Collapsed;
                CmbMic.SelectionChanged -= CmbMic_SelectionChanged;
                CmbMic.Items.Add("Micro par défaut de Windows");
                foreach (var name in MicRecorder.GetDevices()) CmbMic.Items.Add(name);
                CmbMic.SelectedIndex = 0;
                CmbMic.SelectionChanged += CmbMic_SelectionChanged;
            }
            if (IsVisible) StartMic();
        };
        // Le micro n'est ouvert que lorsque l'onglet est affiché
        IsVisibleChanged += (_, _) => { if (IsVisible && _booted) StartMic(); else StopMic(); };
        Unloaded += (_, _) => { StopMic(); HardwareTestService.StopSound(); };
    }

    private void StartMic()
    {
        if (_mic.IsOpen) return;
        string? error = _mic.Open(CmbMic.SelectedIndex - 1);   // 0 = micro par défaut (-1)
        TxtMic.Text = error ?? "";
        BtnRecord.IsEnabled = error == null;
        _timer.Start();
    }

    private void StopMic()
    {
        _timer.Stop();
        _mic.Close();
        _recording = false;
        TxtRecord.Text = "Enregistrer 5 secondes";
        MicLevel.Value = 0;
    }

    private void Tick()
    {
        _mic.Poll();
        // Montée immédiate, descente progressive : la barre reste lisible
        _shownLevel = Math.Max(_mic.Level, _shownLevel * 0.85);
        MicLevel.Value = Math.Min(1, Math.Sqrt(_shownLevel));

        if (!_recording) return;
        if (_mic.IsRecordingClip)
        {
            TxtRecord.Text = $"Enregistrement… {(int)Math.Ceiling(5 - _mic.ClipProgress * 5)} s";
            return;
        }
        _recording = false;
        TxtRecord.Text = "Enregistrer 5 secondes";
        BtnRecord.IsEnabled = true;
        BtnPlay.IsEnabled = _mic.LastClipWav != null;
        TxtMic.Text = "Enregistrement terminé : cliquez sur « Écouter l'enregistrement ».";
        _onOutput("[OK] Test du micro : enregistrement de 5 s effectué.");
    }

    private void CmbMic_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopMic();
        BtnPlay.IsEnabled = false;
        StartMic();
    }

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!_mic.IsOpen) return;
        HardwareTestService.StopSound();
        _mic.StartClip(5);
        _recording = true;
        BtnRecord.IsEnabled = false;
        BtnPlay.IsEnabled = false;
        TxtMic.Text = "Parlez normalement pendant 5 secondes…";
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_mic.LastClipWav == null) return;
        HardwareTestService.PlayWav(_mic.LastClipWav);
        TxtMic.Text = "Lecture de l'enregistrement sur la sortie audio par défaut…";
    }

    private void Beep(bool left, bool right, string label)
    {
        HardwareTestService.PlayWav(HardwareTestService.CreateToneWav(left, right));
        TxtSpeaker.Text = $"Bip {label} en cours : il doit venir {(left && right ? "des deux haut-parleurs" : left ? "uniquement de la gauche" : "uniquement de la droite")}.";
    }

    private void BtnLeft_Click(object sender, RoutedEventArgs e) => Beep(true, false, "à gauche");
    private void BtnRight_Click(object sender, RoutedEventArgs e) => Beep(false, true, "à droite");
    private void BtnBoth_Click(object sender, RoutedEventArgs e) => Beep(true, true, "des deux côtés");

    private void BtnMicPrivacy_Click(object sender, RoutedEventArgs e) => CommandRunner.OpenUrl("ms-settings:privacy-microphone");
}
