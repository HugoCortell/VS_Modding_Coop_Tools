using System;

namespace VSMCTFDesigner;

// Probably overengineered
internal enum TFDesignContext
{
    Gui,
    MainHand,
    OffHand,
    Ground
}

internal static class TfDesignContexts
{
    public static readonly TFDesignContext[] Ordered =
    [
        TFDesignContext.Gui,
        TFDesignContext.MainHand,
        TFDesignContext.OffHand,
        TFDesignContext.Ground
    ];

    public static readonly string[] DropDownValues =
    [
        "none",
        "gui",
        "mainhand",
        "offhand",
        "ground"
    ];

    public static readonly string[] DropDownNames =
    [
        "Select context...",
        "GUI",
        "Main Hand",
        "Off-Hand",
        "Ground"
    ];

    public static bool TryFromCode(string? code, out TFDesignContext context)
    {
        switch ((code ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "gui":
                context = TFDesignContext.Gui;
            return true;

            case "mainhand":
            case "main":
            case "hand":
            case "tp":
            case "tphand":
                context = TFDesignContext.MainHand;
            return true;

            case "offhand":
            case "off":
            case "tpo":
            case "tpoffhand":
                context = TFDesignContext.OffHand;
            return true;

            case "ground":
                context = TFDesignContext.Ground;
            return true;

            default:
                context = TFDesignContext.MainHand;
            return false;
        }
    }

    public static int DropDownIndex(TFDesignContext? context)
    {
        if (context is null) return 0;

        return context.Value switch
        {
            TFDesignContext.Gui => 1,
            TFDesignContext.MainHand => 2,
            TFDesignContext.OffHand => 3,
            TFDesignContext.Ground => 4,
            _ => 0
        };
    }

    public static string DisplayName(TFDesignContext context)
    {
        return context switch
        {
            TFDesignContext.Gui => "GUI",
            TFDesignContext.MainHand => "Main Hand",
            TFDesignContext.OffHand => "Off-Hand",
            TFDesignContext.Ground => "Ground",
            _ => context.ToString()
        };
    }

    public static string JsonPropertyName(TFDesignContext context)
    {
        return context switch
        {
            TFDesignContext.Gui => "guiTransform",
            TFDesignContext.MainHand => "tpHandTransform",
            TFDesignContext.OffHand => "tpOffHandTransform",
            TFDesignContext.Ground => "groundTransform",
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };
    }

    public static string EventTargetName(TFDesignContext context)
    {
        return context switch
        {
            TFDesignContext.Gui => "Gui",
            TFDesignContext.MainHand => "HandTp",
            TFDesignContext.OffHand => "HandTpOff",
            TFDesignContext.Ground => "Ground",
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };
    }
}


internal enum TFDesignGizmoMode { None, Move, Scale, Rotate }
internal enum TFDesignAxis { None, X, Y, Z }
