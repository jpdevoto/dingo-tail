// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only


using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DingoTail;

internal static class Chrome
{
    public static Color Frame => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(25, 29, 37);
    public static Color Toolbar => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(21, 25, 32);
    public static Color Panel => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(30, 35, 44);
    public static Color Log => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(18, 21, 27);
    public static Color Text => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(216, 223, 233);
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(170, 181, 196);
    public static Color Disabled => SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(130, 143, 162);
    public static Color Edge => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(57, 67, 82);
    public static Color Hover => SystemInformation.HighContrast ? SystemColors.ControlLight : Color.FromArgb(43, 49, 60);
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(255, 164, 54);
    public static Color SelectedText => SystemInformation.HighContrast ? SystemColors.HighlightText : Log;
    public static readonly Icon AppIcon = LoadIcon();
    private static readonly ToolStripRenderer Renderer = new MenuRenderer();
    private static readonly Lazy<Dictionary<int, GraphicsPath>> Icons = new(LoadIconOutlines);

    private static Stream Resource(string name) =>
        typeof(Chrome).Assembly.GetManifestResourceStream("DingoTail." + name)!;

    private static Icon LoadIcon()
    {
        using var stream = Resource("App.ico");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    // Normalized outlines extracted from the bundled Material Icons Outlined font.
    private static Dictionary<int, GraphicsPath> LoadIconOutlines()
    {
        using var stream = Resource("IconOutlines.json");
        using var data = JsonDocument.Parse(stream);
        var icons = new Dictionary<int, GraphicsPath>();
        foreach (var icon in data.RootElement.EnumerateObject())
        {
            var path = new GraphicsPath(FillMode.Winding);
            PointF current = default;
            foreach (var command in icon.Value.EnumerateArray())
            {
                PointF Point(int index) => new(command[index].GetSingle(), command[index + 1].GetSingle());
                switch (command[0].GetString())
                {
                    case "moveTo":
                        path.StartFigure();
                        current = Point(1);
                        break;
                    case "lineTo":
                        path.AddLine(current, Point(1));
                        current = Point(1);
                        break;
                    case "curveTo":
                        for (var index = 1; index < command.GetArrayLength(); index += 6)
                        {
                            path.AddBezier(current, Point(index), Point(index + 2), Point(index + 4));
                            current = Point(index + 4);
                        }
                        break;
                    case "closePath":
                        path.CloseFigure();
                        break;
                }
            }
            icons.Add(Convert.ToInt32(icon.Name, 16), path);
        }
        var search = new GraphicsPath(FillMode.Alternate);
        search.AddEllipse(.12f, .12f, .55f, .55f);
        search.AddEllipse(.20f, .20f, .39f, .39f);
        search.AddPolygon(new PointF[] {new(.60f, .54f), new(.88f, .82f), new(.82f, .88f), new(.54f, .60f)});
        icons[0xe8b6] = search;
        return icons;
    }

    public static void StyleForm(Form form)
    {
        form.Icon = AppIcon;
        form.BackColor = Frame;
        form.ForeColor = Text;
        form.HandleCreated += (_, _) =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !SystemInformation.HighContrast)
            {
                var enabled = 1;
                _ = DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
            }
        };
    }

    public static void StyleField(Control control)
    {
        control.ForeColor = Text;
        control.BackColor = control is CheckBox ? Frame : Panel;
        if (control is TextBox text)
            text.BorderStyle = BorderStyle.FixedSingle;
        if (control is CheckBox check)
            check.FlatStyle = FlatStyle.Flat;
    }

    public static Button TextButton(string title, bool primary = false)
    {
        var button = new Button
        {
            Text = title, AccessibleName = title, AutoSize = true, AutoEllipsis = true,
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Panel,
            ForeColor = primary ? SelectedText : Text, Padding = new Padding(8, 2, 8, 2),
        };
        button.FlatAppearance.BorderColor = Edge;
        button.FlatAppearance.MouseOverBackColor = primary ? Accent : Hover;
        return button;
    }

    public static ContextMenuStrip Menu() => new()
    {
        BackColor = Panel, ForeColor = Text, Renderer = Renderer, ShowImageMargin = false,
    };

    public static void StyleStrip(ToolStrip strip) => strip.Renderer = Renderer;

    public static void Message(Form owner, string title, string message)
    {
        using var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(460, 145),
            ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
        };
        StyleForm(dialog);
        var label = new Label { Text = message, Dock = DockStyle.Fill, Padding = new Padding(16), AutoEllipsis = false };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 0, 8, 0) };
        var ok = TextButton("OK", primary: true);
        ok.DialogResult = DialogResult.OK;
        bar.Controls.Add(ok);
        dialog.Controls.Add(label);
        dialog.Controls.Add(bar);
        dialog.AcceptButton = ok;
        dialog.CancelButton = ok;
        dialog.ShowDialog(owner);
    }

    internal sealed class IconButton : Button
    {
        private readonly int glyph;
        private bool hovered;
        private bool pressed;
        private bool selected;

        public IconButton(string title, int codepoint, ToolTip tooltip, Action action)
        {
            glyph = codepoint;
            AccessibleName = title;
            AccessibleRole = AccessibleRole.PushButton;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Size = new Size(32, 32);
            Margin = new Padding(1, 0, 1, 0);
            tooltip.SetToolTip(this, title);
            Click += (_, _) => action();
        }

        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value)
                    return;
                selected = value;
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                Invalidate();
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new IconAccessibility(this);

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Enabled && Selected ? Accent : Enabled && (hovered || pressed) ? Hover : Parent?.BackColor ?? Frame);
            var icon = Icons.Value[glyph];
            var size = LogicalToDeviceUnits(20);
            var saved = e.Graphics.Save();
            e.Graphics.TranslateTransform((Width - size) / 2f, (Height - size) / 2f);
            e.Graphics.ScaleTransform(size, size);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(!Enabled ? Disabled : Selected ? SelectedText : Chrome.Text);
            e.Graphics.FillPath(brush, icon);
            e.Graphics.Restore(saved);
            if (Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2),
                    Selected ? SelectedText : Accent, Selected ? Accent : Frame);
        }

        private sealed class IconAccessibility(IconButton owner) : ButtonBaseAccessibleObject(owner)
        {
            public override AccessibleStates State => base.State |
                (owner.Selected ? AccessibleStates.Pressed : AccessibleStates.None);
        }
    }

    private sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == true ? Text : Disabled;
            base.OnRenderArrow(e);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Edge;
        public override Color MenuItemBorder => Edge;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Panel;
        public override Color MenuItemPressedGradientEnd => Panel;
        public override Color StatusStripGradientBegin => Toolbar;
        public override Color StatusStripGradientEnd => Toolbar;
        public override Color SeparatorDark => Edge;
        public override Color SeparatorLight => Edge;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}




