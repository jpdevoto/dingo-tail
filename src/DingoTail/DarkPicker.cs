// Copyright (C) 2026 Juan Pablo Devoto
// SPDX-License-Identifier: GPL-3.0-only

namespace DingoTail;

internal sealed class DarkPicker : Button
{
    private readonly ListBox list = new()
    {
        BackColor = Chrome.Panel, ForeColor = Chrome.Text, BorderStyle = BorderStyle.None,
        IntegralHeight = false, AccessibleName = "Choices", DrawMode = DrawMode.OwnerDrawFixed,
    };
    private readonly ContextMenuStrip popup = Chrome.Menu();
    private readonly ToolStripControlHost host;
    private int selectedIndex = -1;

    public DarkPicker()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderColor = Chrome.Edge;
        FlatAppearance.MouseOverBackColor = Chrome.Hover;
        BackColor = Chrome.Panel;
        ForeColor = Chrome.Text;
        Height = 29;
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        host = new ToolStripControlHost(list) { AutoSize = false, Margin = Padding.Empty };
        popup.Items.Add(host);
        popup.Padding = new Padding(1);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
                return;
            var selected = e.State.HasFlag(DrawItemState.Selected);
            using var brush = new SolidBrush(selected ? Chrome.Accent : Chrome.Panel);
            e.Graphics.FillRectangle(brush, e.Bounds);
            var bounds = Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(7), 0);
            TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString(), list.Font, bounds,
                selected ? Chrome.SelectedText : Chrome.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (e.State.HasFlag(DrawItemState.Focus))
                e.DrawFocusRectangle();
        };
        popup.Opened += (_, _) => list.Focus();
        popup.Closed += (_, e) =>
        {
            if (e.CloseReason is ToolStripDropDownCloseReason.Keyboard or ToolStripDropDownCloseReason.CloseCalled && CanFocus)
                Focus();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                Commit();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                popup.Close(ToolStripDropDownCloseReason.Keyboard);
                e.SuppressKeyPress = true;
            }
        };
        list.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && list.IndexFromPoint(e.Location) >= 0)
                Commit();
        };
    }

    public ListBox.ObjectCollection Items => list.Items;
    public object? SelectedItem => selectedIndex >= 0 && selectedIndex < Items.Count ? Items[selectedIndex] : null;
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (selectedIndex == value)
                return;
            selectedIndex = value;
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? SelectedIndexChanged;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Items.Count == 0)
            return;
        list.AccessibleName = AccessibleName + " choices";
        list.Font = Font;
        list.ItemHeight = Font.Height + LogicalToDeviceUnits(5);
        host.Size = new Size(Math.Max(1, Width - 2), list.ItemHeight * Math.Min(9, Items.Count) + 2);
        list.SelectedIndex = Math.Clamp(selectedIndex, 0, Items.Count - 1);
        popup.Show(this, new Point(0, Height));
        AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Down or Keys.Up or Keys.Enter or Keys.F4 or (Keys.Alt | Keys.Down))
        {
            OnClick(EventArgs.Empty);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new PickerAccessibility(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var inset = LogicalToDeviceUnits(8);
        var arrow = LogicalToDeviceUnits(18);
        var label = new Rectangle(inset, 0, Math.Max(0, Width - 2 * inset - arrow), Height);
        TextRenderer.DrawText(e.Graphics, SelectedItem?.ToString() ?? "Choose…", Font, label, ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var x = Width - inset - arrow / 2;
        var y = Height / 2;
        var radius = LogicalToDeviceUnits(4);
        using var pen = new Pen(Chrome.Muted, LogicalToDeviceUnits(1));
        e.Graphics.DrawLines(pen, new Point[] {new Point(x - radius, y - 2), new Point(x, y + 2), new Point(x + radius, y - 2) });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            popup.Dispose();
        base.Dispose(disposing);
    }

    private void Commit()
    {
        var index = list.SelectedIndex;
        popup.Close();
        if (index >= 0)
            SelectedIndex = index;
    }

    private sealed class PickerAccessibility(DarkPicker owner) : ButtonBaseAccessibleObject(owner)
    {
        public override string? Value => owner.SelectedItem?.ToString();
        public override AccessibleStates State => base.State |
            (owner.popup.Visible ? AccessibleStates.Expanded : AccessibleStates.Collapsed);
    }
}

