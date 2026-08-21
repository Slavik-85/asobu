using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Mods;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public MainViewModel()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Asobu/0.1 (+https://asobu.cc)");
        Launcher = new AsobuLauncher(_http);

        AccountsPage = new AccountsViewModel(Launcher);
        CrashReportsPage = new CrashReportsViewModel(Launcher, GoInstances);
        InstancesPage = new InstancesViewModel(
            Launcher, AccountsPage, () => _ = GoNewInstanceAsync(),
            GoCrashReports, GoAddMods);
        NewInstancePage = new VersionPickerViewModel(Launcher, OnInstanceCreated, GoInstances);
        Updates = new UpdateViewModel();
        SettingsPage = new SettingsViewModel(Launcher, Updates, ReplayIntro, ReplayTour);
        ExplorePage = new ExploreViewModel(Launcher, GoModPage, AskInstall);
        PackInstaller = new PackInstallViewModel(Launcher, OnInstanceCreated);

        BrowsePage = new BrowseViewModel(Launcher, GoModPage, AskInstall, AskCreatePack);

        // "Go to Accounts" from inside the drawer: the drawer leaves and the page changes at
        // the same moment, so the accounts page is never greyed out under it.
        FriendsPage = new FriendsViewModel(Launcher, AccountsPage, () =>
        {
            _ = CloseFriendsAsync();
            GoAccounts();
        });

        // A second browser rather than the same one wearing a hat: the sidebar's Browse keeps
        // its own search, filters and version while this one is scoped to an instance, and
        // coming back to either should find it as it was left.
        AddModsPage = new BrowseViewModel(Launcher, GoModPageForInstance, AskInstall, AskCreatePack);
        ModPage = new ModPageViewModel(Launcher, AskInstall, AskCreatePack);
        InstallPicker = new InstallPickerViewModel(Launcher, () => _ = GoNewInstanceAsync());

        AccountsPage.Reload();
        AccountsPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountsViewModel.Active))
            {
                InstancesPage.RefreshAccountLabel();
                FriendsPage.OnAccountChanged();
                OnPropertyChanged(nameof(AccountLabel));
                OnPropertyChanged(nameof(AccountKindLabel));
            }
        };

        InstancesPage.Reload();
        CurrentPage = InstancesPage;

        Tour = new TourViewModel(Launcher, GoInstances);
        Tour.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TourViewModel.IsRunning)) OnPropertyChanged(nameof(IsChromeVisible));
        };

        Intro = new IntroViewModel(Launcher, AccountsPage, IntroFinished);

        // A first launch owns the window until it is done; everyone else never sees it.
        IsIntroOpen = !Launcher.Settings.IntroCompleted;

        if (IsIntroOpen)
        {
            // Started after the first layout rather than here. This runs while the window is
            // still being built, and on a cold start that can take longer than the opening
            // beat — which would spend the first line's fade on a window nobody can see yet.
            Dispatcher.UIThread.Post(() => _ = Intro.PlayAsync(), DispatcherPriority.Loaded);
        }
        else
        {
            Tour.OfferOnce();
        }

        WarmUp();
    }

    /// <summary>
    /// Puts the welcome back on screen from the beginning. Emptied first and revealed second,
    /// so the previous run's title isn't sitting there while the new one fades in.
    ///
    /// The flag is cleared as well as the overlay reopened, so a launcher closed midway through
    /// the replay opens on it again — which is what someone testing it would expect.
    /// </summary>
    private void ReplayIntro()
    {
        Launcher.Settings.IntroCompleted = false;
        Launcher.SaveSettings();

        Intro.Reset();
        IsIntroOpen = true;
        _ = Intro.PlayAsync();
    }

    /// <summary>The first launch, over the whole window.</summary>
    public IntroViewModel Intro { get; }

    /// <summary>The walk around the launcher, offered once the welcome is out of the way.</summary>
    public TourViewModel Tour { get; }

    /// <summary>Keeping the launcher current, which it does without being asked.</summary>
    public UpdateViewModel Updates { get; }

    [ObservableProperty] public partial bool IsIntroOpen { get; set; }

    /// <summary>
    /// Everything that floats over the pages steps aside while the welcome or the tour is on:
    /// the friends button would otherwise sit on top of the welcome, and light itself up
    /// through the tour's own dimming.
    /// </summary>
    public bool IsChromeVisible => !IsIntroOpen && !Tour.IsRunning;

    partial void OnIsIntroOpenChanged(bool value) => OnPropertyChanged(nameof(IsChromeVisible));

    /// <summary>
    /// Starts the tour again. Not merely re-offered: someone who pressed this has already said
    /// yes, and the offer itself can be seen by replaying the welcome, which ends with it.
    /// </summary>
    private void ReplayTour() => Tour.StartCommand.Execute(null);

    private void IntroFinished()
    {
        IsIntroOpen = false;

        // The account made during the welcome is the one to play as.
        InstancesPage.RefreshAccountLabel();
        InstancesPage.Reload();

        Tour.OfferOnce();
    }

    /// <summary>
    /// Starts the slow, shared things while the window is being drawn, so the first visit to a
    /// page finds them done rather than starting them.
    ///
    /// The category lists cost four requests between them and are the first thing Explore and
    /// Browse wait on. Posted at background priority so none of it competes with getting the
    /// first frame up, and the result lands in a cache rather than on a page — nothing here
    /// draws anything, so a slow network or a dead provider costs exactly nothing.
    /// </summary>
    private void WarmUp() => Dispatcher.UIThread.Post(
        () => _ = Task.Run(async () =>
        {
            // The instance the browsing pages will open against, which decides what they ask for.
            var first = Launcher.Instances.LoadAll().FirstOrDefault();

            // Everything at once. They are independent requests to three different services and
            // nothing here is waiting on anything else, so running them in turn would make the
            // warm-up take as long as all of them added together.
            await Task.WhenAll(
                Warm(() => Launcher.Mods.GetCategoriesAsync(ModKind.Mod)),

                // Browse cannot offer a version until Mojang's manifest is in.
                Warm(() => Launcher.Meta.GetManifestAsync()),

                // Explore's opening grid, keyed exactly as it will ask for it — same version,
                // same sort, same size — so the answer is already in the cache rather than
                // merely nearby. No loader: Explore asks about a version, not an instance, so
                // keying this by one would prime a query the page never makes.
                Warm(() => Launcher.Mods.SearchAsync(new ModQuery(
                    "", first?.MinecraftVersion, null, ModSort.Popular, null, ExploreGridLimit))),

                // Friends, but only from the stored session — one request when one exists,
                // nothing at all otherwise. Returning users show as online to their friends
                // without having to open the page.
                Warm(FriendsPage.TryResumeAsync),

                // And whether there is a newer Asobu. Silent unless there is: it downloads in
                // the background and puts a button in the sidebar, rather than interrupting.
                Warm(Updates.CheckQuietlyAsync));
        }),
        DispatcherPriority.Background);

    /// <summary>Matches ExploreViewModel's own grid size; the cache key includes it.</summary>
    private const int ExploreGridLimit = 40;

    /// <summary>
    /// Runs a warm-up and swallows whatever it makes of itself. Nothing is waiting on any of
    /// this: a warm-up that fails is a page that loads the way it always did.
    /// </summary>
    private static async Task Warm(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception)
        {
        }
    }

    public AsobuLauncher Launcher { get; }

    public InstancesViewModel InstancesPage { get; }
    public VersionPickerViewModel NewInstancePage { get; }
    public AccountsViewModel AccountsPage { get; }
    public SettingsViewModel SettingsPage { get; }
    public ExploreViewModel ExplorePage { get; }
    public BrowseViewModel BrowsePage { get; }
    public FriendsViewModel FriendsPage { get; }

    /// <summary>Browsing on one instance's behalf, opened from its mods list.</summary>
    public BrowseViewModel AddModsPage { get; }
    public ModPageViewModel ModPage { get; }
    public InstallPickerViewModel InstallPicker { get; }

    /// <summary>Naming the instance a modpack is about to become.</summary>
    public PackInstallViewModel PackInstaller { get; }
    public CrashReportsViewModel CrashReportsPage { get; }

    [ObservableProperty] public partial ViewModelBase CurrentPage { get; set; }

    public bool IsInstances => CurrentPage is InstancesViewModel;
    public bool IsNewInstance => CurrentPage is VersionPickerViewModel;
    public bool IsCrashReports => CurrentPage is CrashReportsViewModel;
    public bool IsInstancesArea => IsInstances || IsNewInstance || IsCrashReports;
    public bool IsExplore => CurrentPage is ExploreViewModel;
    public bool IsBrowse => CurrentPage is BrowseViewModel;
    public bool IsAccounts => CurrentPage is AccountsViewModel;
    public bool IsSettings => CurrentPage is SettingsViewModel;

    public string AccountLabel => AccountsPage.ActiveLabel;
    public string AccountKindLabel => AccountsPage.ActiveKindLabel;

    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        // A mod's page belongs to the page it was opened from. Picking somewhere else in the
        // sidebar leaves that behind, so the overlay goes with it rather than sitting on top of
        // wherever you went instead. The instance's own browser is an overlay for the same
        // reason and leaves at the same time.
        IsModPageOpen = false;
        IsAddModsOpen = false;

        ExplorePage.SetOnScreen(ReferenceEquals(value, ExplorePage));

        OnPropertyChanged(nameof(IsInstances));
        OnPropertyChanged(nameof(IsNewInstance));
        OnPropertyChanged(nameof(IsCrashReports));
        OnPropertyChanged(nameof(IsInstancesArea));
        OnPropertyChanged(nameof(IsExplore));
        OnPropertyChanged(nameof(IsBrowse));
        OnPropertyChanged(nameof(IsAccounts));
        OnPropertyChanged(nameof(IsSettings));
    }

    private void OnInstanceCreated(Instance instance)
    {
        InstancesPage.Reload();
        InstancesPage.Selected = instance;
        CurrentPage = InstancesPage;
    }

    [RelayCommand]
    private void GoInstances()
    {
        InstancesPage.Reload();
        CurrentPage = InstancesPage;
    }

    [RelayCommand]
    private void GoExplore()
    {
        // Reloaded on every visit: the instance list is the browser's install target, and one
        // created since the last visit has to be pickable.
        ExplorePage.Reload();
        CurrentPage = ExplorePage;
    }

    /// <summary>
    /// Raised while a mod's page is over the top of whatever opened it. Not a CurrentPage of its
    /// own: the sidebar should still show which browsing page you are on, because that is where
    /// closing this returns you.
    /// </summary>
    [ObservableProperty] public partial bool IsModPageOpen { get; set; }

    private void GoModPage(CatalogueMod mod)
    {
        ModPage.Load(mod, () => IsModPageOpen = false);
        IsModPageOpen = true;
    }

    /// <summary>
    /// Whether the instance's own mod browser is over the top of the library. An overlay rather
    /// than a page of its own, so the sidebar still reads Instances: that is where closing it
    /// returns you, and it never stopped being where you are.
    /// </summary>
    [ObservableProperty] public partial bool IsAddModsOpen { get; set; }

    private void GoAddMods(Instance instance, ModKind kind)
    {
        AddModsPage.OpenFor(instance, kind, () =>
        {
            IsAddModsOpen = false;

            // Re-read on the way out rather than on every Add: a folder scan per mod would be
            // paid for repeatedly, and nothing behind the browser is being looked at meanwhile.
            InstancesPage.RefreshModsAfterBrowsing();
        });
        IsAddModsOpen = true;
    }

    /// <summary>
    /// The mod page as opened from an instance's browser: it inherits the instance, so its
    /// versions are the ones that instance runs and its buttons install rather than ask.
    /// </summary>
    private void GoModPageForInstance(CatalogueMod mod)
    {
        ModPage.Load(mod, () => IsModPageOpen = false, AddModsPage.Target);
        IsModPageOpen = true;
    }

    /// <summary>
    /// Every Add on every page comes through here. Which instance a mod goes into is a question
    /// worth asking rather than inferring — the browsing pages are filtered by a version, and a
    /// version is not an instance.
    /// </summary>
    private void AskInstall(
        string title,
        Func<Instance, Task<string?>> install,
        Func<CancellationToken, Task<ModSupport>> support,
        CatalogueMod? subject = null) =>
        InstallPicker.Open(title, install, support, subject);

    /// <summary>
    /// The same for a modpack, which cannot use the sheet above: there is no instance to choose
    /// because the pack is about to become one. The only question left is what to call it.
    /// </summary>
    private void AskCreatePack(CatalogueMod pack, ModVersion? version) =>
        PackInstaller.Open(pack, version);

    [RelayCommand]
    private void GoBrowse()
    {
        // Reloaded on every visit, for the same reason Explore is: the instance list is where
        // anything found here gets installed, and one made since the last visit has to be
        // pickable.
        BrowsePage.Reload();
        CurrentPage = BrowsePage;
    }

    /// <summary>
    /// The friends drawer, over everything. Not a page: friends are a glance and a click away
    /// from wherever you are, and closing the drawer leaves you exactly where you were.
    /// </summary>
    [ObservableProperty] public partial bool IsFriendsOpen { get; set; }
    [ObservableProperty] public partial bool IsFriendsClosing { get; set; }

    [RelayCommand]
    private void OpenFriends()
    {
        FriendsPage.Opened();
        IsFriendsOpen = true;
    }

    [RelayCommand]
    private async Task CloseFriendsAsync()
    {
        if (!IsFriendsOpen || IsFriendsClosing) return;

        // Matches the drawer's slide-out; the flag order is what lets the .closing style win
        // while the .open class is still there.
        IsFriendsClosing = true;
        await Task.Delay(240);
        IsFriendsClosing = false;
        IsFriendsOpen = false;

        // Told rather than inferred: the conversation stays selected so reopening lands back in
        // it, which means the friends page cannot work out on its own that nobody can see it.
        FriendsPage.Closed();
    }

    [RelayCommand]
    private async Task GoNewInstanceAsync()
    {
        CurrentPage = NewInstancePage;
        await NewInstancePage.EnsureLoadedAsync();
    }

    private void GoCrashReports(Instance instance)
    {
        CrashReportsPage.Load(instance);
        CurrentPage = CrashReportsPage;
    }

    [RelayCommand]
    private void GoAccounts()
    {
        AccountsPage.Reload();
        CurrentPage = AccountsPage;
    }

    [RelayCommand]
    private void GoSettings()
    {
        SettingsPage.RefreshJavaOptionsCommand.Execute(null);
        CurrentPage = SettingsPage;
    }
}
