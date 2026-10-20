using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using RemoteConnect.Session.Host;

namespace RemoteConnect.Desktop.Services;

/// <summary>Text clipboard through Avalonia (must run on the UI thread).</summary>
public sealed class AvaloniaClipboardBridge : IClipboardBridge
{
    private readonly Func<TopLevel?> _topLevel;

    public AvaloniaClipboardBridge(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    public Task<string?> GetTextAsync()
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clipboard = _topLevel()?.Clipboard;
            return clipboard is null ? null : await clipboard.TryGetTextAsync();
        });

    public Task SetTextAsync(string text)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clipboard = _topLevel()?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        });
}
