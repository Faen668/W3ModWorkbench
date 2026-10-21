using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

public enum MessageKind { Info, Warning, Error, Question, Busy }

/// <summary>Which button closed a card with three: the main one, the other one, or Cancel (or Esc, or anything else).</summary>
public enum MessageChoice { Cancel, Primary, Secondary }

/// <summary>A window that shows messages in a <see cref="MessageCard"/>.</summary>
public interface IMessageHost
{
    /// <summary>A message with an OK button.</summary>
    Task ShowMessage(string? title, string message, MessageKind kind);
}

/// <summary>
/// An in-window message, in place of a native message box. A modal one covers its window, dims it and
/// sits in the middle; otherwise it's only the card, and its host places it.
/// </summary>
public partial class MessageCard : UserControl
{
    private TaskCompletionSource<bool>? _result;
    private DispatcherTimer? _timer;
    private IInputElement? _focusBefore;

    public MessageCard()
    {
        InitializeComponent();
    }

    /// <summary>Covers the whole host and dims it, with the card in the middle.</summary>
    public bool IsModal
    {
        get => (bool)GetValue(IsModalProperty);
        set => SetValue(IsModalProperty, value);
    }

    public static readonly DependencyProperty IsModalProperty = DependencyProperty.Register(nameof(IsModal), typeof(bool),
        typeof(MessageCard), new PropertyMetadata(false, (d, _) => ((MessageCard)d).UpdateModal()));

    public bool IsOpen => _result is not null;

    /// <summary>Whether the card open now, or the one closed last, was closed with its other button.</summary>
    private bool _secondary;

    /// <summary>
    /// A question with three answers, such as Save, Don't Save and Cancel. Anything but the first two buttons,
    /// Esc included, counts as Cancel.
    /// </summary>
    public async Task<MessageChoice> Ask(string? title, string message, MessageKind kind, string primary, string secondary, string? cancel = null)
    {
        cancel ??= L.T("Common.Cancel");
        var shown = Show(title, message, kind, primary, secondary);
        CardCancel.Content = cancel;
        CardCancel.Visibility = Visibility.Visible;
        bool ok = await shown;
        return ok ? MessageChoice.Primary : _secondary ? MessageChoice.Secondary : MessageChoice.Cancel;
    }

    /// <summary>
    /// True when its main button is clicked; false when it's closed any other way: the other button, Esc,
    /// <see cref="Close"/>, another message, or the timer of an <paramref name="autoClose"/> one.
    /// </summary>
    public Task<bool> Show(string? title, string message, MessageKind kind, string? primary = null, string? secondary = null,
        bool autoClose = false)
    {
        Close(false);
        _secondary = false;
        CardCancel.Visibility = Visibility.Collapsed;
        CardIcon.Text = kind switch
        {
            MessageKind.Warning => "",
            MessageKind.Error => "",
            MessageKind.Question => "",
            MessageKind.Busy => "",
            _ => "",
        };
        CardIcon.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            MessageKind.Warning => "SystemFillColorCautionBrush",
            MessageKind.Error => "SystemFillColorCriticalBrush",
            _ => "AccentTextFillColorPrimaryBrush",
        });
        CardTitle.Text = title;
        CardTitle.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        CardMessage.Text = message;
        CardMessage.SetResourceReference(TextBlock.ForegroundProperty, title is null ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        CardPrimary.Content = primary;
        CardSecondary.Content = secondary;
        CardSecondary.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        CardPrimary.Margin = new Thickness(secondary is null ? 0 : 8, 0, 0, 0);
        CardButtons.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;

        _focusBefore = Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        var result = _result = new TaskCompletionSource<bool>();
        if (autoClose)
        {
            var timer = _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                if (_result == result) Close(false);
            };
            timer.Start();
        }
        if (primary is not null)
        {
            // so Enter clicks the main button; a card that's only just been shown isn't laid out yet. Not with
            // UpdateLayout here: an error card shown for a layout error would run the failing layout again, and loop.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_result == result) CardPrimary.Focus();
            });
        }
        return result.Task;
    }

    public void Close(bool result)
    {
        _timer?.Stop();
        _timer = null;
        if (_result is not { } open) return;
        _result = null;
        bool hadFocus = IsKeyboardFocusWithin;
        Visibility = Visibility.Collapsed;
        if (hadFocus && _focusBefore is UIElement { IsVisible: true } before) before.Focus();
        _focusBefore = null;
        open.TrySetResult(result);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && IsOpen)
        {
            Close(false);
            e.Handled = true;
        }
    }

    private void UpdateModal()
    {
        if (IsModal)
        {
            Scrim.SetResourceReference(Panel.BackgroundProperty, "SmokeFillColorDefaultBrush");
            Card.Margin = new Thickness(24);
            Card.HorizontalAlignment = HorizontalAlignment.Center;
            Card.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            Scrim.ClearValue(Panel.BackgroundProperty);
            Card.ClearValue(MarginProperty);
            Card.ClearValue(HorizontalAlignmentProperty);
            Card.ClearValue(VerticalAlignmentProperty);
        }
    }

    private void CardPrimary_Click(object sender, RoutedEventArgs e) => Close(true);

    private void CardSecondary_Click(object sender, RoutedEventArgs e)
    {
        _secondary = true;
        Close(false);
    }

    private void CardCancel_Click(object sender, RoutedEventArgs e) => Close(false);
}
