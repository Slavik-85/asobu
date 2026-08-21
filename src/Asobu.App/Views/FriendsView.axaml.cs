using System.Collections.Specialized;
using System.ComponentModel;
using Asobu.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Asobu.App.Views;

public partial class FriendsView : UserControl
{
    /// <summary>The collection currently being watched, so its handler can be taken off again.</summary>
    private INotifyCollectionChanged? _watching;

    public FriendsView()
    {
        InitializeComponent();

        // Follow the conversation to the bottom as it grows. Chat that leaves the newest message
        // below the fold is chat you have to scroll to read, and a message arriving while you are
        // reading an old one is the one case where moving the view is what anybody wants.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not FriendsViewModel friends) return;

            friends.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FriendsViewModel.Conversation)) Follow(friends);
            };

            Follow(friends);
        };
    }

    private void Follow(FriendsViewModel friends)
    {
        if (_watching is not null) _watching.CollectionChanged -= OnConversationChanged;

        _watching = friends.Conversation;

        if (_watching is not null) _watching.CollectionChanged += OnConversationChanged;

        ToBottom();
    }

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs e) => ToBottom();

    /// <summary>
    /// After the layout pass, not during it. The line has only just been added, so the scroll
    /// viewer does not yet know it is any taller than it was.
    /// </summary>
    private void ToBottom() =>
        Dispatcher.UIThread.Post(() => ChatScroll?.ScrollToEnd(), DispatcherPriority.Background);
}
