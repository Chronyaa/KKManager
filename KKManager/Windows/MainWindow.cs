﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KKManager.Functions;
using KKManager.Properties;
using KKManager.SB3UGS;
using KKManager.Updater;
using KKManager.Updater.Sources;
using KKManager.Updater.Windows;
using KKManager.Util;
using KKManager.Windows.Content;
using KKManager.Windows.ToolWindows;
using KKManager.Windows.ToolWindows.Properties;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using WeifenLuo.WinFormsUI.Docking;

namespace KKManager.Windows
{
    public sealed partial class MainWindow : Form
    {
        private UpdateSourceBase[] _updateSources;
        public UpdateSourceBase[] GetUpdateSources() => _updateSources ?? (_updateSources = UpdateSourceManager.FindUpdateSources(Program.ProgramLocation));

        public MainWindow()
        {
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);

            Program.MainSynchronizationContext = SynchronizationContext.Current;

            Instance = this;

            InitializeComponent();

            InstallDirectoryHelper.KoikatuDirectory = GetKoikatuDirectory();

            SetupTabs();

            Task.Run((Action)PopulateStartMenu);

#if DEBUG
            var version = Assembly.GetExecutingAssembly().GetName().Version;
#else
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
#endif
            Text = $"KK Manager {version} (Fancy updates edition) - {InstallDirectoryHelper.KoikatuDirectory.FullName}";

            Settings.Default.Binder.BindControl(checkForUpdatesOnStartupToolStripMenuItem, settings => settings.AutoUpdateSearch, this);
            Settings.Default.Binder.SendUpdates(this);
        }

        private static DirectoryInfo GetKoikatuDirectory()
        {
            var path = Settings.Default.GamePath;
            if (!InstallDirectoryHelper.IsValidGamePath(path))
            {
                try
                {
                    path = Registry.CurrentUser.OpenSubKey(@"Software\Illusion\Koikatu\koikatu")
                        ?.GetValue("INSTALLDIR") as string;
                }
                catch (SystemException) { }

                if (!InstallDirectoryHelper.IsValidGamePath(path))
                {
                    MessageBox.Show(
                        "Koikatu is either not registered properly or its install directory is missing or inaccessible.\n\nYou will have to select game directory manually.",
                        "Failed to find Koikatu install directory", MessageBoxButtons.OK, MessageBoxIcon.Error);

                    path = ShowInstallDirectoryDialog(path);
                }

                if (string.IsNullOrEmpty(path) || !InstallDirectoryHelper.IsValidGamePath(path))
                {
                    MessageBox.Show(
                        "Koikatu is either not registered properly or its install directory is missing or inaccessible.",
                        "Failed to get Koikatu install directory", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(1);
                }

                Settings.Default.GamePath = path;
            }

            CheckInstallPathPermissions(path);

            return new DirectoryInfo(path);
        }

        private static string ShowInstallDirectoryDialog(string currentPath)
        {
            using (var fb = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                InitialDirectory = currentPath,
                AllowNonFileSystemItems = false,
                AddToMostRecentlyUsedList = false,
                EnsurePathExists = true,
                EnsureFileExists = true,
                Multiselect = false,
                Title = "Select the install directory of the game."
            })
            {
                retryFolderSelect:
                if (fb.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var path = fb.FileName;
                    if (!InstallDirectoryHelper.IsValidGamePath(path))
                    {
                        if (MessageBox.Show(
                                "The selected directory doesn't seem to contain the game. Make sure the directory is correct and try again.",
                                "Select install directory", MessageBoxButtons.OKCancel, MessageBoxIcon.Error) == DialogResult.OK)
                            goto retryFolderSelect;
                    }
                    return path;
                }

                return null;
            }
        }

        private static void CheckInstallPathPermissions(string path)
        {
            if (!PathTools.DirectoryHasWritePermission(path) ||
                            !PathTools.DirectoryHasWritePermission(Path.Combine(path, "mods")) ||
                            !PathTools.DirectoryHasWritePermission(Path.Combine(path, "userdata")))
            {
                if (MessageBox.Show("KK Manager doesn't have write permissions to the game directory! This can cause issues for both KK Manager and the game itself.\n\nDo you want KK Manager to fix permissions of the entire Koikatu folder?",
                        "No write access to game directory", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    ProcessTools.FixPermissions(path)?.WaitForExit();
            }
        }

        private void PopulateStartMenu()
        {
            var toAdd = new List<ToolStripItem>();
            var pluginPath = InstallDirectoryHelper.GetPluginPath();
            var allExes = InstallDirectoryHelper.KoikatuDirectory.GetFiles("*.exe", SearchOption.AllDirectories);
            var filteredExes = allExes.Where(x => !x.Name.Equals("bepinex.patcher.exe", StringComparison.OrdinalIgnoreCase) && !x.FullName.StartsWith(pluginPath, StringComparison.OrdinalIgnoreCase));
            foreach (var file in filteredExes.OrderBy(x => x.Name))
            {
                var item = new ToolStripMenuItem(file.Name);
                item.AutoToolTip = false;
                item.ToolTipText = file.FullName;
                item.Click += (o, args) => { ProcessTools.SafeStartProcess(file.FullName); };
                toAdd.Add(item);
            }
            this.SafeInvoke(() => startTheGameToolStripMenuItem.DropDownItems.AddRange(toAdd.ToArray()));
        }

        public static MainWindow Instance { get; private set; }

        public static void SetStatusText(string text)
        {
            Instance?.SafeInvoke(() => Instance.toolStripStatusLabelStatus.Text = text);
        }

        public PropertyViewerBase DisplayInPropertyViewer(object obj, DockContent source, bool forceOpen = false)
        {
            var viewer = GetOrCreateWindow<PropertiesToolWindow>(forceOpen);
            return viewer?.ShowProperties(obj, source);
        }

        /// <summary>
        /// Get already existing dockable window or open a new instance of it if none are present.
        /// </summary>
        /// <typeparam name="T">Type of the window to open</typeparam>
        /// <param name="createNew">Create new instance if none are present?</param>
        public T GetOrCreateWindow<T>(bool createNew = true) where T : DockContent, new()
        {
            var w = GetWindows<T>().FirstOrDefault();

            if (w == null && createNew)
            {
                w = new T();
                w.Show(dockPanel);
            }

            return w;
        }

        public IEnumerable<T> GetWindows<T>() where T : DockContent, new()
        {
            return dockPanel.Contents.OfType<T>().Concat(dockPanel.FloatWindows.OfType<T>());
        }

        public CardWindow OpenOrGetCardWindow(DirectoryInfo targetDir)
        {
            return OpenOrGetCardWindow(targetDir.FullName);
        }

        private CardWindow OpenOrGetCardWindow(string targetDir)
        {
            var existing = GetWindows<CardWindow>()
                .FirstOrDefault(x => string.Equals(
                    targetDir, x.CurrentDirectory?.FullName, StringComparison.InvariantCultureIgnoreCase));

            if (existing != null)
            {
                existing.Focus();
                return existing;
            }

            var cardWindow = new CardWindow();
            cardWindow.Show(dockPanel, DockState.Document);
            cardWindow.TryOpenCardDirectory(targetDir);
            return cardWindow;
        }

        private void SetupTabs()
        {
            // Try to load saved state first, if that fails load defaults
            try
            {
                if (!string.IsNullOrWhiteSpace(Settings.Default.DockState))
                {
                    using (var s = new MemoryStream(Encoding.Unicode.GetBytes(Settings.Default.DockState)))
                    {
                        dockPanel.LoadFromXml(s, DeserializeTab);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to read opened tabs from config: " + ex.ToStringDemystified());
                foreach (var content in dockPanel.Contents) content.DockHandler.Close();
                dockPanel.ResumeLayout(true, true);
            }

            OpenOrGetCardWindow(InstallDirectoryHelper.GetMaleCardDir());
            OpenOrGetCardWindow(InstallDirectoryHelper.GetFemaleCardDir());

            GetOrCreateWindow<SideloaderModsWindow>();
            GetOrCreateWindow<PluginsWindow>();

            dockPanel.DockRightPortion = 400;
            var propertiesToolWindow = GetOrCreateWindow<PropertiesToolWindow>();
            propertiesToolWindow.DockState = DockState.DockRight;

            var logWindow = GetOrCreateWindow<LogViewer>();
            logWindow.DockState = DockState.DockBottomAutoHide;
        }

        private static IDockContent DeserializeTab(string persistString)
        {
            var cw = CardWindow.TryLoadFromPersistString(persistString);
            if (cw != null) return cw;

            var t = Type.GetType(persistString, false, true);
            if (t == null || !typeof(IDockContent).IsAssignableFrom(t))
                throw new InvalidDataException(persistString + " points to an invalid type");

            return (IDockContent)Activator.CreateInstance(t);
        }

        private void MainWindow_FormClosed(object sender, FormClosedEventArgs e)
        {
            _checkForUpdatesCancel.Cancel();

            using (var s = new MemoryStream())
            {
                dockPanel.SaveAsXml(s, Encoding.Unicode);
                Settings.Default.DockState = Encoding.Unicode.GetString(s.ToArray());
            }
        }

        private void openFemaleCardFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenOrGetCardWindow(InstallDirectoryHelper.GetFemaleCardDir());
        }

        private void openMaleCardFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenOrGetCardWindow(InstallDirectoryHelper.GetMaleCardDir());
        }

        private void openPluginBrowserToolStripMenuItem_Click(object sender, EventArgs e)
        {
            GetOrCreateWindow<PluginsWindow>().Show(dockPanel, DockState.Document);
        }

        private void openPropertiesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            GetOrCreateWindow<PropertiesToolWindow>().Show(dockPanel, DockState.DockRight);
        }

        private void otherToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var dir = CardWindow.ShowCardFolderBrowseDialog(this);
            if (dir != null)
            {
                var w = new CardWindow();
                w.Show(dockPanel, DockState.Document);
                w.TryOpenCardDirectory(dir);
            }
        }

        private void sideloaderModsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            GetOrCreateWindow<SideloaderModsWindow>().Show(dockPanel, DockState.Document);
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void readmeAndSourceCodeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://github.com/bbepis/KKManager");
        }

        private void installANewModToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog
            {
                Multiselect = false,
                CheckFileExists = true,
                DereferenceLinks = true,
                ValidateNames = true,
                AutoUpgradeEnabled = true,
                Title = "Choose a .dll file or an archive with the mod to install",
                Filter = "Supported mod files(*.dll;*.zipmod;*.zip)|*.dll;*.zipmod;*.zip"
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        ModInstaller.InstallFromUnknownFile(dialog.FileName);
                        RefreshContents(true, true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                        MessageBox.Show("Failed to install the selected mod.\n\n" + ex.Message, "Failed to install mod", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void RefreshContents(bool plugins, bool sideloader)
        {
            foreach (var window in GetWindows<DockContent>())
            {
                if (window is PluginsWindow pw)
                {
                    if (plugins)
                        pw.ReloadList();
                }
                else if (window is SideloaderModsWindow sm)
                {
                    if (sideloader)
                        sm.ReloadList();
                }
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var ab = new AboutBox())
                ab.ShowDialog(this);
        }

        private void installDirectoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ProcessTools.SafeStartProcess(InstallDirectoryHelper.KoikatuDirectory.FullName);
        }

        private void screenshotsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ProcessTools.SafeStartProcess(Path.Combine(InstallDirectoryHelper.KoikatuDirectory.FullName, "UserData\\cap"));
        }

        private void charactersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ProcessTools.SafeStartProcess(Path.Combine(InstallDirectoryHelper.KoikatuDirectory.FullName, "UserData\\chara"));
        }

        private void scenesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ProcessTools.SafeStartProcess(Path.Combine(InstallDirectoryHelper.KoikatuDirectory.FullName, "UserData\\Studio\\scene"));
        }

        private void kKManagerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ProcessTools.SafeStartProcess(Program.ProgramLocation);
        }

        private void updateSideloaderModpackToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ShowModUpdateDialog();
        }

        private void ShowModUpdateDialog()
        {
            _checkForUpdatesCancel.Cancel();

            var sideWindows = GetWindows<DockContent>().OfType<SideloaderModsWindow>().ToList();
            foreach (var window in sideWindows)
                window.CancelListReload();

            try
            {
                var updateSources = GetUpdateSources();
                if (!updateSources.Any()) throw new IOException("No update sources are available");
                ModUpdateProgressDialog.StartUpdateDialog(this, updateSources);
            }
            catch (Exception ex)
            {
                var errorMsg = "Failed to start update - " + ex.ToStringDemystified();
                Console.WriteLine(errorMsg);
                MessageBox.Show(errorMsg, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            foreach (var window in sideWindows)
                window.ReloadList();

            updateSideloaderModpackToolStripMenuItem.BackColor = DefaultBackColor;
            updateSideloaderModpackToolStripMenuItem.ForeColor = DefaultForeColor;
        }

        private readonly CancellationTokenSource _checkForUpdatesCancel = new CancellationTokenSource();

        private async void MainWindow_Shown(object sender, EventArgs e)
        {
            if (!Settings.Default.AutoUpdateSearch) return;
            // Check For Updates
            // todo make more efficient?
            try
            {
                var updateSources = GetUpdateSources();
                if (!updateSources.Any()) return;
                var results = await UpdateSourceManager.GetUpdates(_checkForUpdatesCancel.Token, updateSources);
                var updates = results.Count(item => !item.UpToDate);

                _checkForUpdatesCancel.Token.ThrowIfCancellationRequested();

                if (updates > 0)
                {
                    SetStatusText($"Found {updates} mod updates!");
                    updateSideloaderModpackToolStripMenuItem.BackColor = Color.Lime;
                }
                else
                {
                    SetStatusText("No mod updates were found");
                    updateSideloaderModpackToolStripMenuItem.ForeColor = Color.Gray;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine(ex); }
        }

        private void fixFileAndFolderPermissionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ProcessTools.FixPermissions(InstallDirectoryHelper.KoikatuDirectory.FullName)?.WaitForExit();
        }

        private void changeGameInstallDirectoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var folder = ShowInstallDirectoryDialog(Settings.Default.GamePath);
            if (folder == null) return;

            Settings.Default.GamePath = folder;
            Settings.Default.Save();
            MessageBox.Show("Install directory has been changed successfully. KKManager has to be restarted for the changes to take effect.", "Change install directory", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void generateContentsOfUpdatexmlToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var w = new UpdateInfoEditorWindow())
            {
                w.ShowDialog(this);
            }
        }

        private void compressGameFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                    "This will compress all of your game files in order to greatly reduce their size on disk and potentially slightly improve the loading times.\n\nThis process can take a very long time depending on your CPU and drive speeds. If some or all game files are already compressed then the size reduction might be low.",
                    "Compress files", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                var directories = InstallDirectoryHelper.KoikatuDirectory.GetDirectories("*", SearchOption.TopDirectoryOnly)
                    .Where(directory => directory.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) ||
                                        directory.Name.Equals("abdata", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var files = directories.SelectMany(dir => dir.GetFiles("*", SearchOption.AllDirectories)).Where(SB3UGS_Utils.FileIsAssetBundle).ToList();

                CompressFiles(files, false);
            }
        }

        private void CompressFiles(IReadOnlyList<FileInfo> files, bool randomizeCab)
        {
            if (!SB3UGS_Initializer.CheckIsAvailable())
            {
                MessageBox.Show(
                    "SB3UGS has not been found in KK Manager directory or it failed to be loaded. Reinstall KK Manager and try again.",
                    "Compress files", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            LoadingDialog.ShowDialog(this, "Compressing asset bundle files", dialogInterface =>
            {
                dialogInterface.SetMaximum(files.Count);

                var excs = new ConcurrentBag<Exception>();
                long totalSizeSaved = 0;
                var count = 0;

                Parallel.ForEach(files, file =>
                {
                    dialogInterface.SetProgress(count++, "Compressing " + file.Name);

                    try
                    {
                        var origSize = file.Length;
                        SB3UGS_Utils.CompressBundle(file.FullName, randomizeCab);
                        file.Refresh();
                        totalSizeSaved += origSize - file.Length;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to compress file {file.FullName} - {ex.Message}");
                        excs.Add(ex);
                    }
                });

                if (excs.Any())
                    MessageBox.Show($"Successfully compressed {files.Count - excs.Count} out of {files.Count} files, see log for details. Saved {FileSize.FromBytes(totalSizeSaved).ToString()}.", "Compress files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    MessageBox.Show($"Successfully compressed {files.Count} files. Saved {FileSize.FromBytes(totalSizeSaved).ToString()}.", "Compress files", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void compressBundlesAndRandomizeCABsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var d = new CommonOpenFileDialog("Compress bundles and randomize CABs")
            {
                IsFolderPicker = true,
                EnsurePathExists = true,
                EnsureFileExists = true
            })
            {
                if (d.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var files = new DirectoryInfo(d.FileName).GetFiles("*", SearchOption.AllDirectories).Where(SB3UGS_Utils.FileIsAssetBundle).ToList();

                    var randomize = MessageBox.Show("Do you want to randomize CABs of the compressed files? Click No to keep the original CAB strings.",
                                        "Compress bundles in a folder...", MessageBoxButtons.YesNo, MessageBoxIcon.Question) ==
                                    DialogResult.Yes;

                    CompressFiles(files, randomize);
                }
            }
        }

        private void openLogViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            GetOrCreateWindow<LogViewer>().Show(dockPanel, DockState.DockBottom);
        }
    }
}
