using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    Keyboard and controller navigation for layouts the ModPage's up/down list doesn't cover. Each sets the elements'
    leftNav/rightNav/topNav/botNav; pass what's around the layout (null: stop at the edge).

        ModNav.Grid(rows, above: tabButton, below: back);            // icon grids: rows of tiles
        ModNav.Columns(columns, above: toolbar, below: back);       // boards: left/right keeps the row, clamped
        ModNav.Spatial(markers, m => m.Rect.anchoredPosition);       // free layouts (a map): the nearest in a direction

    On a ModPage, add such elements with page.AddElement only if the page's own up/down chain should include them;
    otherwise wire them here and connect the page in page.Wired.
*/
namespace PalaceMod
{
    public static class ModNav
    {
        /// <summary>Rows of elements: left/right along a row, up/down to the nearest column of the row above/below.</summary>
        public static void Grid(IReadOnlyList<IReadOnlyList<ThronefallUIElement>> rows, ThronefallUIElement above = null, ThronefallUIElement below = null,
            ThronefallUIElement left = null, ThronefallUIElement right = null)
        {
            var lines = rows.Where(r => r != null && r.Count > 0).ToList();
            for (int r = 0; r < lines.Count; r++)
            {
                var row = lines[r];
                for (int c = 0; c < row.Count; c++)
                {
                    var e = row[c];
                    e.leftNav = c > 0 ? row[c - 1] : left;
                    e.rightNav = c < row.Count - 1 ? row[c + 1] : right;
                    e.topNav = r > 0 ? lines[r - 1][Mathf.Min(c, lines[r - 1].Count - 1)] : above;
                    e.botNav = r < lines.Count - 1 ? lines[r + 1][Mathf.Min(c, lines[r + 1].Count - 1)] : below;
                }
            }
        }

        /// <summary>Columns of elements (top to bottom): up/down within a column, left/right to the same position in
        /// the next non-empty column (or its last element). <paramref name="above"/> is where up from a column's top
        /// goes (one per column, or a single element for all).</summary>
        public static void Columns(IReadOnlyList<IReadOnlyList<ThronefallUIElement>> columns, ThronefallUIElement above = null, ThronefallUIElement below = null)
        {
            var cols = columns.Where(c => c != null && c.Count > 0).ToList();
            for (int x = 0; x < cols.Count; x++)
            {
                var col = cols[x];
                for (int y = 0; y < col.Count; y++)
                {
                    var e = col[y];
                    e.topNav = y > 0 ? col[y - 1] : above;
                    e.botNav = y < col.Count - 1 ? col[y + 1] : below;
                    e.leftNav = x > 0 ? cols[x - 1][Mathf.Min(y, cols[x - 1].Count - 1)] : null;
                    e.rightNav = x < cols.Count - 1 ? cols[x + 1][Mathf.Min(y, cols[x + 1].Count - 1)] : null;
                }
            }
        }

        /// <summary>Free positions (y up): each direction goes to the nearest element within a 45° cone that way,
        /// else to <paramref name="edges"/> for that side (left, right, up, down; null entries stop).</summary>
        public static void Spatial<T>(IReadOnlyList<T> elements, System.Func<T, Vector2> position, ThronefallUIElement[] edges = null) where T : ThronefallUIElement
        {
            var points = elements.Select(e => (e, p: position(e))).ToList();
            ThronefallUIElement Nearest(Vector2 from, Vector2 dir, T self)
            {
                ThronefallUIElement best = null;
                float bestScore = float.MaxValue;
                foreach (var (e, p) in points)
                {
                    if (e == self) continue;
                    var d = p - from;
                    float along = Vector2.Dot(d, dir);
                    if (along <= 0.5f) continue;
                    float across = Mathf.Abs(d.x * dir.y - d.y * dir.x);
                    if (across > along * 1.2f) continue; // roughly a 100° cone
                    float score = along + across * 2f;
                    if (score < bestScore) { bestScore = score; best = e; }
                }
                return best;
            }
            foreach (var (e, p) in points)
            {
                e.leftNav = Nearest(p, Vector2.left, e) ?? edges?.ElementAtOrDefault(0);
                e.rightNav = Nearest(p, Vector2.right, e) ?? edges?.ElementAtOrDefault(1);
                e.topNav = Nearest(p, Vector2.up, e) ?? edges?.ElementAtOrDefault(2);
                e.botNav = Nearest(p, Vector2.down, e) ?? edges?.ElementAtOrDefault(3);
            }
        }
    }
}
