using System;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FluentHud
{
    // The tray menu, drawn like a Windows 11 dark context menu. WinForms' own layout puts the check
    // in a wide margin, shortcuts and submenu arrows on two different right edges and separators
    // against one side, so every part is placed here from the menu's edges instead:
    //
    //   | 4 | pill                                                              | 4 |
    //   |   | 12 | check 16 | 12 | text ............ shortcut or chevron | 12 |   |
    //
    // Both sides keep the same 16 px gutter; text starts after the check column only in menus that
    // have checkable items.
    sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        const int PillInset = 4;         // highlight pill, from the menu's edges
        const int Gutter = 16;           // content, from the menu's edges (pill inset + 12)
        const int CheckColumn = 16;
        const int CheckGap = 12;
        const int PillRadius = 4;

        public DarkMenuRenderer(System.Windows.Media.Color accent) : base(new DarkMenuColors())
        {
            Accent = accent;
            RoundedEdges = false;
        }

        public System.Windows.Media.Color Accent { get; set; }

        // Item coordinates of the visible menu surface's left edge and (exclusive) right edge. The
        // dropdown's Width counts one more column than the surface DWM shows, hence the - 1.
        static int MenuLeft(Forms.ToolStripItem item) { return -item.Bounds.Left; }
        static int MenuRight(Forms.ToolStripItem item, Forms.ToolStrip menu) { return menu.Width - 1 - item.Bounds.Left; }

        static bool HasCheckColumn(Forms.ToolStrip menu)
        {
            var drop = menu as Forms.ToolStripDropDownMenu;
            return drop != null && drop.ShowCheckMargin;
        }

        static int TextLeft(Forms.ToolStripItem item, Forms.ToolStrip menu)
        {
            return MenuLeft(item) + Gutter + (HasCheckColumn(menu) ? CheckColumn + CheckGap : 0);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var menuItem = e.Item as Forms.ToolStripMenuItem;
            bool shortcut = menuItem != null && !string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString) && e.Text == menuItem.ShortcutKeyDisplayString;
            e.TextColor = !e.Item.Enabled ? Drawing.Color.FromArgb(0x78, 0x78, 0x78)
                        : shortcut ? Drawing.Color.FromArgb(0x9E, 0x9E, 0x9E)
                        : Drawing.Color.White;
            int left = TextLeft(e.Item, e.ToolStrip), right = MenuRight(e.Item, e.ToolStrip) - Gutter;
            e.TextRectangle = new Drawing.Rectangle(left, 0, Math.Max(0, right - left), e.Item.Height);
            // NoPadding: GDI otherwise adds a few pixels on each side, which would undo the gutters.
            e.TextFormat = Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.NoPadding
                         | (shortcut ? Forms.TextFormatFlags.Right : Forms.TextFormatFlags.Left | Forms.TextFormatFlags.EndEllipsis);
            base.OnRenderItemText(e);
        }

        // A rounded pill inset from the menu's edges, as Windows 11 highlights menu items.
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            int left = MenuLeft(e.Item) + PillInset, right = MenuRight(e.Item, e.ToolStrip) - PillInset;
            var r = new Drawing.Rectangle(left, 1, right - left, e.Item.Height - 2);
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = RoundedRect(r, PillRadius))
            using (var fill = new Drawing.SolidBrush(DarkMenuColors.Hover))
                e.Graphics.FillPath(fill, path);
        }

        // A thin chevron on the same right edge as the shortcuts.
        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            if (e.Item == null || e.Item.Owner == null) { base.OnRenderArrow(e); return; }
            float right = MenuRight(e.Item, e.Item.Owner) - Gutter, cy = e.Item.Height / 2f;
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(0xC5, 0xC5, 0xC5), 1.25f))
            {
                pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = Drawing.Drawing2D.LineJoin.Round;
                e.Graphics.DrawLines(pen, new[] { new Drawing.PointF(right - 4, cy - 4.5f), new Drawing.PointF(right - 0.5f, cy), new Drawing.PointF(right - 4, cy + 4.5f) });
            }
        }

        // An accent check mark, centered in the check column.
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            float cx = MenuLeft(e.Item) + Gutter + CheckColumn / 2f, cy = e.Item.Height / 2f;
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(Accent.R, Accent.G, Accent.B), 1.75f))
            {
                pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = Drawing.Drawing2D.LineJoin.Round;
                e.Graphics.DrawLines(pen, new[] { new Drawing.PointF(cx - 5f, cy + 0.5f), new Drawing.PointF(cx - 1.5f, cy + 4f), new Drawing.PointF(cx + 5f, cy - 3.5f) });
            }
        }

        // Full width minus the same inset as the pill, so it's centered under the items.
        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            int left = MenuLeft(e.Item) + PillInset, right = MenuRight(e.Item, e.ToolStrip) - PillInset;
            int y = e.Item.Height / 2;
            using (var pen = new Drawing.Pen(DarkMenuColors.Line))
                e.Graphics.DrawLine(pen, left, y, right, y);   // GDI+ stops a 1 px line one pixel short of its end point
        }

        // The check column is drawn by OnRenderItemCheck; WinForms' margin background would show as a band.
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

        static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        // The dark flyout surface (acrylic's opaque fallback), its subtle-fill hover, and divider strokes.
        static readonly Drawing.Color Surface = Drawing.Color.FromArgb(0x2C, 0x2C, 0x2C);
        internal static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A);
        internal static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x45, 0x45, 0x45);

        public override Drawing.Color ToolStripDropDownBackground { get { return Surface; } }
        public override Drawing.Color ImageMarginGradientBegin { get { return Surface; } }
        public override Drawing.Color ImageMarginGradientMiddle { get { return Surface; } }
        public override Drawing.Color ImageMarginGradientEnd { get { return Surface; } }
        public override Drawing.Color MenuBorder { get { return Line; } }
        public override Drawing.Color MenuItemBorder { get { return Hover; } }
        public override Drawing.Color MenuItemSelected { get { return Hover; } }
        public override Drawing.Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Drawing.Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Drawing.Color MenuItemPressedGradientBegin { get { return Hover; } }
        public override Drawing.Color MenuItemPressedGradientEnd { get { return Hover; } }
        public override Drawing.Color SeparatorDark { get { return Line; } }
        public override Drawing.Color SeparatorLight { get { return Surface; } }
        public override Drawing.Color CheckBackground { get { return Surface; } }
        public override Drawing.Color CheckSelectedBackground { get { return Hover; } }
        public override Drawing.Color CheckPressedBackground { get { return Hover; } }
    }
}
