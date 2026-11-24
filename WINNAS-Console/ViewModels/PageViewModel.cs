using CommunityToolkit.Mvvm.ComponentModel;
using Winnas.Console.Infrastructure;
using Winnas.Console.Models;
using Winnas.Console.Services;

namespace Winnas.Console.ViewModels;

/// <summary>
/// A page that owns a timer of its own (the live rate chart). Sampling must
/// stop the moment the user leaves the page, otherwise every open page would
/// keep querying the target forever.
/// </summary>
public interface ILivePage
{
    void StartLive();
    void StopLive();
}

/// <summary>
/// Common contract for every page. Pages are created once and kept alive, so
/// switching tabs preserves scroll positions and selections; the active page
/// is the only one that refreshes.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected readonly WinnasService Service;

    protected PageViewModel(WinnasService service) => Service = service;

    public abstract string Title { get; }
    public abstract string Glyph { get; }

    /// <summary>Sidebar section this page belongs to.</summary>
    public virtual string Group => "监控";

    /// <summary>One or two lines shown in the collapsible help strip above the page.</summary>
    public virtual string HelpText => "";

    /// <summary>
    /// Whether the global refresh timer may reload this page. Pages that hold
    /// user state (a half-typed command, a process selection) opt out, so a
    /// background tick never yanks the rug out from under the user.
    /// </summary>
    public virtual bool AutoRefreshable => true;

    /// <summary>Driven by a global setting so a user can silence all of them at once.</summary>
    public bool HelpVisible => AppState.Settings.ShowPageHelp && HelpText.Length > 0;

    /// <summary>Set by MainViewModel: the first page of each sidebar section.</summary>
    public bool ShowGroupHeader { get; set; }

    /// <summary>Public re-raise for the global "hide all help strips" toggle.</summary>
    public void RefreshHelpVisible() => OnPropertyChanged(nameof(HelpVisible));

    /// <summary>Injected by MainViewModel: pages never reach into each other.</summary>
    public Func<WinnasSession?> SessionProvider { get; set; } = () => null;

    protected WinnasSession? CurrentSession => SessionProvider();

    [ObservableProperty] private bool isBusy;

    [ObservableProperty] private string? error;

    [ObservableProperty] private string? notice;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public abstract Task LoadAsync(WinnasSession? session);

    /// <summary>Guard wrapper so every page handles failures the same way.</summary>
    protected async Task Run(Func<Task> body)
    {
        IsBusy = true;
        Error = null;
        try { await body(); }
        catch (Exception ex) { Error = OpResult.From(ex).Message; }
        finally { IsBusy = false; }
    }

    protected async Task<T> Run<T>(Func<Task<T>> body, T fallback)
    {
        IsBusy = true;
        Error = null;
        try { return await body(); }
        catch (Exception ex) { Error = OpResult.From(ex).Message; return fallback; }
        finally { IsBusy = false; }
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
