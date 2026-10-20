namespace TVCitation.Control;

internal sealed class TvPreview : PictureBox
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyMessage { get; set; } = "Live picture unavailable";

    public TvPreview()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = true;
        SizeMode = PictureBoxSizeMode.Zoom;
        BackColor = Color.FromArgb(8, 12, 18);
        Dock = DockStyle.Fill;
        AccessibleName = "Live TV picture; click it, then use the arrow keys as a remote";
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Enter or Keys.Escape || base.IsInputKey(keyData);

    public Point? ToScreenCoordinates(Point location, int screenWidth = 1920, int screenHeight = 1080)
    {
        if (Image is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return null;
        var scale = Math.Min((double)ClientSize.Width / Image.Width, (double)ClientSize.Height / Image.Height);
        var width = Image.Width * scale;
        var height = Image.Height * scale;
        var left = (ClientSize.Width - width) / 2;
        var top = (ClientSize.Height - height) / 2;
        if (location.X < left || location.X >= left + width || location.Y < top || location.Y >= top + height) return null;
        return new Point(
            Math.Clamp((int)((location.X - left) / width * screenWidth), 0, screenWidth - 1),
            Math.Clamp((int)((location.Y - top) / height * screenHeight), 0, screenHeight - 1));
    }

    protected override void OnPaint(PaintEventArgs pe)
    {
        base.OnPaint(pe);
        if (Image is null)
        {
            using var title = new Font("Microsoft YaHei UI", 16, FontStyle.Regular);
            using var hint = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
            var bounds = new Rectangle(24, Height / 2 - 45, Math.Max(0, Width - 48), 42);
            TextRenderer.DrawText(pe.Graphics, EmptyMessage, title, bounds, Color.FromArgb(211, 221, 231), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            bounds.Offset(0, 44);
            TextRenderer.DrawText(pe.Graphics, "Use the remote on the right or PC casting", hint, bounds, Color.FromArgb(118, 136, 154), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Focused)
        {
            using var pen = new Pen(Color.FromArgb(62, 211, 181), 2);
            pe.Graphics.DrawRectangle(pen, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        }
    }
}
