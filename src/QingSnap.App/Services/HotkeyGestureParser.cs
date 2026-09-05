namespace QingSnap.App.Services;

internal static class HotkeyGestureParser
{
    private const uint AltModifier = 0x0001;
    private const uint ControlModifier = 0x0002;
    private const uint ShiftModifier = 0x0004;

    public static bool IsValid(string? value) => TryParse(value, out _);

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryParse(value, out var gesture))
        {
            return false;
        }

        var parts = new List<string>(4);
        if ((gesture.Modifiers & ControlModifier) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((gesture.Modifiers & ShiftModifier) != 0)
        {
            parts.Add("Shift");
        }

        if ((gesture.Modifiers & AltModifier) != 0)
        {
            parts.Add("Alt");
        }

        parts.Add($"F{gesture.VirtualKey - 0x6F}");
        normalized = string.Join('+', parts);
        return true;
    }

    public static bool TryParse(string? value, out ParsedHotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        foreach (var part in value.Split(
                     '+',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ControlModifier;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ShiftModifier;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= AltModifier;
            }
            else if (virtualKey == 0 &&
                     part.Length is 2 or 3 &&
                     part[0] is 'F' or 'f' &&
                     int.TryParse(part[1..], out var functionKey) &&
                     functionKey is >= 1 and <= 12)
            {
                virtualKey = (uint)(0x6F + functionKey);
            }
            else
            {
                return false;
            }
        }

        if (virtualKey == 0)
        {
            return false;
        }

        gesture = new ParsedHotkeyGesture(modifiers, virtualKey);
        return true;
    }
}

internal readonly record struct ParsedHotkeyGesture(uint Modifiers, uint VirtualKey);
