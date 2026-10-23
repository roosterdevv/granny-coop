using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace GrannyCoop.Setup
{
    /// <summary>
    /// The form the player sees: one folder, one role, one button. The work runs on a
    /// background thread; every log line comes back to the UI through BeginInvoke.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private readonly Options _opt;
        private readonly TextBox _dir = new TextBox();
        private readonly RadioButton _off = new RadioButton { Text = "Не включать / Off", AutoSize = true };
        private readonly RadioButton _host = new RadioButton { Text = "Хост / Host", AutoSize = true };
        private readonly RadioButton _join = new RadioButton { Text = "Гость / Join", AutoSize = true };
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _address = new TextBox();
        private readonly NumericUpDown _port = new NumericUpDown();
        private readonly Button _install = new Button { Text = "Установить" };
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();
        private readonly RichTextBox _log = new RichTextBox();
        private readonly string _logPath = Log.NewPath();
        private bool _busy;

        public SetupForm(Options opt)
        {
            _opt = opt;
            Text = "Granny Co-op — установщик " + Installer.Version;
            ClientSize = new Size(660, 620);
            MinimumSize = new Size(620, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;

            BuildLayout();

            _host.Checked = true;
            _name.Text = "Player";
            _address.Text = "127.0.0.1";
            _port.Minimum = 1;
            _port.Maximum = 65535;
            _port.Value = 47777;
            _off.CheckedChanged += (s, e) => UpdateRoleFields();
            _host.CheckedChanged += (s, e) => UpdateRoleFields();
            _join.CheckedChanged += (s, e) => UpdateRoleFields();

            string detected = SteamLocator.FindGranny();
            if (detected != null)
            {
                _dir.Text = detected;
                AppendLog("Игра найдена автоматически: " + detected);
            }
            else if (!string.IsNullOrEmpty(opt.InstallDir))
            {
                _dir.Text = opt.InstallDir;
            }
            else
            {
                AppendLog("Steam-папка не найдена автоматически — нажмите «Обзор…».");
            }

            AppendLog("Установщик кладёт MelonLoader " + Installer.LoaderVersion + ", .NET 6 (если нет) и сам мод.");
            AppendLog("Папку игры можно поменять. Игру надо закрыть перед установкой.");
        }

        private void BuildLayout()
        {
            var lblDir = new Label { Text = "Папка игры (где лежит Granny.exe):", AutoSize = true, Location = new Point(12, 12) };

            _dir.Location = new Point(12, 32);
            _dir.Width = 400;
            _dir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var browse = new Button { Text = "Обзор…", Location = new Point(420, 30), Size = new Size(100, 26) };
            browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browse.Click += (s, e) => Browse();

            var find = new Button { Text = "Найти игру", Location = new Point(526, 30), Size = new Size(122, 26) };
            find.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            find.Click += (s, e) => AutoFind();

            var roles = new GroupBox { Text = "Роль / Role", Location = new Point(12, 66), Size = new Size(636, 52) };
            roles.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _off.Location = new Point(14, 21);
            _host.Location = new Point(170, 21);
            _join.Location = new Point(300, 21);
            roles.Controls.AddRange(new Control[] { _off, _host, _join });

            var lblName = new Label { Text = "Имя / Name:", AutoSize = true, Location = new Point(12, 132) };
            _name.Location = new Point(88, 129);
            _name.Width = 120;
            _name.MaxLength = 24;

            var lblAddr = new Label { Text = "Адрес хоста / Host:", AutoSize = true, Location = new Point(222, 132) };
            _address.Location = new Point(340, 129);
            _address.Width = 130;

            var lblPort = new Label { Text = "Порт / Port:", AutoSize = true, Location = new Point(486, 132) };
            _port.Location = new Point(566, 129);
            _port.Width = 82;

            _install.Location = new Point(12, 166);
            _install.Size = new Size(200, 34);
            _install.Click += (s, e) => StartInstall();

            _bar.Location = new Point(12, 210);
            _bar.Size = new Size(636, 18);
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _status.Location = new Point(12, 234);
            _status.Size = new Size(636, 34);
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _status.Text = "Готов к установке.";

            _log.Location = new Point(12, 274);
            _log.Size = new Size(636, 334);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _log.ReadOnly = true;
            _log.BackColor = Color.White;
            _log.Font = new Font("Consolas", 8.5f);
            _log.WordWrap = false;

            Controls.AddRange(new Control[]
            {
                lblDir, _dir, browse, find, roles,
                lblName, _name, lblAddr, _address, lblPort, _port,
                _install, _bar, _status, _log
            });
        }

        private void UpdateRoleFields()
        {
            _address.Enabled = _join.Checked;
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку, где лежит Granny.exe";
                dlg.ShowNewFolderButton = false;
                if (Directory.Exists(_dir.Text)) dlg.SelectedPath = _dir.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) _dir.Text = dlg.SelectedPath;
            }
        }

        private void AutoFind()
        {
            string found = SteamLocator.FindGranny();
            if (found != null)
            {
                _dir.Text = found;
                AppendLog("Найдено: " + found);
            }
            else
            {
                MessageBox.Show(this,
                    "Не удалось найти установленную Granny через Steam.\n" +
                    "Нажмите «Обзор…» и выберите папку вручную.",
                    "Granny Co-op", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void StartInstall()
        {
            if (_busy) return;

            string dir = _dir.Text.Trim();
            if (dir.Length == 0)
            {
                MessageBox.Show(this, "Укажите папку игры.", "Granny Co-op",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(Path.Combine(dir, "Granny.exe")))
            {
                MessageBox.Show(this, "В этой папке нет Granny.exe.", "Granny Co-op",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var o = new Options
            {
                InstallDir = dir,
                Role = _off.Checked ? "off" : _join.Checked ? "join" : "host",
                PlayerName = _name.Text,
                Address = _address.Text,
                Port = (int)_port.Value,
                Force = _opt.Force
            };

            _busy = true;
            _install.Enabled = false;
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;
            _status.Text = "Установка…";
            AppendLog(string.Empty);
            AppendLog("=== установка в " + dir + " ===");

            int result = -1;
            var worker = new Thread(() =>
            {
                try
                {
                    result = Installer.Run(o, line =>
                    {
                        Log.Write(_logPath, line);
                        try { BeginInvoke(new Action(() => AppendLog(line))); } catch { }
                    },
                    percent => { try { BeginInvoke(new Action(() => StepUI(percent))); } catch { } });
                }
                catch (Exception e)
                {
                    Log.Write(_logPath, "ERROR " + e);
                    try { BeginInvoke(new Action(() => AppendLog("ОШИБКА: " + e.Message))); } catch { }
                    result = 5;
                }
                finally
                {
                    try { BeginInvoke(new Action(() => Finish(result))); } catch { }
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void StepUI(int percent)
        {
            if (percent >= 100) return;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = Math.Max(0, Math.Min(100, percent));
            _status.Text = "Установка… " + percent + "%";
        }

        private void Finish(int result)
        {
            _busy = false;
            _install.Enabled = true;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 100;
            UpdateRoleFields();

            string message;
            switch (result)
            {
                case 0:
                    message = "Готово. Запускайте игру через Steam.\n\n" +
                              (_host.Checked ? "Вы хост: друзья подключаются на ваш IP, UDP 47777." :
                               _join.Checked ? "Вы гость: адрес " + _address.Text + ":" + _port.Value + "." :
                               "Роль не включена — выберите её позже в UserData/MelonPreferences.cfg.");
                    _status.Text = "Установлено. Запускайте игру через Steam.";
                    break;
                case 6:
                    message = "Файлы установлены, но .NET 6 runtime не встал.\n" +
                              "Мод не загрузится без него. Установщик оставил файл в %TEMP%\\GrannyCoop-Setup —\n" +
                              "запустите его вручную или поставьте .NET 6 Desktop Runtime с сайта Microsoft.";
                    _status.Text = "Нужен .NET 6 runtime (см. лог).";
                    break;
                case 3:
                    message = "Эта версия Granny не та, на которой мод проверен.\n" +
                              "Нужна Steam-версия buildid 21006637 (v1.3.4).";
                    _status.Text = "Неизвестная версия игры.";
                    break;
                case 4:
                    message = "Granny запущена. Закройте игру и нажмите «Установить» снова.";
                    _status.Text = "Игра запущена.";
                    break;
                case 2:
                    message = "Папка не похожа на установленную Granny.";
                    _status.Text = "Неверная папка.";
                    break;
                default:
                    message = "Не получилось (код " + result + "). Подробности в логе.";
                    _status.Text = "Ошибка.";
                    break;
            }

            message += "\n\nЛог: " + _logPath;
            MessageBox.Show(this, message, "Granny Co-op",
                MessageBoxButtons.OK,
                result == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void AppendLog(string line)
        {
            _log.AppendText(line + "\r\n");
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }
}
