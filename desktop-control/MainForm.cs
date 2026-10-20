using System.Drawing;

namespace TVCitation.Remote;

internal sealed class MainForm : Form
{
    private readonly SamsungClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Button> remoteButtons = [];
    private readonly Label connectionLabel = new() { AutoSize = true, Text = "Not connected", ForeColor = Color.DimGray };
    private readonly Label informationLabel = new() { AutoSize = true, Text = "Model: reading…    OS: reading…\nDeveloper mode: reading…" };
    private readonly Label statusLabel = new() { AutoSize = false, Dock = DockStyle.Fill, Text = "Preparing to connect to the TV…", Padding = new Padding(0, 8, 0, 0) };
    private readonly Button connectButton;
    private readonly Button browserButton;
    private bool connecting;
    private bool browserBusy;

    internal MainForm()
    {
        Text = "TVCITATION · Samsung TV Remote";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 248, 251);
        ForeColor = Color.FromArgb(30, 41, 59);
        ClientSize = new Size(700, 650);
        MinimumSize = new Size(670, 670);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 7
        };
        foreach (var height in new float[] { 46, 34, 64, 252, 58, 66, 0 })
            layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var heading = new Label
        {
            Text = "Samsung TV Remote", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true
        };
        layout.Controls.Add(heading, 0, 0);
        var connectionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        connectionRow.Controls.Add(new Label { Text = $"Samsung QN90CA 50  ·  {SamsungClient.Address}", AutoSize = true, Margin = new Padding(0, 3, 20, 0) });
        connectionLabel.Margin = new Padding(0, 3, 0, 0);
        connectionRow.Controls.Add(connectionLabel);
        layout.Controls.Add(connectionRow, 0, 1);
        informationLabel.Dock = DockStyle.Fill;
        informationLabel.Padding = new Padding(0, 10, 0, 0);
        layout.Controls.Add(informationLabel, 0, 2);

        var controlRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        controlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        controlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.Controls.Add(controlRow, 0, 3);

        var pad = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = new Padding(0, 0, 20, 0) };
        for (var i = 0; i < 3; i++)
        {
            pad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            pad.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        }
        pad.Controls.Add(KeyButton("↑", "Up", "KEY_UP"), 1, 0);
        pad.Controls.Add(KeyButton("←", "Left", "KEY_LEFT"), 0, 1);
        var enter = KeyButton("OK", "OK", "KEY_ENTER");
        enter.BackColor = Color.FromArgb(30, 64, 175);
        enter.ForeColor = Color.White;
        pad.Controls.Add(enter, 1, 1);
        pad.Controls.Add(KeyButton("→", "Right", "KEY_RIGHT"), 2, 1);
        pad.Controls.Add(KeyButton("↓", "Down", "KEY_DOWN"), 1, 2);
        controlRow.Controls.Add(pad, 0, 0);

        var functions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = Padding.Empty };
        functions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        functions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 4; i++) functions.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        functions.Controls.Add(KeyButton("Back", "Back", "KEY_RETURN"), 0, 0);
        functions.Controls.Add(KeyButton("Home", "Home", "KEY_HOME"), 1, 0);
        functions.Controls.Add(KeyButton("Menu", "Menu", "KEY_MENU"), 0, 1);
        functions.Controls.Add(KeyButton("Exit", "Exit", "KEY_EXIT"), 1, 1);
        functions.Controls.Add(KeyButton("Vol −", "Volume down", "KEY_VOLDOWN"), 0, 2);
        functions.Controls.Add(KeyButton("Vol +", "Volume up", "KEY_VOLUP"), 1, 2);
        var mute = KeyButton("Mute / Unmute", "Mute / Unmute", "KEY_MUTE");
        functions.Controls.Add(mute, 0, 3);
        functions.SetColumnSpan(mute, 2);
        controlRow.Controls.Add(functions, 1, 0);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        connectButton = MakeButton("Reconnect / Refresh info");
        connectButton.Click += async (_, _) => await ConnectAsync();
        browserButton = MakeButton("Open TV browser");
        browserButton.Enabled = false;
        browserButton.Click += async (_, _) => await OpenBrowserAsync();
        actions.Controls.Add(connectButton, 0, 0);
        actions.Controls.Add(browserButton, 1, 0);
        layout.Controls.Add(actions, 0, 4);
        layout.Controls.Add(statusLabel, 0, 5);
        layout.Controls.Add(new Label
        {
            AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(100, 116, 139),
            Text = "System file access: not available   ·   4K120 casting: not implemented\nA working remote connection does not grant system file access."
        }, 0, 6);

        client.ConnectionChanged += (connected, message) => OnUi(() =>
        {
            connectionLabel.Text = connected ? "● Connected" : "● Not connected";
            connectionLabel.ForeColor = connected ? Color.FromArgb(22, 101, 52) : Color.DimGray;
            UpdateButtons();
            SetStatus(message);
        });
        Shown += async (_, _) => await ConnectAsync();
        FormClosed += (_, _) => { lifetime.Cancel(); client.Dispose(); lifetime.Dispose(); };
    }

    private Button MakeButton(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, Margin = new Padding(4), BackColor = Color.White,
        FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font(Font.FontFamily, 10.5F),
        FlatAppearance = { BorderColor = Color.FromArgb(203, 213, 225), BorderSize = 1 },
        UseVisualStyleBackColor = false
    };

    private Button KeyButton(string text, string label, string key)
    {
        var button = MakeButton(text);
        button.AccessibleName = label;
        button.Enabled = false;
        button.Click += async (_, _) =>
        {
            try
            {
                await client.SendKeyAsync(key, lifetime.Token);
                SetStatus($"Sent \"{label}\". Check the TV screen to confirm it took effect.");
            }
            catch (OperationCanceledException) { }
            catch (RemoteException ex) { SetStatus(ex.Message); }
            catch { SetStatus("Sending the key failed. Reconnect and try again."); }
        };
        remoteButtons.Add(button);
        return button;
    }

    private async Task ConnectAsync()
    {
        if (connecting || IsDisposed) return;
        connecting = true;
        connectButton.Enabled = false;
        connectionLabel.Text = "Connecting…";
        SetStatus("Verifying the paired TV certificate and remote authorization…");
        UpdateButtons();
        var infoTask = RefreshInformationAsync();
        try { await client.ConnectAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { SetStatus("Connection timed out. Check the TV and the local network."); }
        catch (RemoteException ex) { SetStatus(ex.Message); }
        catch { SetStatus("Connection failed. Check that the TV is on and on the same local network."); }
        finally
        {
            await infoTask;
            if (!IsDisposed)
            {
                connecting = false;
                connectButton.Enabled = !browserBusy;
                connectionLabel.Text = client.IsConnected ? "● Connected" : "● Not connected";
                connectionLabel.ForeColor = client.IsConnected ? Color.FromArgb(22, 101, 52) : Color.DimGray;
                UpdateButtons();
            }
        }
    }

    private async Task RefreshInformationAsync()
    {
        try
        {
            var info = await client.ReadInformationAsync(lifetime.Token);
            if (!IsDisposed) informationLabel.Text = $"Model: {info.Model}    OS: {info.OperatingSystem}\nDeveloper mode: {info.DeveloperMode} (reported by the TV API)";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { if (!IsDisposed) informationLabel.Text = "TV info: read timed out\nFixed target: 192.168.1.5"; }
        catch { if (!IsDisposed) informationLabel.Text = "TV info: unavailable\nFixed target: 192.168.1.5"; }
    }

    private async Task OpenBrowserAsync()
    {
        if (browserBusy || !client.IsConnected) return;
        browserBusy = true;
        connectButton.Enabled = false;
        UpdateButtons();
        SetStatus("Requesting the browser and waiting for the TV API to confirm it is visible…");
        try
        {
            var visible = await client.OpenBrowserAsync(lifetime.Token);
            SetStatus(visible ? "Confirmed by the TV API: the browser is visible." : "Browser requested; visibility was not confirmed within 10 seconds.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { SetStatus("Browser request timed out; visibility not confirmed."); }
        catch (RemoteException ex) { SetStatus(ex.Message); }
        catch { SetStatus("Browser request failed; visibility not confirmed."); }
        finally
        {
            if (!IsDisposed)
            {
                browserBusy = false;
                connectButton.Enabled = !connecting;
                UpdateButtons();
            }
        }
    }

    private void UpdateButtons()
    {
        var enabled = client.IsConnected && !connecting && !browserBusy;
        foreach (var button in remoteButtons) button.Enabled = enabled;
        browserButton.Enabled = enabled;
    }

    private void SetStatus(string text) { if (!IsDisposed) statusLabel.Text = text; }

    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { if (InvokeRequired) BeginInvoke(action); else action(); }
        catch (InvalidOperationException) { }
    }
}
