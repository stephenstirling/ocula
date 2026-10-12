using System;
using System.Collections.Generic;
using System.Linq;

namespace Ocula.Services;

/// <summary>How photo windows share a screen (design: docs/mockups, "Photo windows with snap spots").</summary>
public enum SnapLayout
{
    /// <summary>Side by side, one column per window.</summary>
    Columns,
    /// <summary>The first window takes the left half; the rest share the right half.</summary>
    LargeAndRest,
    /// <summary>Two rows, the top one holding more windows ("3 + 2" for five).</summary>
    TopHeavyRows,
    /// <summary>Two rows, the bottom one holding more windows ("2 + 3" for five).</summary>
    BottomHeavyRows,
    /// <summary>Overlapping, each one a step down and to the right.</summary>
    Cascade,
}

/// <summary>What double-clicking another photo does when every spot already has one.</summary>
public enum WhenSpotsFull
{
    ReplaceOldest,
    Ask,
    Cascade,
}

/// <summary>A rectangle in physical screen pixels (a monitor's work area, or a window's spot).</summary>
public readonly record struct SpotRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(SpotRect other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
    public bool Overlaps(SpotRect other) => other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;
}

/// <summary>
/// The arithmetic behind photo windows: where each spot is on a monitor and which spot a new
/// window takes. Spots are fixed: with 6 columns each window is a sixth of the monitor's width
/// wherever it sits, however many are open. Knows nothing about
/// windows, so it is unit-tested (tests/Ocula.Tests/PhotoWindowLayoutTests.cs).
/// </summary>
public static class PhotoWindowLayout
{
    public const int MinSpots = 2;
    public const int MaxSpots = 6;
    public const int DefaultSpots = 6;

    public static IReadOnlyList<SnapLayout> Layouts { get; } = Enum.GetValues<SnapLayout>();

    public static int ClampSpots(int spots) => Math.Clamp(spots, MinSpots, MaxSpots);

    /// <summary>The name shown on the layout chip, e.g. "6 columns", "1 large + 5" or "3 + 3".</summary>
    public static string Name(SnapLayout layout, int spots) => layout switch
    {
        SnapLayout.Columns => $"{spots} columns",
        SnapLayout.LargeAndRest => $"1 large + {spots - 1}",
        SnapLayout.TopHeavyRows => $"{(spots + 1) / 2} + {spots / 2}",
        SnapLayout.BottomHeavyRows => $"{spots / 2} + {(spots + 1) / 2}",
        _ => "Cascade",
    };

    /// <summary>
    /// The rectangles for <paramref name="count"/> windows in a layout, in spot order (left to
    /// right, then top to bottom). Apart from Cascade they tile the area exactly: no gaps, no
    /// overlaps, and leftover pixels go to the first columns and rows.
    /// </summary>
    public static IReadOnlyList<SpotRect> Spots(SnapLayout layout, int count, SpotRect area)
    {
        if (count <= 0) return Array.Empty<SpotRect>();
        if (count == 1 && layout != SnapLayout.Cascade) return new[] { area };

        switch (layout)
        {
            case SnapLayout.Columns:
                return Rows(area, new[] { count });

            case SnapLayout.LargeAndRest:
            {
                var halves = Split(area.X, area.Width, 2);
                var (left, right) = (halves[0], halves[1]);
                var large = area with { X = left.Start, Width = left.Length };
                var rest = count - 1;
                var columns = rest >= 4 ? 2 : 1;
                var perRow = Enumerable.Range(0, (rest + columns - 1) / columns)
                                       .Select(row => Math.Min(columns, rest - row * columns)).ToArray();
                var others = Rows(area with { X = right.Start, Width = right.Length }, perRow);
                return new[] { large }.Concat(others).ToList();
            }

            case SnapLayout.TopHeavyRows:
                return Rows(area, new[] { (count + 1) / 2, count / 2 }.Where(n => n > 0).ToArray());

            case SnapLayout.BottomHeavyRows:
                return Rows(area, new[] { count / 2, (count + 1) / 2 }.Where(n => n > 0).ToArray());

            default:
                return Cascade(area, count);
        }
    }

    /// <summary>
    /// Where each snapped window goes, given the spot each one holds (0-based). Spots are fixed, so
    /// spot 0 is always the far left and the last spot the far right, whatever else is open.
    /// </summary>
    public static IReadOnlyList<SpotRect> Place(SnapLayout layout, int spots, IReadOnlyList<int> spotOfEachWindow, SpotRect area)
    {
        var rects = Spots(layout, spots, area);
        return spotOfEachWindow.Select(spot => rects[Math.Clamp(spot, 0, rects.Count - 1)]).ToList();
    }

    /// <summary>The lowest free spot, or null when all <paramref name="spots"/> are taken.</summary>
    public static int? NextFreeSpot(IEnumerable<int> taken, int spots)
    {
        var used = taken.ToHashSet();
        for (var spot = 0; spot < spots; spot++)
            if (!used.Contains(spot)) return spot;
        return null;
    }

    /// <summary>A window centred in the area at two thirds of its size.</summary>
    public static SpotRect Centre(SpotRect area)
    {
        var width = area.Width * 2 / 3;
        var height = area.Height * 2 / 3;
        return new SpotRect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    /// <summary>Rows of windows: perRow[i] windows side by side in row i, rows sharing the height.</summary>
    private static List<SpotRect> Rows(SpotRect area, int[] perRow)
    {
        var rects = new List<SpotRect>();
        var rows = Split(area.Y, area.Height, perRow.Length);
        for (var r = 0; r < perRow.Length; r++)
            foreach (var column in Split(area.X, area.Width, perRow[r]))
                rects.Add(new SpotRect(column.Start, rows[r].Start, column.Length, rows[r].Length));
        return rects;
    }

    /// <summary>Overlapping windows at three fifths of the area, each a step further down and right.</summary>
    private static List<SpotRect> Cascade(SpotRect area, int count)
    {
        var width = area.Width * 3 / 5;
        var height = area.Height * 3 / 5;
        var step = Math.Max(24, Math.Min(area.Width, area.Height) / 24);
        var fits = Math.Max(1, Math.Min((area.Width - width) / step, (area.Height - height) / step) + 1);
        return Enumerable.Range(0, count)
                         .Select(i => new SpotRect(area.X + i % fits * step, area.Y + i % fits * step, width, height))
                         .ToList();
    }

    /// <summary>Splits a length into parts that add up exactly; the first parts get any leftover pixels.</summary>
    private static (int Start, int Length)[] Split(int start, int length, int parts)
    {
        var result = new (int, int)[parts];
        var size = length / parts;
        var extra = length % parts;
        var position = start;
        for (var i = 0; i < parts; i++)
        {
            var part = size + (i < extra ? 1 : 0);
            result[i] = (position, part);
            position += part;
        }
        return result;
    }
}
