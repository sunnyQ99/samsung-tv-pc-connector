using System.Text.Json;

namespace TVCitation.Control;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(15, 21, 29);
    private static readonly Color Surface = Color.FromArgb(24, 34, 46);
    private static readonly Color Border = Color.FromArgb(42, 56, 72);
    private static readonly Color Foreground = Color.FromArgb(235, 241, 247);
    private static readonly Color Muted = Color.FromArgb(143, 161, 180);
    private static readonly Color Accent = Color.FromArgb(62, 211, 181);
    private readonly BackendClient backend;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly SemaphoreSlim pointerCommands = new(1, 1);
    private readonly TvPreview preview = new();
    private readonly VideoView video;
    private readonly Label connectionLabel = TextLabel("●  TV not connected", 10, Muted);
    private readonly Label fpsLabel = TextLabel("Waiting for TV picture", 9, Muted);
    private readonly Label messageLabel = TextLabel("Starting local control service…", 9, Muted);
    private readonly Label pointerHint = TextLabel("Use arrow keys or the remote on the right", 9, Muted);
    private readonly Label castLabel = TextLabel("Send the PC desktop and video to the TV", 9, Muted);
    private readonly Label audioHint = TextLabel("PC audio is restored when casting stops", 8.5f, Muted);
    private readonly TextBox textInput = new();
    private readonly CheckBox mouseToggle = new() { Text = "Mouse control", Checked = true, Enabled = false, AutoSize = true };
    private readonly CheckBox tvOnlyAudio = new() { Text = "Play audio on TV only", Checked = true, AutoSize = true };
    private readonly Button connectButton = ActionButton("Connect TV", false);
    private readonly Button castButton = ActionButton("Cast PC to TV", true);
    private readonly Button stopCastButton = ActionButton("Stop casting", false);
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer pointerTimer = new() { Interval = 32 };
    private bool connected;
    private bool videoConnected;
    private bool pointerAvailable;
    private bool casting;
    private bool castOperationBusy;
    private bool castSubmitting;
    private bool? appliedAudioOption;
    private bool statusInFlight;
    private bool moveInFlight;
    private bool initializing;
    private bool autoConnectRequested;
    private bool connectRequestAcknowledged;
    private bool closing;
    private bool shutdownCompleted;
    private Task? shutdownTask;
    private Task playerReleaseTask = Task.CompletedTask;
    private Task previewReleaseTask = Task.CompletedTask;
    private Point? pendingMove;
    private Point? playerPendingMove;
    private Point lastPointerPoint = new(960, 540);
    private MouseButtons pressedMouse;
    private long? playerPressId;
    private Point playerPressPoint = new(960, 540);
    private long lastKeyboardAt;
    private string? lastNotice;
    private double? decodedFps;
    private long decodedFpsAt;

    public MainForm(Uri endpoint, bool autoConnect = true, bool initializeOnShown = true, string? credentialPath = null, string? browserDataDirectory = null)
    {
        backend = new BackendClient(endpoint, credentialPath);
        autoConnectRequested = autoConnect;
        video = new VideoView(endpoint, backend, browserDataDirectory);
        Text = "Samsung TV PC Connector";
        ClientSize = new Size(1240, 790);
        MinimumSize = new Size(1050, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Foreground;
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = SystemIcons.Application;
        DoubleBuffered = true;
        BuildLayout();
        preview.Controls.Add(video);
        BindEvents();
        if (initializeOnShown) Shown += async (_, _) => await InitializeAsync(autoConnect);
        FormClosing += OnFormClosing;
    }

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 14), ColumnCount = 1, RowCount = 3, BackColor = Background };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(shell);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        var brand = new Panel { Dock = DockStyle.Fill };
        var title = TextLabel("TV Control", 21, Foreground, FontStyle.Bold);
        title.Dock = DockStyle.Top;
        title.Height = 36;
        var subtitle = TextLabel("TVCITATION   /   SAMSUNG", 8.5f, Muted);
        subtitle.Dock = DockStyle.Bottom;
        subtitle.Height = 22;
        brand.Controls.Add(title);
        brand.Controls.Add(subtitle);
        header.Controls.Add(brand, 0, 0);
        connectionLabel.Dock = DockStyle.Fill;
        connectionLabel.TextAlign = ContentAlignment.MiddleRight;
        connectionLabel.Padding = new Padding(0, 0, 18, 0);
        header.Controls.Add(connectionLabel, 1, 0);
        connectButton.Dock = DockStyle.Fill;
        connectButton.Margin = new Padding(0, 12, 0, 12);
        header.Controls.Add(connectButton, 2, 0);
        shell.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 14, 0, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 314));
        shell.Controls.Add(body, 0, 1);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 18, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        body.Controls.Add(left, 0, 0);
        var previewHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        previewHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        var previewTitle = TextLabel("Live TV picture", 11, Foreground, FontStyle.Bold);
        previewTitle.Dock = DockStyle.Fill;
        previewHeading.Controls.Add(previewTitle, 0, 0);
        fpsLabel.Dock = DockStyle.Fill;
        fpsLabel.TextAlign = ContentAlignment.MiddleRight;
        previewHeading.Controls.Add(fpsLabel, 1, 0);
        left.Controls.Add(previewHeading, 0, 0);
        var previewBorder = new Panel { Dock = DockStyle.Fill, BackColor = Border, Padding = new Padding(1), Margin = Padding.Empty };
        previewBorder.Controls.Add(preview);
        left.Controls.Add(previewBorder, 0, 1);
        pointerHint.Dock = DockStyle.Fill;
        left.Controls.Add(pointerHint, 0, 2);

        var inputCard = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(12, 6, 12, 10), ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        inputCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputCard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        inputCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        inputCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var inputLabel = TextLabel("Send text to the TV input field", 9, Muted);
        inputLabel.Dock = DockStyle.Fill;
        inputCard.Controls.Add(inputLabel, 0, 0);
        inputCard.SetColumnSpan(inputLabel, 2);
        textInput.Dock = DockStyle.Fill;
        textInput.BackColor = Color.FromArgb(12, 19, 27);
        textInput.ForeColor = Foreground;
        textInput.BorderStyle = BorderStyle.FixedSingle;
        textInput.PlaceholderText = "e.g. a URL or search text";
        textInput.Margin = new Padding(0, 3, 10, 0);
        textInput.AccessibleName = "Text to send to the TV";
        inputCard.Controls.Add(textInput, 0, 1);
        var send = ActionButton("Send", false);
        send.Dock = DockStyle.Fill;
        send.Margin = Padding.Empty;
        send.Click += async (_, _) => await SendTextAsync();
        inputCard.Controls.Add(send, 1, 1);
        left.Controls.Add(inputCard, 0, 3);

        var rightHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var right = new TableLayoutPanel { Dock = DockStyle.Top, Height = 564, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 296));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 222));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rightHost.Controls.Add(right);
        body.Controls.Add(rightHost, 1, 0);
        right.Controls.Add(BuildRemoteCard(), 0, 0);
        right.Controls.Add(BuildCastCard(), 0, 1);
        var note = TextLabel("Click the live picture to control TV apps.", 8.5f, Muted);
        note.Dock = DockStyle.Fill;
        note.Padding = new Padding(3, 10, 3, 0);
        note.TextAlign = ContentAlignment.TopLeft;
        right.Controls.Add(note, 0, 2);
        messageLabel.Dock = DockStyle.Fill;
        messageLabel.AutoEllipsis = true;
        shell.Controls.Add(messageLabel, 0, 2);
    }

    private System.Windows.Forms.Control BuildRemoteCard()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(16, 12, 16, 12), ColumnCount = 1, RowCount = 5, Margin = new Padding(0, 0, 0, 12) };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = TextLabel("Remote", 11, Foreground, FontStyle.Bold);
        title.Dock = DockStyle.Fill;
        card.Controls.Add(title, 0, 0);
        var dpad = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = new Padding(28, 0, 28, 0) };
        for (var i = 0; i < 3; i++) { dpad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f)); dpad.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333f)); }
        AddRemote(dpad, "↑", "KEY_UP", 1, 0);
        AddRemote(dpad, "←", "KEY_LEFT", 0, 1);
        AddRemote(dpad, "OK", "KEY_ENTER", 1, 1, true);
        AddRemote(dpad, "→", "KEY_RIGHT", 2, 1);
        AddRemote(dpad, "↓", "KEY_DOWN", 1, 2);
        card.Controls.Add(dpad, 0, 1);
        var navigation = ButtonRow(("Home", "KEY_HOME"), ("Back", "KEY_RETURN"), ("Play/Pause", "KEY_PLAYPAUSE"));
        card.Controls.Add(navigation, 0, 2);
        card.Controls.Add(ButtonRow(("Vol −", "KEY_VOLDOWN"), ("Mute", "KEY_MUTE"), ("Vol +", "KEY_VOLUP")), 0, 3);
        mouseToggle.ForeColor = Muted;
        mouseToggle.Margin = new Padding(3, 5, 0, 0);
        mouseToggle.Font = new Font(Font.FontFamily, 9);
        card.Controls.Add(mouseToggle, 0, 4);
        return card;
    }

    private System.Windows.Forms.Control BuildCastCard()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(16, 12, 16, 12), ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        foreach (var height in new[] { 28, 28, 45, 32, 23, 38 }) card.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var title = TextLabel("PC casting", 11, Foreground, FontStyle.Bold);
        title.Dock = DockStyle.Fill;
        card.Controls.Add(title, 0, 0);
        castLabel.Dock = DockStyle.Fill;
        card.Controls.Add(castLabel, 0, 1);
        castButton.Dock = DockStyle.Fill;
        castButton.Margin = new Padding(0, 0, 0, 3);
        card.Controls.Add(castButton, 0, 2);
        tvOnlyAudio.ForeColor = Foreground;
        tvOnlyAudio.Margin = new Padding(0, 8, 0, 0);
        card.Controls.Add(tvOnlyAudio, 0, 3);
        audioHint.Dock = DockStyle.Fill;
        card.Controls.Add(audioHint, 0, 4);
        stopCastButton.Dock = DockStyle.Fill;
        stopCastButton.Margin = Padding.Empty;
        card.Controls.Add(stopCastButton, 0, 5);
        return card;
    }

    private System.Windows.Forms.Control ButtonRow(params (string Text, string Key)[] items)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = items.Length, Margin = new Padding(0, 3, 0, 0) };
        for (var index = 0; index < items.Length; index++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / items.Length));
            AddRemote(row, items[index].Text, items[index].Key, index, 0);
        }
        return row;
    }

    private void AddRemote(TableLayoutPanel parent, string text, string key, int column, int row, bool accent = false)
    {
        var button = ActionButton(text, accent);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(3);
        button.Font = new Font(Font.FontFamily, text.Length > 3 ? 8.5f : 10, FontStyle.Regular);
        button.AccessibleName = "TV " + text;
        button.Click += async (_, _) => await SendKeyAsync(key);
        parent.Controls.Add(button, column, row);
    }

    private void BindEvents()
    {
        video.PlayerError += text => SetNotice(text, true);
        video.PlayerMessage += async message => await OnPlayerMessageAsync(message);
        mouseToggle.CheckedChanged += async (_, _) =>
        {
            video.UpdateInputAvailability(connected, connected && pointerAvailable && mouseToggle.Checked);
            if (!mouseToggle.Checked) await ReleaseHeldPointersAsync();
        };
        video.Leave += async (_, _) => await ReleasePlayerPointerAsync();
        preview.Leave += async (_, _) => await ReleasePreviewPointerAsync();
        Deactivate += async (_, _) => await ReleaseHeldPointersAsync();
        connectButton.Click += async (_, _) =>
        {
            if (initializing) return;
            if (!connected) { await InitializeAsync(); return; }
            await DisconnectAsync();
        };
        castButton.Click += async (_, _) =>
        {
            if (castSubmitting || castOperationBusy) return;
            if (casting && appliedAudioOption.HasValue && appliedAudioOption.Value != tvOnlyAudio.Checked)
            { SetNotice("The audio setting applies to the next session. Stop casting first, then reconnect."); return; }
            castSubmitting = true;
            castButton.Enabled = false;
            stopCastButton.Enabled = false;
            try
            {
                await RunCommandAsync("/cast/start", new { tvOnlyAudio = tvOnlyAudio.Checked }, "Cast request sent, preparing the connection…");
                await PollStatusAsync();
            }
            finally
            {
                castSubmitting = false;
                if (!closing) { castButton.Enabled = !castOperationBusy; stopCastButton.Enabled = !castOperationBusy; }
            }
        };
        stopCastButton.Click += async (_, _) =>
        {
            if (castSubmitting || castOperationBusy) return;
            castSubmitting = true;
            castButton.Enabled = false;
            stopCastButton.Enabled = false;
            try
            {
                await RunCommandAsync("/cast/stop", null, "Stopping casting and restoring PC audio…");
                await PollStatusAsync();
            }
            finally
            {
                castSubmitting = false;
                if (!closing) { castButton.Enabled = !castOperationBusy; stopCastButton.Enabled = !castOperationBusy; }
            }
        };
        tvOnlyAudio.CheckedChanged += (_, _) =>
        {
            if ((casting || castOperationBusy) && !closing)
            { audioHint.Text = "Audio setting applies to the next session"; SetNotice("The audio setting applies to the next session; the current cast keeps playing."); }
        };
        textInput.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.SuppressKeyPress = true;
            await SendTextAsync();
        };
        preview.KeyDown += async (_, args) =>
        {
            if (!preview.Focused || !connected) return;
            var key = args.KeyCode switch
            {
                Keys.Up => "KEY_UP", Keys.Down => "KEY_DOWN", Keys.Left => "KEY_LEFT", Keys.Right => "KEY_RIGHT",
                Keys.Enter => "KEY_ENTER", Keys.Back or Keys.Escape => "KEY_RETURN", Keys.Home => "KEY_HOME",
                Keys.Space => "KEY_PLAYPAUSE", Keys.Add or Keys.Oemplus => "KEY_VOLUP", Keys.Subtract or Keys.OemMinus => "KEY_VOLDOWN", Keys.M => "KEY_MUTE",
                _ => null
            };
            if (key is null) return;
            args.SuppressKeyPress = true;
            var now = Environment.TickCount64;
            if (now - lastKeyboardAt < 75) return;
            lastKeyboardAt = now;
            await SendKeyAsync(key);
        };
        preview.MouseMove += (_, args) =>
        {
            if (CanUsePointer && preview.ToScreenCoordinates(args.Location) is Point point)
                pendingMove = lastPointerPoint = point;
        };
        preview.MouseDown += async (_, args) =>
        {
            if (!CanUsePointer || args.Button != MouseButtons.Left || preview.ToScreenCoordinates(args.Location) is not Point point) return;
            pressedMouse = args.Button;
            lastPointerPoint = point;
            preview.Capture = true;
            await SendPointerAsync("down", point, args.Button, 0);
        };
        preview.MouseUp += async (_, args) =>
        {
            var point = preview.ToScreenCoordinates(args.Location) ?? lastPointerPoint;
            await ReleasePreviewPointerAsync(point);
        };
        preview.MouseCaptureChanged += async (_, _) => { if (!preview.Capture) await ReleasePreviewPointerAsync(); };
        statusTimer.Tick += async (_, _) => await PollStatusAsync();
        pointerTimer.Tick += async (_, _) =>
        {
            if (moveInFlight || !CanUsePointer || pendingMove is not Point point) return;
            pendingMove = null;
            moveInFlight = true;
            try { await SendPointerAsync("move", point, pressedMouse, 0, quiet: true); }
            finally { moveInFlight = false; }
        };
    }

    private bool CanUsePointer => connected && pointerAvailable && mouseToggle.Checked && preview.Focused && !closing;

    private async Task InitializeAsync(bool connectToTv = true)
    {
        if (initializing || closing) return;
        autoConnectRequested = connectToTv;
        initializing = true;
        connectButton.Enabled = false;
        SetNotice("Preparing the local service and TV connection…");
        var launched = false;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        try
        {
            while (DateTime.UtcNow < deadline && !closing)
            {
                try
                {
                    var status = await backend.GetStatusAsync(lifetime.Token);
                    ApplyStatus(status);
                    if (connectToTv)
                    {
                        await backend.PostAsync("/connect", null, lifetime.Token);
                        connectRequestAcknowledged = true;
                        SetNotice("TV connection requested, preparing live picture and remote.");
                    }
                    else SetNotice(connected ? "TV remote connected; the player shows the picture status." : "Service ready. Click \"Connect TV\" to start.");
                    statusTimer.Start(); pointerTimer.Start();
                    await video.StartAsync();
                    await PollStatusAsync();
                    return;
                }
                catch (Exception) when (!lifetime.IsCancellationRequested)
                {
                    if (!launched)
                    {
                        var launcher = BackendClient.FindBackendLauncher();
                        if (launcher is null) throw new FileNotFoundException("backend\\start-backend.ps1 was not found. Keep the app inside the project folder structure.");
                        BackendClient.StartBackend(launcher);
                        launched = true;
                    }
                }
                await Task.Delay(700, lifetime.Token);
            }
            if (!closing) throw new TimeoutException("The control service is not ready yet. Click \"Connect TV\" to retry later.");
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception exception)
        {
            ApplyConnected(false);
            preview.EmptyMessage = "Waiting for TV picture";
            preview.Invalidate();
            SetNotice(exception.Message, true);
        }
        finally
        {
            initializing = false;
            if (!closing) { connectButton.Enabled = true; statusTimer.Start(); }
        }
    }

    internal async Task DisconnectAsync()
    {
        autoConnectRequested = false;
        connectRequestAcknowledged = false;
        await RunCommandAsync("/disconnect", null, "Disconnected from the TV");
        ApplyConnected(false);
    }

    internal async Task PollStatusAsync()
    {
        if (statusInFlight || closing) return;
        statusInFlight = true;
        try
        {
            var status = await backend.GetStatusAsync(lifetime.Token);
            ApplyStatus(status);
            // A backend can restart entirely between successful polls. Its lost connection intent
            // must be reapplied even when this GUI never observed an HTTP failure.
            var backendAutoConnectDisabled = StatusValue.Boolean(status, "autoConnect") == false;
            if (autoConnectRequested && (!connectRequestAcknowledged || backendAutoConnectDisabled) && !initializing)
            {
                await backend.PostAsync("/connect", null, lifetime.Token);
                connectRequestAcknowledged = true;
                pointerTimer.Start();
                await video.StartAsync();
            }
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception exception) { connectRequestAcknowledged = false; ApplyConnected(false); SetNotice(FriendlyError(exception), true); }
        finally { statusInFlight = false; }
    }

    internal void ApplyStatus(JsonElement status)
    {
        if (closing) return;
        var isConnected = StatusValue.Boolean(status, "controlConnected", "connected", "isConnected") ?? false;
        videoConnected = StatusValue.Boolean(status, "videoConnected") ?? false;
        ApplyConnected(isConnected);
        pointerAvailable = StatusValue.Boolean(status, "pointerAvailable") ?? false;
        if (!pointerAvailable) _ = ReleaseHeldPointersAsync();
        mouseToggle.Enabled = connected && pointerAvailable;
        video.UpdateInputAvailability(connected, connected && pointerAvailable && mouseToggle.Checked);
        pointerHint.Text = connected && pointerAvailable
            ? "Left-click to use apps; arrows, Enter and Esc work as remote"
            : "Arrows, Enter and Esc work as remote; mouse unavailable";
        var active = StatusValue.Boolean(status, "castActive", "casting");
        if (active.HasValue) casting = active.Value;
        var audioPolicy = StatusValue.Boolean(status, "tvOnlyAudio");
        if (audioPolicy.HasValue) appliedAudioOption = audioPolicy;
        audioHint.Text = casting && appliedAudioOption.HasValue && appliedAudioOption.Value != tvOnlyAudio.Checked
            ? "Audio setting applies to the next session" : "PC audio is restored when casting stops";
        castLabel.Text = casting ? "PC screen is casting to the TV" : "Send the PC desktop and video to the TV";
        var statusMessage = StatusValue.Text(status, "message", "error");
        if (!string.IsNullOrWhiteSpace(statusMessage)) SetNotice(statusMessage);
        var operation = StatusValue.Find(status, "castOperation");
        castOperationBusy = operation.HasValue && StatusValue.Boolean(operation.Value, "busy") == true;
        castButton.Enabled = stopCastButton.Enabled = !castSubmitting && !castOperationBusy;
        castButton.Text = castOperationBusy ? "Casting in progress…" : "Cast PC to TV";
        if (operation.HasValue)
        {
            var operationMessage = StatusValue.Text(operation.Value, "message");
            var failed = StatusValue.Boolean(operation.Value, "ok") == false || StatusValue.Text(operation.Value, "state") == "error";
            var recovery = StatusValue.Boolean(operation.Value, "needsRecovery") == true || StatusValue.Boolean(status, "needsRecovery") == true;
            if (castOperationBusy) castLabel.Text = operationMessage ?? "Preparing PC casting…";
            else if (failed && !casting) castLabel.Text = "Casting did not finish; retry as prompted";
            if ((castOperationBusy || failed) && !string.IsNullOrWhiteSpace(operationMessage)) SetNotice(operationMessage, failed);
            if (recovery)
            {
                audioHint.Text = "PC audio needs restoring; click Stop casting";
                SetNotice((operationMessage ?? "PC audio has not been restored yet.") + " Click \"Stop casting\" to retry.", true);
            }
        }
        var videoFps = Environment.TickCount64 - decodedFpsAt < 3000 ? decodedFps : StatusValue.Number(status, "videoFps");
        fpsLabel.Text = !videoConnected ? "Live picture unavailable" : videoFps.HasValue ? $"Video · {videoFps:0.0} fps" : "Video connected";
    }

    private void ApplyConnected(bool value)
    {
        if (closing) return;
        connected = value;
        connectionLabel.Text = value ? videoConnected ? "●  TV connected" : "●  Remote connected" : "●  TV not connected";
        connectionLabel.ForeColor = value ? Accent : Muted;
        connectButton.Text = value ? "Disconnect" : "Connect TV";
        mouseToggle.Enabled = value && pointerAvailable;
        if (!value) { pendingMove = null; _ = ReleaseHeldPointersAsync(); }
    }

    private async Task SendKeyAsync(string key) => await RunCommandAsync("/remote", new { key }, null, quiet: true);

    private async Task OnPlayerMessageAsync(JsonElement message)
    {
        if (closing) return;
        var type = StatusValue.Text(message, "type");
        if (type == "videoStats")
        {
            var fps = StatusValue.Number(message, "fps");
            if (videoConnected && fps is >= 0 and <= 1000)
            {
                decodedFps = fps; decodedFpsAt = Environment.TickCount64;
                fpsLabel.Text = $"Video · {fps:0.0} fps";
            }
            return;
        }
        if (type == "pointer")
        {
            var action = StatusValue.Text(message, "action");
            var x = StatusValue.Number(message, "x");
            var y = StatusValue.Number(message, "y");
            if (action is not ("move" or "down" or "up") || StatusValue.Text(message, "button") != "left" || x is null or < 0 or >= 1920 || y is null or < 0 or >= 1080) return;
            var point = new Point((int)x.Value, (int)y.Value);
            var press = StatusValue.Number(message, "pressId");
            var validPress = press is >= 1 and <= 9007199254740991 && Math.Truncate(press.Value) == press.Value;
            // A release belongs to the accepted down, even after focus/availability changes.
            if (action == "up")
            {
                if (validPress) await ReleasePlayerPointerAsync((long)press!.Value, point);
                return;
            }
            if (!connected || !video.ContainsFocus || !pointerAvailable || !mouseToggle.Checked) return;
            if (action == "down")
            {
                if (!validPress || playerPressId.HasValue) return;
                playerPressId = (long)press!.Value;
            }
            playerPressPoint = point;
            lastPointerPoint = point;
            if (action == "move")
            {
                // Latest wins: keep the newest point while one move is in flight instead of dropping it.
                playerPendingMove = point;
                if (moveInFlight) return;
                moveInFlight = true;
                try
                {
                    while (playerPendingMove is Point next)
                    {
                        playerPendingMove = null;
                        await SendPointerAsync(action, next, MouseButtons.Left, 0, quiet: true);
                    }
                }
                finally { moveInFlight = false; }
            }
            else
            {
                playerPendingMove = null; // A down is a barrier: never replay an older move after it.
                await SendPointerAsync(action, point, MouseButtons.Left, 0, quiet: true);
            }
            return;
        }
        if (!connected || !video.ContainsFocus) return;
        if (type == "remote")
        {
            var key = StatusValue.Text(message, "key");
            if (key is not ("KEY_UP" or "KEY_DOWN" or "KEY_LEFT" or "KEY_RIGHT" or "KEY_ENTER" or "KEY_RETURN" or "KEY_HOME" or "KEY_PLAYPAUSE" or "KEY_VOLUP" or "KEY_VOLDOWN" or "KEY_MUTE")) return;
            var now = Environment.TickCount64;
            if (now - lastKeyboardAt < 75) return;
            lastKeyboardAt = now;
            await SendKeyAsync(key);
        }
    }

    private Task ReleasePlayerPointerAsync(long? expectedPressId = null, Point? point = null)
    {
        if (expectedPressId.HasValue && playerPressId != expectedPressId) return Task.CompletedTask;
        playerPendingMove = null; // Only a matching release or unconditional focus/close barrier clears moves.
        if (!playerPressId.HasValue) return playerReleaseTask;
        playerPressId = null; // Claim once before the existing FIFO may await an earlier down.
        return playerReleaseTask = SendPointerAsync("up", point ?? playerPressPoint, MouseButtons.Left, 0, quiet: true);
    }

    private Task ReleasePreviewPointerAsync(Point? point = null)
    {
        if (pressedMouse == MouseButtons.None) return previewReleaseTask;
        var button = pressedMouse;
        pressedMouse = MouseButtons.None;
        pendingMove = null;
        preview.Capture = false;
        return previewReleaseTask = SendPointerAsync("up", point ?? lastPointerPoint, button, 0, quiet: true);
    }

    private Task ReleaseHeldPointersAsync() => Task.WhenAll(ReleasePlayerPointerAsync(), ReleasePreviewPointerAsync());

    private async Task SendPointerAsync(string action, Point point, MouseButtons button, int delta, bool quiet = false)
    {
        var name = button switch { MouseButtons.Right => "right", MouseButtons.Middle => "middle", _ => "left" };
        await RunCommandAsync("/pointer", new { action, x = point.X, y = point.Y, button = name, delta }, null, quiet, allowWhileClosing: action == "up");
    }

    private async Task SendTextAsync()
    {
        var text = textInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (await RunCommandAsync("/text", new { text }, "Text sent to the TV")) textInput.Clear();
    }

    private async Task<bool> RunCommandAsync(string path, object? body, string? successMessage, bool quiet = false, bool allowWhileClosing = false)
    {
        if (closing && !allowWhileClosing) return false;
        var acquired = false;
        // Preserve pointer down/up order, independently of slow remote/text/cast requests.
        var gate = path == "/pointer" ? pointerCommands : commands;
        try
        {
            await gate.WaitAsync(lifetime.Token);
            acquired = true;
            await backend.PostAsync(path, body, lifetime.Token);
            if (!quiet && successMessage is not null) SetNotice(successMessage);
            return true;
        }
        catch (OperationCanceledException) when (closing) { return false; }
        catch (Exception exception) { SetNotice(FriendlyError(exception), true); return false; }
        finally { if (acquired) gate.Release(); }
    }

    private void SetNotice(string text, bool error = false)
    {
        if (closing || lastNotice == text) return;
        lastNotice = text;
        messageLabel.Text = text;
        messageLabel.ForeColor = error ? Color.FromArgb(238, 174, 119) : Muted;
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        HttpRequestException => "Cannot reach the local control service. Please reconnect.",
        TaskCanceledException => "The operation timed out. Please try again.",
        _ => exception.Message
    };

    private async void OnFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (shutdownCompleted) return;
        args.Cancel = true;
        if (closing) return;
        try { await ShutdownAsync(); }
        finally
        {
            shutdownCompleted = true;
            if (!IsDisposed && !Disposing) Close();
        }
    }

    private Task ShutdownAsync() => shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        closing = true;
        autoConnectRequested = false;
        statusTimer.Stop(); pointerTimer.Stop();
        pendingMove = null;
        // Even missing credentials can fail synchronously. Return from the first
        // FormClosing event before cleanup can complete and call Close again.
        await Task.Yield();
        // Stop accepting new presses, but let the already accepted down and its
        // once-only release finish through the same FIFO before cancelling it.
        // Also join a release already queued by blur/lost capture.
        try { await ReleaseHeldPointersAsync().WaitAsync(TimeSpan.FromMilliseconds(700)); }
        catch (Exception) { /* The bounded disconnect below also releases native buttons. */ }
        lifetime.Cancel();
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            // Bypass the ordinary command queue: cast preparation must not hold
            // control cleanup. This disconnect does not stop the PC-to-TV cast.
            await backend.PostAsync("/disconnect", null, cleanup.Token).WaitAsync(cleanup.Token);
        }
        catch (Exception) { /* Backend heartbeat/transport cleanup remains the fallback. */ }
        finally
        {
            preview.Image?.Dispose(); preview.Image = null;
            backend.Dispose();
            statusTimer.Dispose(); pointerTimer.Dispose();
        }
        // The shared backend process and an existing cast keep running.
    }

    private static Label TextLabel(string text, float size, Color color, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, ForeColor = color, BackColor = Color.Transparent,
        Font = new Font("Microsoft YaHei UI", size, style), Margin = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
    };

    private static Button ActionButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Accent : Color.FromArgb(34, 47, 62),
            ForeColor = primary ? Color.FromArgb(9, 36, 32) : Foreground,
            Font = new Font("Microsoft YaHei UI", 10, primary ? FontStyle.Bold : FontStyle.Regular),
            Height = 40, UseVisualStyleBackColor = false, TabStop = true
        };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(99, 227, 203) : Color.FromArgb(45, 62, 80);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(40, 180, 154) : Color.FromArgb(28, 40, 54);
        return button;
    }
}
