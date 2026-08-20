using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>Which part of the welcome is on screen.</summary>
public enum IntroAct
{
    /// <summary>The name, alone in the middle of the window.</summary>
    Welcome,

    /// <summary>Choosing an account, and making one.</summary>
    SignIn,

    /// <summary>What this thing actually is, now that they can use it.</summary>
    About,
}

/// <summary>Within the sign-in act: the two doors, or what lies behind one of them.</summary>
public enum SignInDoor
{
    Choice,
    Microsoft,
    Offline,
}

/// <summary>
/// The first launch.
///
/// Sequenced here rather than in the view because the timing is part of the writing: a line
/// needs a beat to be read before the next one arrives, and a beat is a property change at a
/// particular moment. The view owns how each act looks and animates; this owns when.
///
/// It creates no accounts of its own — every door leads to the same AccountsViewModel the rest
/// of the launcher uses, so an account made here is made exactly the way it is anywhere else.
/// </summary>
public partial class IntroViewModel : ViewModelBase
{
    private readonly AsobuLauncher _launcher;
    private readonly Action _finished;

    public IntroViewModel(AsobuLauncher launcher, AccountsViewModel accounts, Action finished)
    {
        _launcher = launcher;
        _finished = finished;
        Accounts = accounts;

        // Sign-in finishing is the cue to move on, and it can finish in three different ways —
        // a browser, a name box, or an account that was already there. Watching the result
        // rather than each route means none of them needs to know about the welcome.
        Accounts.PropertyChanged += OnAccountsChanged;
    }

    /// <summary>The real accounts page, driving the real sign-in. Bound to directly by the view.</summary>
    public AccountsViewModel Accounts { get; }

    [ObservableProperty] public partial IntroAct Act { get; set; } = IntroAct.Welcome;
    [ObservableProperty] public partial SignInDoor Door { get; set; } = SignInDoor.Choice;

    // The fades are plain numbers the view transitions between, rather than class-triggered
    // keyframes. A keyframe animation only fires when its selector *starts* matching, which
    // makes replaying it a dance of removing a class and adding it back a frame later — and
    // leaves the last filled value in place if anything goes wrong with that. A number that
    // the view eases towards is the same effect with no state to get stuck in, which matters
    // here because this whole overlay swallows clicks: stuck at zero, it is an invisible sheet
    // over a launcher that appears to have frozen.

    /// <summary>The whole overlay: 1 while it is up, 0 as it leaves.</summary>
    [ObservableProperty] public partial double Veil { get; set; }

    /// <summary>"Welcome to".</summary>
    [ObservableProperty] public partial double HelloFade { get; set; }

    /// <summary>"asobu", a beat later.</summary>
    [ObservableProperty] public partial double NameFade { get; set; }

    /// <summary>The offline name, kept here so a half-typed name is not the accounts page's problem.</summary>
    [ObservableProperty] public partial string OfflineName { get; set; } = "Player";

    public bool IsWelcome => Act == IntroAct.Welcome;
    public bool IsSignIn => Act == IntroAct.SignIn;
    public bool IsAbout => Act == IntroAct.About;

    public bool IsChoosing => IsSignIn && Door == SignInDoor.Choice;
    public bool IsMicrosoft => IsSignIn && Door == SignInDoor.Microsoft;
    public bool IsOffline => IsSignIn && Door == SignInDoor.Offline;

    /// <summary>True from the sign-in act onwards: what moves the title up out of the middle.</summary>
    public bool IsTitleRaised => Act != IntroAct.Welcome;

    /// <summary>
    /// How tall the area under the title is. The title is centred against it, so growing this
    /// is what carries the title up — by exactly half, whatever the contents turn out to be.
    /// One height for both acts, so moving between them never nudges it.
    /// </summary>
    public double StageHeight => Act == IntroAct.Welcome ? 0 : 440;

    /// <summary>
    /// Greets them by name, or doesn't. Someone who skipped signing in has no name to use, and
    /// "You're in, Not signed in" is worse than not trying.
    /// </summary>
    public string Greeting => Accounts.Active is { Username: { Length: > 0 } name }
        ? $"You’re in, {name}."
        : "You’re all set.";

    partial void OnActChanged(IntroAct value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsSignIn));
        OnPropertyChanged(nameof(IsAbout));
        OnPropertyChanged(nameof(IsTitleRaised));
        OnPropertyChanged(nameof(StageHeight));
        OnPropertyChanged(nameof(Greeting));
        RaiseDoors();
    }

    partial void OnDoorChanged(SignInDoor value) => RaiseDoors();

    private void RaiseDoors()
    {
        OnPropertyChanged(nameof(IsChoosing));
        OnPropertyChanged(nameof(IsMicrosoft));
        OnPropertyChanged(nameof(IsOffline));
    }

    /// <summary>How long the view takes to ease one of the fades above. Keep the two in step.</summary>
    private const int FadeMilliseconds = 550;

    /// <summary>
    /// Runs the opening on its own clock: a blank beat, then "Welcome to", then the name, then
    /// long enough to read both before the launcher starts asking for anything.
    ///
    /// The opening beat is exactly one fade long, so on a replay whatever was left on screen has
    /// finished fading out before the first line starts fading in.
    /// </summary>
    public async Task PlayAsync()
    {
        Veil = 1;
        await Task.Delay(FadeMilliseconds);

        HelloFade = 1;
        await Task.Delay(800);

        NameFade = 1;
        await Task.Delay(1500);

        // Someone who clicked through already is not dragged back to the start.
        if (Act == IntroAct.Welcome) Act = IntroAct.SignIn;
    }

    /// <summary>
    /// Back to a blank screen, before the overlay is shown. Separate from ReplayAsync so the
    /// caller can empty the screen while it is still hidden and only then reveal it — otherwise
    /// the last run’s title is briefly visible under the new one fading in.
    /// </summary>
    public void Reset()
    {
        if (Accounts.IsSigningIn) Accounts.CancelSignInCommand.Execute(null);

        Accounts.Error = null;
        OfflineName = "Player";
        Door = SignInDoor.Choice;
        Act = IntroAct.Welcome;

        Veil = 0;
        HelloFade = 0;
        NameFade = 0;
    }

    private void OnAccountsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountsViewModel.Active)) return;
        if (Act != IntroAct.SignIn || Accounts.Active is null) return;

        Act = IntroAct.About;
    }

    [RelayCommand]
    private void ChooseMicrosoft()
    {
        Accounts.Error = null;
        Door = SignInDoor.Microsoft;
        Accounts.SignInMicrosoftCommand.Execute(null);
    }

    [RelayCommand]
    private void ChooseOffline()
    {
        Accounts.Error = null;
        Door = SignInDoor.Offline;
    }

    /// <summary>Back to the two doors, cancelling whatever the chosen one had started.</summary>
    [RelayCommand]
    private void BackToChoice()
    {
        if (Accounts.IsSigningIn) Accounts.CancelSignInCommand.Execute(null);

        Accounts.Error = null;
        Door = SignInDoor.Choice;
    }

    /// <summary>
    /// Hands the typed name to the accounts page, which owns what a username may be. Anything
    /// it objects to comes back through Accounts.Error, which the view is already showing.
    /// </summary>
    [RelayCommand]
    private void AddOffline()
    {
        Accounts.NewUsername = OfflineName;
        Accounts.AddOfflineCommand.Execute(null);
    }

    /// <summary>
    /// Past the account question, and only that. An account can be added from Accounts whenever
    /// they want one, but the rest of the welcome is the part that explains what they have just
    /// installed — which is worth more to someone who skipped signing in, not less.
    /// </summary>
    [RelayCommand]
    private void Skip()
    {
        if (Accounts.IsSigningIn) Accounts.CancelSignInCommand.Execute(null);

        Accounts.Error = null;
        Act = IntroAct.About;
    }

    /// <summary>
    /// The last button. Writes the flag first so a launcher killed during the fade still counts
    /// as welcomed, then lets the window through.
    ///
    /// The account watcher is deliberately left hooked up: it does nothing outside the sign-in
    /// act, and leaving it means the welcome can be replayed from Settings without rewiring.
    /// </summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        _launcher.Settings.IntroCompleted = true;
        _launcher.SaveSettings();

        Veil = 0;
        await Task.Delay(FadeMilliseconds);

        _finished();
    }

    /// <summary>Winds the whole thing back and plays it, for the button in Settings.</summary>
    public Task ReplayAsync()
    {
        Reset();

        return PlayAsync();
    }
}
