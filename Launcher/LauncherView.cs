using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CoopLauncher;

internal sealed partial class LauncherForm
{
	private static readonly Color Ink = Color.FromArgb(25, 23, 24);
	private static readonly Color Surface = Color.FromArgb(39, 34, 35);
	private static readonly Color Muted = Color.FromArgb(195, 181, 170);
	private static readonly Color Accent = Color.FromArgb(244, 182, 114);
	private readonly Label installedValue = new Label(), latestValue = new Label(), contentValue = new Label();
	private readonly Label sourcePath = new Label();
	private readonly Panel homeView = new Panel(), settingsView = new Panel(), diagnostics = new Panel();
	private readonly Button syncButton = new Button(), sourceButton = new Button(), existingButton = new Button();
	private readonly Button overviewTab = new Button(), settingsTab = new Button(), diagnosticsButton = new Button();

	private void InitializeModernView()
	{
		Text = "TLD-COOP · The Long Dark";
		ClientSize = new Size(1100, 730);
		MinimumSize = new Size(900, 680);
		StartPosition = FormStartPosition.CenterScreen;
		Font = new Font("Segoe UI", 10);
		BackColor = Ink;
		ForeColor = Color.FromArgb(249, 242, 234);
		AutoScaleMode = AutoScaleMode.Dpi;
		DoubleBuffered = true;

		var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(28, 4, 28, 16) };
		shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
		shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
		Controls.Add(shell);

		var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 162));
		header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
		var wordmark = TextLabel("TLD-COOP", 22, FontStyle.Bold, Accent);
		wordmark.Dock = DockStyle.Fill;
		wordmark.TextAlign = ContentAlignment.MiddleLeft;
		header.Controls.Add(wordmark, 0, 0);
		overviewTab.Text = "Обзор";
		settingsTab.Text = "Установка и DLC";
		overviewTab.Click += delegate { ShowSection(false); };
		settingsTab.Click += delegate { ShowSection(true); };
		checkButton.Text = "Проверить обновления";
		foreach (var button in new[] { overviewTab, settingsTab, checkButton })
		{
			FormatButton(button);
			button.Dock = DockStyle.Fill;
			button.Margin = new Padding(5, 18, 0, 18);
		}
		header.Controls.Add(overviewTab, 1, 0);
		header.Controls.Add(settingsTab, 2, 0);
		header.Controls.Add(checkButton, 3, 0);
		shell.Controls.Add(header, 0, 0);

		var stage = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
		homeView.Dock = settingsView.Dock = DockStyle.Fill;
		stage.Controls.Add(settingsView);
		stage.Controls.Add(homeView);
		shell.Controls.Add(stage, 0, 1);
		BuildOverview();
		BuildSettings();

		diagnostics.Dock = DockStyle.Top;
		diagnostics.Height = 120;
		diagnostics.Visible = false;
		log.Multiline = log.ReadOnly = true;
		log.ScrollBars = ScrollBars.Vertical;
		log.BackColor = Color.FromArgb(18, 17, 19);
		log.ForeColor = Muted;
		log.BorderStyle = BorderStyle.None;
		log.Font = new Font("Consolas", 9);
		log.Dock = DockStyle.Fill;
		diagnostics.Controls.Add(log);
		shell.Controls.Add(diagnostics, 0, 2);

		var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 0) };
		folderButton.Text = "Открыть папку";
		diagnosticsButton.Text = "Диагностика";
		diagnosticsButton.Click += delegate
		{
			diagnostics.Visible = !diagnostics.Visible;
			diagnosticsButton.Text = diagnostics.Visible ? "Скрыть журнал" : "Диагностика";
		};
		var github = new Button { Text = "Выпуски ↗" };
		github.Click += delegate { Process.Start("https://github.com/" + Repository + "/releases"); };
		foreach (var button in new[] { folderButton, diagnosticsButton, github })
		{
			FormatButton(button);
			button.Size = new Size(131, 32);
			button.Margin = new Padding(0, 0, 8, 0);
			footer.Controls.Add(button);
		}
		var version = TextLabel("Лаунчер " + LauncherVersion.ToString(3), 9, FontStyle.Regular, Muted);
		version.Size = new Size(140, 32);
		version.TextAlign = ContentAlignment.MiddleLeft;
		version.Margin = new Padding(8, 0, 0, 0);
		footer.Controls.Add(version);
		shell.Controls.Add(footer, 0, 3);
		versionLabel.Visible = false;
		ShowSection(false);
	}

	private void BuildOverview()
	{
		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
		homeView.Controls.Add(layout);

		var banner = new ShelterPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
		var title = TextLabel("The Long Dark", 33, FontStyle.Bold, ForeColor);
		title.BackColor = Color.Transparent;
		title.SetBounds(28, 22, 440, 64);
		var description = TextLabel("Кооперативное выживание", 12, FontStyle.Regular, ForeColor);
		description.BackColor = Color.Transparent;
		description.SetBounds(31, 89, 400, 28);
		banner.Controls.Add(title);
		banner.Controls.Add(description);
		layout.Controls.Add(banner, 0, 0);

		var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
		for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
		VersionCard(cards, 0, "Установлено", installedValue);
		VersionCard(cards, 1, "Доступно", latestValue);
		VersionCard(cards, 2, "Дополнения", contentValue);
		layout.Controls.Add(cards, 0, 1);

		var launch = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(14, 5, 12, 5), BackColor = Surface };
		launch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		launch.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		launch.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		launch.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		launch.RowStyles.Add(new RowStyle(SizeType.Absolute, 5));
		var statePanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 12, 0) };
		statusLabel.Font = new Font("Segoe UI Semibold", 11);
		statusLabel.Dock = DockStyle.Fill;
		statusLabel.TextAlign = ContentAlignment.MiddleLeft;
		pathLabel.Dock = DockStyle.Bottom;
		pathLabel.Height = 24;
		pathLabel.ForeColor = Muted;
		pathLabel.AutoEllipsis = true;
		statePanel.Controls.Add(statusLabel);
		statePanel.Controls.Add(pathLabel);
		launch.Controls.Add(statePanel, 0, 0);
		installButton.Text = "Установить";
		FormatButton(installButton);
		installButton.MinimumSize = new Size(156, 48);
		installButton.Anchor = AnchorStyles.Right;
		playButton.Text = "Играть";
		FormatButton(playButton, primary: true);
		playButton.Size = new Size(142, 48);
		playButton.Font = new Font("Segoe UI Semibold", 13);
		playButton.Anchor = AnchorStyles.Right;
		launch.Controls.Add(installButton, 1, 0);
		launch.Controls.Add(playButton, 2, 0);
		progress.Dock = DockStyle.Fill;
		progress.Margin = Padding.Empty;
		launch.Controls.Add(progress, 0, 1);
		launch.SetColumnSpan(progress, 3);
		layout.Controls.Add(launch, 0, 2);
	}

	private static Label TextLabel(string text, float size, FontStyle style, Color color) =>
		new Label { Text = text, Font = new Font("Segoe UI", size, style), ForeColor = color };

	private static void FormatButton(Button button, bool primary = false)
	{
		button.FlatStyle = FlatStyle.Flat;
		button.FlatAppearance.BorderSize = primary ? 0 : 1;
		button.FlatAppearance.BorderColor = Color.FromArgb(89, 72, 64);
		button.BackColor = primary ? Accent : Surface;
		button.ForeColor = primary ? Ink : Color.FromArgb(246, 232, 221);
		button.Cursor = Cursors.Hand;
		button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(255, 203, 149) : Color.FromArgb(66, 51, 47);
	}

	private static void VersionCard(TableLayoutPanel table, int column, string title, Label value)
	{
		var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = new Padding(0, 0, column == 2 ? 0 : 8, 0), Padding = new Padding(14, 9, 8, 6) };
		var label = TextLabel(title, 9, FontStyle.Regular, Muted);
		label.Dock = DockStyle.Top;
		label.Height = 22;
		value.Dock = DockStyle.Fill;
		value.Font = new Font("Segoe UI Semibold", 16);
		value.AutoEllipsis = true;
		panel.Controls.Add(value);
		panel.Controls.Add(label);
		table.Controls.Add(panel, column, 0);
	}

	private void BuildSettings()
	{
		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(0, 8, 0, 0) };
		settingsView.Controls.Add(layout);
		var title = TextLabel("Установка и DLC", 24, FontStyle.Bold, ForeColor);
		title.Dock = DockStyle.Top;
		title.Height = 58;
		layout.Controls.Add(title);
		AddSettingsCard(layout, "Оригинальная игра Steam", sourcePath, sourceButton, "Выбрать папку Steam", delegate
		{
			var path = AskFolder("Оригинальная игра Steam", "tld.exe");
			if (path != null) { state.gameDirectory = path; SaveState(); RefreshView(); }
		});
		AddSettingsCard(layout, "Дополнения", TextLabel("Скопировать установленные в Steam DLC в кооперативную копию.", 10, FontStyle.Regular, Muted),
			syncButton, "Проверить и добавить DLC", async delegate { await SyncContentFromSettings(); });
		AddSettingsCard(layout, "Кооперативная копия", TextLabel("Подключить отдельную папку с уже установленным модом.", 10, FontStyle.Regular, Muted),
			existingButton, "Подключить папку", delegate
			{
				var path = AskFolder("Готовая папка кооператива (player)", "game\tld.exe");
				if (path != null && IsInstalled(path)) { state.installDirectory = path; SaveState(); RefreshView(); }
			});
		testBuilds.Text = "Получать тестовые версии мода";
		testBuilds.Dock = DockStyle.Top;
		testBuilds.Height = 43;
		layout.Controls.Add(testBuilds);
	}

	private void AddSettingsCard(TableLayoutPanel layout, string name, Label description, Button button, string action, EventHandler handler)
	{
		var card = new TableLayoutPanel { Dock = DockStyle.Top, Height = 96, BackColor = Surface, ColumnCount = 2, RowCount = 2, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 9) };
		card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
		card.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
		card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		var label = TextLabel(name, 12, FontStyle.Bold, ForeColor);
		label.Dock = DockStyle.Fill;
		description.Dock = DockStyle.Fill;
		description.ForeColor = Muted;
		description.AutoEllipsis = true;
		button.Text = action;
		FormatButton(button);
		button.Dock = DockStyle.Fill;
		button.Margin = new Padding(10, 12, 0, 12);
		button.Click += handler;
		card.Controls.Add(label, 0, 0);
		card.Controls.Add(description, 0, 1);
		card.Controls.Add(button, 1, 0);
		card.SetRowSpan(button, 2);
		layout.Controls.Add(card);
	}

	private void ShowSection(bool settings)
	{
		settingsView.Visible = settings;
		homeView.Visible = !settings;
		overviewTab.ForeColor = settings ? ForeColor : Accent;
		settingsTab.ForeColor = settings ? Accent : ForeColor;
		overviewTab.AccessibleDescription = settings ? "" : "Текущий раздел";
		settingsTab.AccessibleDescription = settings ? "Текущий раздел" : "";
	}

	private void RefreshDashboard(bool installed, Version current, bool update, bool incomplete)
	{
		installedValue.Text = incomplete ? "Прервано" : current?.ToString(3) ?? (installed ? "Неизвестно" : "Нет");
		latestValue.Text = latest?.Version.ToString(3) ?? "—";
		contentValue.Text = installed && File.Exists(Path.Combine(state.installDirectory, "game", "tld_dlc", "TFTFT", "DLC01.json")) ? "TFTFT" : installed ? "Базовая игра" : "—";
		sourcePath.Text = string.IsNullOrEmpty(state.gameDirectory) ? "Папка определяется при установке" : state.gameDirectory;
		syncButton.Enabled = !busy && installed && !incomplete;
		sourceButton.Enabled = existingButton.Enabled = folderButton.Enabled = !busy;
		playButton.Visible = installed;
		installButton.Visible = !installed || update || incomplete;
		progress.Visible = busy;
	}

	private async System.Threading.Tasks.Task SyncContentFromSettings()
	{
		if(busy||GameRunning())return;SetBusy(true,"Проверяю дополнения Steam…");
		try{await SyncInstalledContent();Log("Дополнения проверены.");}
		catch(Exception ex){Log(ex.Message);MessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error);}
		finally{SetBusy(false,null);}
	}
	private async Task SyncInstalledContent(bool useInstalledOnMismatch = false)
	{
		string source=state.gameDirectory;
		if(string.IsNullOrEmpty(source)||!File.Exists(Path.Combine(source,"GameAssembly.dll")))source=FindSteamGame();
		if(source==null){Log("Steam-оригинал не найден: DLC не синхронизированы. Укажите путь в «Установка и DLC».");return;}
		state.gameDirectory=source;SaveState();
		if (useInstalledOnMismatch)
		{
			string installedAssembly = Path.Combine(state.installDirectory, "game", "GameAssembly.dll");
			bool compatible = await Task.Run(() =>
			{
				using var sha = SHA256.Create();
				using var original = File.OpenRead(Path.Combine(source, "GameAssembly.dll"));
				using var installed = File.OpenRead(installedAssembly);
				return sha.ComputeHash(original).SequenceEqual(sha.ComputeHash(installed));
			});
			if (!compatible)
			{
				Log("Версии Steam и кооперативной копии различаются. Запускаю установленную копию с её дополнениями.");
				return;
			}
		}
		string script=Path.Combine(state.installDirectory,"Sync-GameContent.ps1");
		if(!File.Exists(script))script=Path.Combine(root,"friend-kit","Sync-GameContent.ps1");
		if(File.Exists(script))await RunScript(script,"-GameDirectory \""+source+"\" -InstallDirectory \""+state.installDirectory+"\"");
	}
}

internal sealed class ShelterPanel : Panel
{
	private static readonly Image Art = LoadArtwork();

	internal ShelterPanel() { DoubleBuffered = true; }

	private static Image LoadArtwork()
	{
		using var stream = typeof(ShelterPanel).Assembly.GetManifestResourceStream("CoopLauncher.Shelter.jpg");
		if (stream == null) return null;
		using var image = Image.FromStream(stream);
		return new Bitmap(image);
	}

	protected override void OnPaintBackground(PaintEventArgs e)
	{
		var bounds = ClientRectangle;
		if (bounds.Width < 1 || bounds.Height < 1) return;
		if (Art != null)
		{
			float scale = Math.Max((float)bounds.Width / Art.Width, (float)bounds.Height / Art.Height);
			float width = Art.Width * scale, height = Art.Height * scale;
			e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
			e.Graphics.DrawImage(Art, bounds.Width - width, (bounds.Height - height) / 2, width, height);
		}
		else e.Graphics.Clear(Color.FromArgb(59, 43, 40));
		using var shade = new LinearGradientBrush(bounds, Color.FromArgb(228, 22, 19, 22), Color.FromArgb(8, 22, 19, 22), LinearGradientMode.Horizontal);
		e.Graphics.FillRectangle(shade, bounds);
	}
}
