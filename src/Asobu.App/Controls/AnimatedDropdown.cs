using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace Asobu.App.Controls;

/// <summary>
/// Gives a ComboBox's popup an exit animation.
///
/// Avalonia offers no way to defer a popup's close — <c>Popup</c> raises Closed after the fact and
/// has no cancellable Closing — so the popup is simply reopened for the length of the animation
/// and closed for real afterwards. The reopen is invisible: the .closing class is applied first,
/// and its style is declared after the entrance rule, so what plays on the way back in is the
/// exit, not another entrance.
///
/// Every branch below exists because of a way this can go wrong: the user reopening mid-exit, our
/// own reopen being mistaken for theirs, or our own final close being mistaken for a new one.
/// </summary>
public static class AnimatedDropdown
{
    /// <summary>Matches the closing animation in Asobu.axaml; keep the two in step.</summary>
    private const int ExitMilliseconds = 150;

    private const string ClosingClass = "closing";

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>("Enabled", typeof(AnimatedDropdown));

    public static void SetEnabled(ComboBox element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(ComboBox element) => element.GetValue(EnabledProperty);

    private sealed class State
    {
        /// <summary>True while we are the ones putting the popup back, not the user.</summary>
        public bool Reopening;

        /// <summary>Marks the close we perform ourselves, so it isn't treated as a new one.</summary>
        public bool FinalClose;

        /// <summary>Bumped whenever the user opens the list, abandoning any exit in flight.</summary>
        public int Generation;
    }

    private static readonly Dictionary<ComboBox, State> States = [];

    static AnimatedDropdown()
    {
        EnabledProperty.Changed.AddClassHandler<ComboBox>((combo, e) =>
        {
            if (e.GetNewValue<bool>()) combo.PropertyChanged += OnComboPropertyChanged;
            else combo.PropertyChanged -= OnComboPropertyChanged;
        });
    }

    private static async void OnComboPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (e.Property != ComboBox.IsDropDownOpenProperty) return;

        if (!States.TryGetValue(combo, out var state)) States[combo] = state = new State();

        if (e.GetNewValue<bool>())
        {
            // Our own reopen. Leave the exit running.
            if (state.Reopening) return;

            // The user opened it, so any exit still in flight no longer applies.
            state.Generation++;
            combo.Classes.Remove(ClosingClass);
            return;
        }

        if (state.FinalClose)
        {
            state.FinalClose = false;
            return;
        }

        var generation = ++state.Generation;

        // Class first, then reopen: the entrance rule would otherwise win the frame in between.
        combo.Classes.Add(ClosingClass);

        state.Reopening = true;
        combo.IsDropDownOpen = true;
        state.Reopening = false;

        await Task.Delay(ExitMilliseconds);

        // Reopened while it was leaving, so the list is wanted after all.
        if (state.Generation != generation) return;

        state.FinalClose = true;
        combo.IsDropDownOpen = false;
        combo.Classes.Remove(ClosingClass);
    }
}
