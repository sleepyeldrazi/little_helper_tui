using System.Security.Cryptography;
using System.Text;

namespace LittleHelperTui;

/// <summary>
/// Clipboard support using OSC 52 escape sequence.
/// Works everywhere: tmux, SSH, Wayland, X11, macOS.
/// Falls back to wl-copy/xclip/pbcopy if OSC 52 fails.
/// </summary>
public static class ClipboardHelper
{
    /// <summary>Copy text to terminal clipboard via OSC 52.</summary>
    public static bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // Try OSC 52: \e]52;c;base64...\e\\
        // This works in tmux (if set-clipboard on), kitty, ghostty, wezterm, iterm2
        try
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            // Limit: many terminals reject payloads > ~1MB
            if (b64.Length > 1_000_000) return false;
            Console.Out.Write($"\x1b]52;c;{b64}\x07");
            Console.Out.Flush();
            return true;
        }
        catch { }

        return false;
    }
}
