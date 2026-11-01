#nullable disable
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SCL.Core;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text;

namespace SCL
{
    public partial class MainWindow : Window
    {
        private ComboBox DownloadVersionCombo => FindName("CmbDownloadVersion") as ComboBox;
        private Border DownloadQuickBarElement => FindName("DownloadQuickBar") as Border;
        private ComboBox LoaderTypeCombo => FindName("CmbLoaderType") as ComboBox;
        private Button DownloadVanillaButton => FindName("BtnDownloadVanilla") as Button;

        public object TxtSelectedVersionHint { get; private set; }

        private readonly string gamePath;
        private readonly string settingsDirectory;
        private readonly string wallpaperSettingPath;
        private readonly string overlaySettingPath;
        private bool pageInitialized;
        private string selectedLaunchVersion = string.Empty;
        private static readonly HttpClient Http = new HttpClient();
        private readonly List<(string Id, string Type)> officialVersions = new List<(string Id, string Type)>();

        // 正版登录凭据记录
        private MsaAuthClient.MinecraftProfile _currentProfile = null;
        private bool _isMsLoggingIn = false;

        public MainWindow()
        {
            InitializeComponent();
            Closed += Window_Closed;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            gamePath = Path.Combine(baseDir, ".minecraft");

            settingsDirectory = Path.Combine(baseDir, "SCL_Settings");
            wallpaperSettingPath = Path.Combine(settingsDirectory, "wallpaper.txt");
            overlaySettingPath = Path.Combine(settingsDirectory, "wallpaper-overlay.txt");

            Directory.CreateDirectory(gamePath);
            Directory.CreateDirectory(Path.Combine(gamePath, "versions"));
            Directory.CreateDirectory(Path.Combine(gamePath, "libraries"));
            Directory.CreateDirectory(Path.Combine(gamePath, "assets"));
            Directory.CreateDirectory(settingsDirectory);

            TxtSettingsRoot.Text = gamePath;
            TxtGamePath.Text = "游戏目录：" + gamePath;

            LoadWallpaperSettings();
            AutoDetectJava();
            LoadLocalVersions();
            ShowPage("Download");

            Log("================================");
            Log("SCL Launcher 启动成功");
            Log("游戏根目录：" + gamePath);
            Log("================================");

            pageInitialized = true;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadWallpaperSettings();
            await LoadVersionManifestAsync();
        }

        // ================= 动感核心动画 =================
        private void PlayPageAnimation(Grid targetPage)
        {
            if (targetPage == null) return;

            targetPage.Visibility = Visibility.Visible;
            targetPage.Opacity = 0;

            var group = new TransformGroup();
            var scale = new ScaleTransform(0.98, 0.98);
            var translate = new TranslateTransform(0, 40);
            group.Children.Add(scale);
            group.Children.Add(translate);

            targetPage.RenderTransform = group;
            targetPage.RenderTransformOrigin = new Point(0.5, 0.5);

            var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            targetPage.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            var translateAnim = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            };
            translate.BeginAnimation(TranslateTransform.YProperty, translateAnim);

            var scaleXAnim = new DoubleAnimation(0.98, 1.0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var scaleYAnim = new DoubleAnimation(0.98, 1.0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        }

        // ================= 微软正版登录核心逻辑 (带自动复制) =================
        private async void BtnMsLogin_Click(object sender, RoutedEventArgs e)
        {
            if (_isMsLoggingIn) return;
            _isMsLoggingIn = true;
            BtnMsLogin.IsEnabled = false;
            TxtLoginStatus.Text = "正在请求登录...";
            TxtLoginSub.Text = "请稍候";

            try
            {
                var loginHelper = new MsaAuthClient();
                var profile = await loginHelper.LoginAsync((msg) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        // 提取验证码 (通常最后一行)
                        string codeToCopy = "";
                        var lines = msg.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0) codeToCopy = lines[lines.Length - 1].Trim();

                        // 自动复制到剪贴板
                        if (!string.IsNullOrEmpty(codeToCopy))
                        {
                            try { Clipboard.SetText(codeToCopy); } catch { }
                        }

                        MessageBox.Show($"验证码已自动复制到剪贴板：\n\n{codeToCopy}\n\n点击“确定”后将自动打开浏览器。\n进入网页后直接按 Ctrl+V 粘贴即可！",
                                        "微软正版登录", MessageBoxButton.OK, MessageBoxImage.Information);

                        TxtLoginStatus.Text = "等待网页验证...";
                    });
                });

                _currentProfile = profile;
                TxtLoginStatus.Text = $"已登录：{profile.Name}";
                TxtLoginSub.Text = "正版账号已授权";

                TxtPlayerNameQuick.Text = profile.Name;
                TxtPlayerNameQuick.IsReadOnly = true;
                TxtPlayerNameQuick.Foreground = Brushes.Gray;
                TxtPlayerName.Text = profile.Name;

                BtnMsLogin.Content = "切换账号";
                Log($"正版登录成功：{profile.Name} (UUID: {profile.Uuid})");
            }
            catch (Exception ex)
            {
                MessageBox.Show("登录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtLoginStatus.Text = "登录失败";
                TxtLoginSub.Text = "请重试";
                Log("正版登录失败：" + ex.Message);
            }
            finally
            {
                _isMsLoggingIn = false;
                BtnMsLogin.IsEnabled = true;
            }
        }

        // ================= 启动逻辑 =================
        private void BtnQuickLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (ListLatestVersions.SelectedItem == null || GetVersionItemId(ListLatestVersions.SelectedItem) == "暂无已安装版本")
            {
                MessageBox.Show("请先在左侧选择一个要启动的版本！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtPlayerName.Text = TxtPlayerNameQuick.Text;

            string selectedVersion = GetVersionItemId(ListLatestVersions.SelectedItem);
            selectedLaunchVersion = selectedVersion;
            if (TxtLaunchVersionTitle != null) TxtLaunchVersionTitle.Text = selectedVersion;

            ShowPage("LaunchDetails");
            BtnStartGame_Click(sender, e);
        }

        private async void BtnStartGame_Click(object sender, RoutedEventArgs e)
        {
            string javaPath = TxtJavaPath.Text == null ? string.Empty : TxtJavaPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath)) { MessageBox.Show("Java 路径无效，请先选择 java.exe。", "SCL", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            string version = GetSelectedLaunchVersion();
            if (string.IsNullOrWhiteSpace(version)) { MessageBox.Show("没有可启动的版本，请先下载 SCL 游戏。", "SCL", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            string username = string.IsNullOrWhiteSpace(TxtPlayerName.Text) ? "SCL_Player" : TxtPlayerName.Text.Trim();

            BtnStartGame.IsEnabled = false;
            SetLaunchProgress(0, "正在准备启动…", "已开始启动流程");
            Log("--------------------------------");
            Log("准备启动 SCL：" + version);
            Log("Java：" + javaPath);
            try
            {
                SetLaunchProgress(35, "正在启动游戏…", "正在创建 SCL 启动进程");
                await Task.Run(() =>
                {
                    MinecraftLauncher launcher = new MinecraftLauncher(gamePath, javaPath);

                    // 如果你要在启动器中加入正版验证，这里可以传入 _currentProfile
                    // 注意：需要你自行修改 MinecraftLauncher.Launch 方法支持接收 Token 和 UUID
                    launcher.Launch(version, username);
                });
                SetLaunchProgress(100, "启动命令已执行", "Java 启动进程已创建；此状态不代表已进入游戏主菜单。");
                Log("Java 启动进程已创建（此状态不代表已进入游戏主菜单）。");
            }
            catch (Exception ex)
            {
                SetLaunchProgress(0, "启动失败", ex.Message);
                Log("SCL 启动失败：" + ex);
                MessageBox.Show("启动失败：\n" + ex.Message, "SCL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { BtnStartGame.IsEnabled = true; }
        }

        // ================= 下载与路由逻辑 =================
        private async Task LoadVersionManifestAsync()
        {
            try
            {
                using (HttpResponseMessage response = await Http.GetAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync();
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        JsonElement versions;
                        if (!document.RootElement.TryGetProperty("versions", out versions) || versions.ValueKind != JsonValueKind.Array) return;
                        officialVersions.Clear();
                        foreach (JsonElement version in versions.EnumerateArray())
                        {
                            if (!version.TryGetProperty("id", out JsonElement idElement) || !version.TryGetProperty("type", out JsonElement typeElement)) continue;
                            string id = idElement.GetString() ?? string.Empty;
                            string type = typeElement.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(type)) officialVersions.Add((id, type));
                        }

                        string selected = GetSelectedDownloadVersion();
                        if (DownloadVersionCombo != null)
                        {
                            DownloadVersionCombo.Items.Clear();
                            foreach (var item in officialVersions) DownloadVersionCombo.Items.Add(new ComboBoxItem { Content = item.Id });
                            SelectComboVersion(selected);
                        }
                        RefreshOfficialVersions();
                        TxtVersionCount.Text = officialVersions.Count + " 个官方版本";
                    }
                }
            }
            catch (Exception ex) { Log("获取官方版本清单失败：" + ex.Message); }
        }

        private void RefreshOfficialVersions()
        {
            if (ListOfficialVersions == null) return;
            string category = RbSnapshot != null && RbSnapshot.IsChecked == true ? "snapshot" : RbAprilFools != null && RbAprilFools.IsChecked == true ? "april" : "release";
            string selected = GetSelectedDownloadVersion();
            var filtered = officialVersions.FindAll(v => category == "april"
                ? (v.Id.Contains("infinite") || v.Id.Contains("20w14infinite") || v.Id.Contains("15w14a") || v.Id.Contains("3D Shareware") || v.Id.Contains("23w13a_or_b") || v.Id.Contains("24w14potato"))
                : v.Type == category);

            ListOfficialVersions.Items.Clear();
            foreach (var version in filtered)
            {
                ListOfficialVersions.Items.Add(new global::VersionItem { Name = version.Id, VersionId = version.Id, IconPath = GetVersionIconPath(category) });
            }

            TxtOfficialCount.Text = filtered.Count + " 个官方版本";
            if (TxtVersionCount != null) TxtVersionCount.Text = filtered.Count + " 个可浏览版本";
            if (filtered.Count == 0) { TxtOfficialCount.Text = "未找到此类版本"; return; }
            int selectedIndex = filtered.FindIndex(v => v.Id == selected);
            ListOfficialVersions.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            if (DownloadVersionCombo != null && DownloadVersionCombo.Items.Count > 0 && selectedIndex < 0) SelectComboVersion(filtered[0].Id);
        }

        private void SelectComboVersion(string id)
        {
            if (DownloadVersionCombo == null) return;
            for (int i = 0; i < DownloadVersionCombo.Items.Count; i++)
            {
                ComboBoxItem item = DownloadVersionCombo.Items[i] as ComboBoxItem;
                if (item != null && Convert.ToString(item.Content) == id) { DownloadVersionCombo.SelectedIndex = i; return; }
                if (DownloadVersionCombo.Items[i] != null && DownloadVersionCombo.Items[i].ToString() == id) { DownloadVersionCombo.SelectedIndex = i; return; }
            }
            DownloadVersionCombo.Items.Add(new ComboBoxItem { Content = id });
            DownloadVersionCombo.SelectedIndex = DownloadVersionCombo.Items.Count - 1;
        }

        private void OfficialVersions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListOfficialVersions.SelectedItem == null) return;
            string id = GetVersionItemId(ListOfficialVersions.SelectedItem);
            if (string.IsNullOrWhiteSpace(id)) return;
            SelectComboVersion(id);
            if (TxtDownloadStatus != null) TxtDownloadStatus.Text = "已选择官方版本 " + id + "，可以开始安装。";
        }

        private static string GetVersionIconPath(string category)
        {
            if (category == "snapshot") return "Images/Blocks/CommandBlock.png";
            if (category == "april") return "Images/Blocks/GoldBlock.png";
            return "Images/Blocks/Grass.png";
        }

        private static string GetVersionItemId(object value)
        {
            if (value is global::VersionItem versionItem) return versionItem.VersionId ?? string.Empty;
            return value == null ? string.Empty : value.ToString() ?? string.Empty;
        }

        private string detailSelectedVersion = string.Empty;

        private void OpenDownloadDetails_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button)) return;
            detailSelectedVersion = Convert.ToString(button.Tag) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(detailSelectedVersion)) return;
            SelectComboVersion(detailSelectedVersion);
            TxtDetailVersion.Text = "SCL " + detailSelectedVersion;
            DetailLoaderVanilla.IsChecked = true;

            DownloadHeader.Visibility = Visibility.Collapsed;
            if (DownloadQuickBarElement != null) DownloadQuickBarElement.Visibility = Visibility.Collapsed;
            DownloadVersionBrowser.Visibility = Visibility.Collapsed;
            DownloadDetailsPage.Visibility = Visibility.Visible;
            TxtDownloadStatus.Text = "已选择 SCL " + detailSelectedVersion + "，请选择安装类型。";
        }

        private void BackToVersionList_Click(object sender, RoutedEventArgs e)
        {
            DownloadDetailsPage.Visibility = Visibility.Collapsed;
            DownloadHeader.Visibility = Visibility.Visible;
            if (DownloadQuickBarElement != null) DownloadQuickBarElement.Visibility = Visibility.Visible;
            DownloadVersionBrowser.Visibility = Visibility.Visible;
        }

        private void DetailLoader_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton radio)) return;
            string tag = Convert.ToString(radio.Tag) ?? "Vanilla";
            if (LoaderTypeCombo != null) LoaderTypeCombo.SelectedIndex = tag == "Forge" ? 1 : tag == "Fabric" ? 2 : 0;
            if (BtnInstallFromDetails != null) BtnInstallFromDetails.Content = tag == "Vanilla" ? "下载纯净版 SCL" : tag == "Forge" ? "安装 Forge" : "安装 Fabric";
        }

        private void LoaderCard_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton radio)) return;
            string tag = Convert.ToString(radio.Tag) ?? "Vanilla";
            if (LoaderTypeCombo != null) LoaderTypeCombo.SelectedIndex = tag == "Forge" ? 1 : tag == "Fabric" ? 2 : 0;
            if (DownloadVanillaButton != null) DownloadVanillaButton.Content = tag == "Vanilla" ? "下载纯净版" : tag == "Forge" ? "安装 Forge" : "安装 Fabric";
        }

        private async void BtnInstallSelected_Click(object sender, RoutedEventArgs e)
        {
            string version = detailSelectedVersion;
            if (string.IsNullOrWhiteSpace(version)) version = GetSelectedDownloadVersion();

            string loader = DetailLoaderForge.IsChecked == true ? "Forge"
                : DetailLoaderFabric.IsChecked == true ? "Fabric" : "Vanilla";

            if (LoaderTypeCombo != null && LoaderTypeCombo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag != null) loader = Convert.ToString(selectedItem.Tag) ?? loader;
            else if (LoaderTypeCombo != null && LoaderTypeCombo.SelectedIndex == 1) loader = "Forge";
            else if (LoaderTypeCombo != null && LoaderTypeCombo.SelectedIndex == 2) loader = "Fabric";

            if (loader == "Forge") { await InstallForgeAsync(version); return; }
            if (loader == "Fabric") { await InstallFabricAsync(version); return; }

            if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = false;
            if (BtnInstallFromDetails != null) BtnInstallFromDetails.IsEnabled = false;
            try
            {
                DownloadProgress.IsIndeterminate = true;
                DownloadProgress.Value = 0;
                TxtDownloadStatus.Text = "正在下载 SCL " + version + "…（正在获取版本清单、游戏文件和依赖库）";
                await Task.Run(() => new MinecraftDownloader(gamePath).Install(version));
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = 100;
                TxtDownloadStatus.Text = "SCL " + version + " 下载完成。";
                LoadLocalVersions();
            }
            catch (Exception ex)
            {
                DownloadProgress.IsIndeterminate = false;
                TxtDownloadStatus.Text = "下载失败：" + ex.Message;
                MessageBox.Show("下载失败：\n" + ex.Message, "SCL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = true;
                if (BtnInstallFromDetails != null) BtnInstallFromDetails.IsEnabled = true;
            }
        }

        private async Task InstallForgeAsync(string version)
        {
            if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = false;
            if (BtnInstallFromDetails != null) BtnInstallFromDetails.IsEnabled = false;
            try
            {
                DownloadProgress.IsIndeterminate = true;
                DownloadProgress.Value = 0;
                TxtDownloadStatus.Text = "正在安装 Forge…";
                string installedVersion = await new ForgeInstaller(gamePath).InstallAsync(version);
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = 100;
                TxtDownloadStatus.Text = "Forge 安装完成：" + installedVersion;
                LoadLocalVersions();
            }
            catch (Exception ex)
            {
                DownloadProgress.IsIndeterminate = false;
                TxtDownloadStatus.Text = "Forge 安装失败：" + ex.Message;
                MessageBox.Show("Forge 安装失败：\n" + ex.Message, "SCL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = true;
                if (BtnInstallFromDetails != null) BtnInstallFromDetails.IsEnabled = true;
            }
        }

        private async Task InstallFabricAsync(string version)
        {
            if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = false;
            if (BtnInstallFromDetails != null) BtnInstallFromDetails.IsEnabled = false;
            try
            {
                DownloadProgress.IsIndeterminate = true;
                DownloadProgress.Value = 0;
                TxtDownloadStatus.Text = "正在安装 Fabric…";
                Type installerType = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (Type type in assembly.GetTypes())
                            if (type.Name.Equals("FabricInstaller", StringComparison.OrdinalIgnoreCase)) { installerType = type; break; }
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        foreach (Type type in ex.Types)
                            if (type != null && type.Name.Equals("FabricInstaller", StringComparison.OrdinalIgnoreCase)) { installerType = type; break; }
                    }
                    if (installerType != null) break;
                }
                if (installerType == null) throw new InvalidOperationException("没有找到 FabricInstaller 类。");

                object installer = Activator.CreateInstance(installerType, new object[] { gamePath });
                MethodInfo method = installerType.GetMethod("InstallAsync", new[] { typeof(string) }) ?? installerType.GetMethod("Install", new[] { typeof(string) });
                object result = method.Invoke(installer, new object[] { version });
                string installedVersion = version + "-Fabric";
                if (result is Task task)
                {
                    await task;
                    PropertyInfo resultProperty = task.GetType().GetProperty("Result");
                    object resultValue = resultProperty == null ? null : resultProperty.GetValue(task);
                    if (resultValue != null && !string.IsNullOrWhiteSpace(resultValue.ToString())) installedVersion = resultValue.ToString();
                }
                else if (result != null && !string.IsNullOrWhiteSpace(result.ToString())) installedVersion = result.ToString();

                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = 100;
                TxtDownloadStatus.Text = "Fabric 安装完成：" + installedVersion;
                LoadLocalVersions();
            }
            catch (TargetInvocationException ex)
            {
                DownloadProgress.IsIndeterminate = false;
                TxtDownloadStatus.Text = "Fabric 安装失败：" + (ex.InnerException ?? ex).Message;
                MessageBox.Show("Fabric 安装失败：\n" + (ex.InnerException ?? ex).Message, "SCL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                DownloadProgress.IsIndeterminate = false;
                TxtDownloadStatus.Text = "Fabric 安装失败：" + ex.Message;
                MessageBox.Show("Fabric 安装失败：\n" + ex.Message, "SCL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = true;
                if (BtnInstallFromDetails != null) BtnInstallFromDetails.IsEnabled = true;
            }
        }

        private void NavButton_Checked(object sender, RoutedEventArgs e)
        {
            RadioButton button = sender as RadioButton;
            if (button != null) ShowPage(Convert.ToString(button.Tag) ?? string.Empty);
        }

        private void CmbLaunchVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLaunchVersion.SelectedItem != null) selectedLaunchVersion = CmbLaunchVersion.SelectedItem.ToString() ?? string.Empty;
        }

        private void NavButton_ToLaunchList_Click(object sender, RoutedEventArgs e) { ShowPage("LaunchDetails"); }

        private void BtnVersionSettings_Click(object sender, RoutedEventArgs e) { MessageBox.Show("开发中...", "SCL"); }

        private void ShowPage(string pageName)
        {
            if (PageDownload == null || PageLaunch == null || PageLaunchDetails == null || PageSettings == null || PageMultiplayer == null) return;

            PageDownload.Visibility = Visibility.Collapsed;
            PageLaunch.Visibility = Visibility.Collapsed;
            PageLaunchDetails.Visibility = Visibility.Collapsed;
            PageSettings.Visibility = Visibility.Collapsed;
            PageMultiplayer.Visibility = Visibility.Collapsed;

            if (pageName == "Download") PlayPageAnimation(PageDownload);
            else if (pageName == "Launch") PlayPageAnimation(PageLaunch);
            else if (pageName == "LaunchDetails") PlayPageAnimation(PageLaunchDetails);
            else if (pageName == "Settings") PlayPageAnimation(PageSettings);
            else if (pageName == "Multiplayer") PlayPageAnimation(PageMultiplayer);

            if (pageName == "DownloadDetails")
            {
                PlayPageAnimation(PageDownload);
                DownloadDetailsPage.Visibility = Visibility.Visible;
                ListOfficialVersions.Visibility = Visibility.Collapsed;
                DownloadHeader.Visibility = Visibility.Collapsed;
                if (DownloadQuickBarElement != null) DownloadQuickBarElement.Visibility = Visibility.Collapsed;
            }
            else if (pageName == "Download")
            {
                DownloadDetailsPage.Visibility = Visibility.Collapsed;
                ListOfficialVersions.Visibility = Visibility.Visible;
                DownloadHeader.Visibility = Visibility.Visible;
                if (DownloadQuickBarElement != null) DownloadQuickBarElement.Visibility = Visibility.Visible;
            }
        }

        private string GetSelectedDownloadVersion()
        {
            if (DownloadVersionCombo == null)
                return !string.IsNullOrWhiteSpace(detailSelectedVersion) ? detailSelectedVersion : (ListOfficialVersions.SelectedItem != null ? GetVersionItemId(ListOfficialVersions.SelectedItem) : "1.20.1");
            ComboBoxItem item = DownloadVersionCombo.SelectedItem as ComboBoxItem;
            if (item != null && item.Content != null) return item.Content.ToString() ?? string.Empty;
            return DownloadVersionCombo.SelectedItem == null ? "1.20.1" : (DownloadVersionCombo.SelectedItem.ToString() ?? "1.20.1");
        }

        private void DownloadVersionChanged(object sender, SelectionChangedEventArgs e) { }

        private void VersionCategoryChanged(object sender, RoutedEventArgs e) { if (pageInitialized) RefreshOfficialVersions(); }

        private void ThemeChanged(object sender, RoutedEventArgs e)
        {
            if (!pageInitialized) return;
            bool overlay = ChkDarkModeSettings != null && ChkDarkModeSettings.IsChecked == true;
            try { File.WriteAllText(overlaySettingPath, overlay ? "1" : "0"); } catch { }
            ApplyWallpaperOverlay(overlay);
        }

        private void LoadWallpaperSettings()
        {
            try
            {
                if (File.Exists(wallpaperSettingPath))
                {
                    string path = File.ReadAllText(wallpaperSettingPath).Trim();
                    if (File.Exists(path))
                    {
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path, UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze();
                        WallpaperImage.Source = bitmap;
                    }
                }
                bool overlay = !File.Exists(overlaySettingPath) || File.ReadAllText(overlaySettingPath).Trim() != "0";
                if (ChkDarkModeSettings != null) ChkDarkModeSettings.IsChecked = overlay;
                ApplyWallpaperOverlay(overlay);
            }
            catch { }
        }

        private void ApplyWallpaperOverlay(bool enabled)
        {
            Border surface = Content as Border;
            if (surface == null) return;
            Grid root = surface.Child as Grid;
            if (root == null || root.Children.Count < 2) return;
            Border overlay = root.Children[1] as Border;
            if (overlay != null) overlay.Background = new SolidColorBrush(enabled ? Color.FromArgb(196, 12, 14, 13) : Color.FromArgb(70, 9, 16, 11));
        }

        private void ChooseWallpaper_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog { Title = "选择 SCL 壁纸", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(dialog.FileName, UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze();
                WallpaperImage.Source = bitmap;
                File.WriteAllText(wallpaperSettingPath, dialog.FileName);
            }
            catch (Exception ex) { MessageBox.Show("无法加载这张图片：\n" + ex.Message); }
        }

        private void ResetWallpaper_Click(object sender, RoutedEventArgs e)
        {
            WallpaperImage.Source = null;
            try { if (File.Exists(wallpaperSettingPath)) File.Delete(wallpaperSettingPath); } catch { }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                if (e.ClickCount == 2) { ToggleMaximize(); return; }
                try { DragMove(); } catch { }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void Maximize_Click(object sender, RoutedEventArgs e) { ToggleMaximize(); }
        private void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        private async void BtnDownloadVanilla_Click(object sender, RoutedEventArgs e) { await BtnInstallSelectedInternalAsync(); }

        private async Task BtnInstallSelectedInternalAsync()
        {
            string version = GetSelectedDownloadVersion();
            if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = false;
            DownloadProgress.Value = 0; DownloadProgress.IsIndeterminate = true;
            try
            {
                await Task.Run(() => new MinecraftDownloader(gamePath).Install(version));
                DownloadProgress.IsIndeterminate = false; DownloadProgress.Value = 100;
                LoadLocalVersions();
            }
            catch (Exception ex) { DownloadProgress.IsIndeterminate = false; MessageBox.Show("下载失败：\n" + ex.Message); }
            finally { if (DownloadVanillaButton != null) DownloadVanillaButton.IsEnabled = true; }
        }

        private async void BtnInstallForge_Click(object sender, RoutedEventArgs e) { await InstallForgeAsync(GetSelectedDownloadVersion()); }

        private void LoadLocalVersions()
        {
            ListLatestVersions.Items.Clear();
            CmbLaunchVersion.Items.Clear();
            string versionsDirectory = Path.Combine(gamePath, "versions");
            if (!Directory.Exists(versionsDirectory)) return;
            try
            {
                foreach (string directory in Directory.GetDirectories(versionsDirectory))
                {
                    string version = Path.GetFileName(directory);
                    if (string.IsNullOrWhiteSpace(version)) continue;
                    if (!File.Exists(Path.Combine(directory, version + ".json"))) continue;
                    string iconPath = version.IndexOf("fabric", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Images/Blocks/CommandBlock.png"
                        : version.IndexOf("forge", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "Images/Blocks/GoldBlock.png" : "Images/Blocks/Grass.png";
                    ListLatestVersions.Items.Add(new global::VersionItem { Name = version, VersionId = version, IconPath = iconPath });
                    CmbLaunchVersion.Items.Add(version);
                }
                if (CmbLaunchVersion.Items.Count > 0) CmbLaunchVersion.SelectedIndex = 0;
                if (ListLatestVersions.Items.Count == 0) ListLatestVersions.Items.Add("暂无已安装版本");
            }
            catch (Exception ex) { Log("读取本地版本失败：" + ex.Message); }
        }

        private void ListLatestVersions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListLatestVersions.SelectedItem == null) return;
            string selectedVersion = GetVersionItemId(ListLatestVersions.SelectedItem);
            if (string.IsNullOrWhiteSpace(selectedVersion) || selectedVersion == "暂无已安装版本") return;
            for (int i = 0; i < CmbLaunchVersion.Items.Count; i++)
                if (string.Equals(CmbLaunchVersion.Items[i].ToString(), selectedVersion, StringComparison.OrdinalIgnoreCase)) { CmbLaunchVersion.SelectedIndex = i; break; }
        }

        private void VersionSelect_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null || button.Tag == null) return;
            string version = button.Tag.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(version) || version == "暂无已安装版本") return;
            selectedLaunchVersion = version;
            if (TxtLaunchVersionTitle != null) TxtLaunchVersionTitle.Text = version;
            for (int i = 0; i < CmbLaunchVersion.Items.Count; i++)
                if (string.Equals(Convert.ToString(CmbLaunchVersion.Items[i]), version, StringComparison.OrdinalIgnoreCase)) { CmbLaunchVersion.SelectedIndex = i; break; }
            ShowPage("LaunchDetails");
        }

        private void BackToLaunchList_Click(object sender, RoutedEventArgs e) { ShowPage("Launch"); }

        private void AutoDetectJava()
        {
            try
            {
                string path = JavaFinder.FindJava(17);
                if (!string.IsNullOrWhiteSpace(path)) TxtJavaPath.Text = path;
            }
            catch { }
        }

        private void BtnBrowseJava_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog { Title = "选择 Java", Filter = "Java 程序 (java.exe)|java.exe|所有文件|*.*", CheckFileExists = true };
            if (dialog.ShowDialog() == true) TxtJavaPath.Text = dialog.FileName;
        }

        private string GetSelectedLaunchVersion()
        {
            if (!string.IsNullOrWhiteSpace(selectedLaunchVersion)) return selectedLaunchVersion;
            if (CmbLaunchVersion.SelectedItem != null) return CmbLaunchVersion.SelectedItem.ToString() ?? string.Empty;
            if (ListLatestVersions.SelectedItem != null)
            {
                string value = GetVersionItemId(ListLatestVersions.SelectedItem);
                if (value != "暂无已安装版本") return value;
            }
            return string.Empty;
        }

        private void SetLaunchProgress(int value, string status, string detail)
        {
            if (LaunchProgress != null) LaunchProgress.Value = value;
            if (TxtLaunchStatus != null) TxtLaunchStatus.Text = status;
            if (TxtLaunchDetail != null) TxtLaunchDetail.Text = detail;
            Log(status + "（" + value + "%）: " + detail);
        }

        private void Log(string message)
        {
            if (TxtLog == null) return;
            TxtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }

        private readonly TerracottaProvisioningService terracottaProvisioning = new();
        private readonly TerracottaMultiplayerClient terracottaMultiplayer = new();
        private bool terracottaEventsConnected;
        private bool terracottaOperationInProgress;
        private CancellationTokenSource terracottaOperationCts;

        private void EnsureTerracottaEventsConnected()
        {
            if (terracottaEventsConnected) return;
            terracottaMultiplayer.SnapshotChanged += TerracottaMultiplayer_SnapshotChanged;
            terracottaMultiplayer.Stopped += TerracottaMultiplayer_Stopped;
            terracottaEventsConnected = true;
        }

        private async void BtnMultiplayerCreate_Click(object sender, RoutedEventArgs e)
        {
            if (terracottaOperationInProgress) return;
            EnsureTerracottaEventsConnected();
            terracottaOperationCts?.Dispose();
            terracottaOperationCts = new CancellationTokenSource();
            CancellationToken token = terracottaOperationCts.Token;
            SetTerracottaBusy(true);
            TerracottaDownloadProgress.Visibility = Visibility.Visible;
            TerracottaDownloadProgress.Value = 0;
            TxtMultiplayerStatus.Text = "正在检查 / 下载 Terracotta 核心…";

            try
            {
                var progress = new Progress<TerracottaProvisioningProgress>(p => { TerracottaDownloadProgress.Value = p.Percent; TxtMultiplayerStatus.Text = p.Message; });
                TerracottaModule module = await terracottaProvisioning.EnsureAvailableAsync(progress, token);
                TxtMultiplayerStatus.Text = $"核心 {module.Version} 已就绪，正在创建房间……";
                string playerName = string.IsNullOrWhiteSpace(TxtMultiplayerPlayerName.Text) ? "SCL_Player" : TxtMultiplayerPlayerName.Text.Trim();
                TerracottaLobbySnapshot snapshot = await terracottaMultiplayer.CreateHostAsync(module, playerName, token);
                TxtMultiplayerRoomCode.Text = snapshot.RoomCode;
                ApplyTerracottaSnapshot(snapshot);
            }
            catch (OperationCanceledException) { TxtMultiplayerStatus.Text = "操作已取消。"; }
            catch (Exception ex) { TxtMultiplayerStatus.Text = "创建房间失败：" + ex.Message; MessageBox.Show(ex.ToString(), "SCL 陶瓦联机"); }
            finally
            {
                TerracottaDownloadProgress.Visibility = Visibility.Collapsed;
                SetTerracottaBusy(false);
                terracottaOperationCts?.Dispose();
                terracottaOperationCts = null;
            }
        }

        private async void BtnMultiplayerJoin_Click(object sender, RoutedEventArgs e)
        {
            if (terracottaOperationInProgress) return;
            string roomCode = TxtMultiplayerRoomCode.Text?.Trim() ?? string.Empty;
            if (roomCode.Length == 0) { MessageBox.Show("请先输入房间码。", "SCL 陶瓦联机"); return; }

            EnsureTerracottaEventsConnected();
            terracottaOperationCts?.Dispose();
            terracottaOperationCts = new CancellationTokenSource();
            CancellationToken token = terracottaOperationCts.Token;
            SetTerracottaBusy(true);
            TerracottaDownloadProgress.Visibility = Visibility.Visible;
            TerracottaDownloadProgress.Value = 0;
            TxtMultiplayerStatus.Text = "正在检查 / 下载 Terracotta 核心…";

            try
            {
                var progress = new Progress<TerracottaProvisioningProgress>(p => { TerracottaDownloadProgress.Value = p.Percent; TxtMultiplayerStatus.Text = p.Message; });
                TerracottaModule module = await terracottaProvisioning.EnsureAvailableAsync(progress, token);
                TxtMultiplayerStatus.Text = $"核心 {module.Version} 已就绪，正在加入房间……";
                string playerName = string.IsNullOrWhiteSpace(TxtMultiplayerPlayerName.Text) ? "SCL_Player" : TxtMultiplayerPlayerName.Text.Trim();
                TerracottaLobbySnapshot snapshot = await terracottaMultiplayer.JoinAsync(module, roomCode, playerName, token);
                TxtMultiplayerRoomCode.Text = snapshot.RoomCode;
                ApplyTerracottaSnapshot(snapshot);
            }
            catch (OperationCanceledException) { TxtMultiplayerStatus.Text = "操作已取消。"; }
            catch (Exception ex) { TxtMultiplayerStatus.Text = "加入房间失败：" + ex.Message; MessageBox.Show(ex.ToString(), "SCL 陶瓦联机"); }
            finally
            {
                TerracottaDownloadProgress.Visibility = Visibility.Collapsed;
                SetTerracottaBusy(false);
                terracottaOperationCts?.Dispose();
                terracottaOperationCts = null;
            }
        }

        private async void BtnMultiplayerStop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                terracottaOperationCts?.Cancel();
                await terracottaMultiplayer.StopAsync();
                TxtMultiplayerStatus.Text = "联机已结束。";
                ListMultiplayerPlayers.Items.Clear();
                BtnMultiplayerStop.IsEnabled = false;
                BtnMultiplayerCreate.IsEnabled = true;
                BtnMultiplayerJoin.IsEnabled = true;
            }
            catch (Exception ex) { TxtMultiplayerStatus.Text = "结束联机时出错：" + ex.Message; }
        }

        private void BtnMultiplayerCopyCode_Click(object sender, RoutedEventArgs e)
        {
            string code = TxtMultiplayerRoomCode.Text?.Trim() ?? string.Empty;
            if (code.Length == 0) { MessageBox.Show("当前没有房间码。"); return; }
            try { Clipboard.SetText(code); TxtMultiplayerStatus.Text = "房间码已复制：" + code; } catch (Exception ex) { MessageBox.Show("复制失败：" + ex.Message); }
        }

        private void TerracottaMultiplayer_SnapshotChanged(TerracottaLobbySnapshot snapshot)
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyTerracottaSnapshot(snapshot)));
        }

        private void TerracottaMultiplayer_Stopped(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtMultiplayerStatus.Text = message;
                BtnMultiplayerStop.IsEnabled = false;
                BtnMultiplayerCreate.IsEnabled = true;
                BtnMultiplayerJoin.IsEnabled = true;
            }));
        }

        private void ApplyTerracottaSnapshot(TerracottaLobbySnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.RoomCode)) TxtMultiplayerRoomCode.Text = snapshot.RoomCode;
            ListMultiplayerPlayers.Items.Clear();
            foreach (TerracottaLobbyPlayer player in snapshot.Players)
            {
                string labels = player.Kind == TerracottaPlayerKind.Host ? "房主" : "玩家";
                if (player.IsLocal) labels += " · 本机";
                ListMultiplayerPlayers.Items.Add($"{player.DisplayName}  [{labels}]  —  {player.Vendor}");
            }
            TxtMultiplayerStatus.Text = snapshot.State switch
            {
                TerracottaLobbyState.Creating => "正在创建房间……",
                TerracottaLobbyState.Joining => "正在加入房间……",
                TerracottaLobbyState.Active => $"联机已连接 · 房间码：{snapshot.RoomCode} · 玩家数：{snapshot.Players.Count}",
                TerracottaLobbyState.Stopping => "正在结束联机……",
                _ => "准备就绪。"
            };
            bool active = snapshot.State == TerracottaLobbyState.Active;
            BtnMultiplayerStop.IsEnabled = active || snapshot.State == TerracottaLobbyState.Creating || snapshot.State == TerracottaLobbyState.Joining;
            BtnMultiplayerCreate.IsEnabled = !active && !terracottaOperationInProgress;
            BtnMultiplayerJoin.IsEnabled = !active && !terracottaOperationInProgress;
        }

        private void SetTerracottaBusy(bool busy)
        {
            terracottaOperationInProgress = busy;
            BtnMultiplayerCreate.IsEnabled = !busy && terracottaMultiplayer.Current is null;
            BtnMultiplayerJoin.IsEnabled = !busy && terracottaMultiplayer.Current is null;
            BtnMultiplayerStop.IsEnabled = terracottaMultiplayer.Current is not null;
        }

        private async void Window_Closed(object sender, EventArgs e)
        {
            try { terracottaOperationCts?.Cancel(); await terracottaMultiplayer.StopAsync(); }
            catch { }
            finally { terracottaOperationCts?.Dispose(); terracottaMultiplayer.Dispose(); }
        }
    }

    // ================= 微软 OAuth2 登录实现 (已彻底改名防冲突) =================
    public class MsaAuthClient
    {
        private const string ClientId = "9a256803-4f56-46ad-8681-4414b7a7e521";
        private const string Scope = "XboxLive.signin offline_access";
        private const string Tenant = "consumers";
        private static readonly HttpClient Http = new HttpClient();

        public class MinecraftProfile
        {
            public string Name { get; set; } = string.Empty;
            public string Uuid { get; set; } = string.Empty;
            public string AccessToken { get; set; } = string.Empty;
        }

        public async Task<MinecraftProfile> LoginAsync(Action<string> showCodeCallback)
        {
            var deviceCodeReq = new Dictionary<string, string> { { "client_id", ClientId }, { "scope", Scope } };
            var devCodeRes = await PostFormAsync($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/devicecode", deviceCodeReq);

            string deviceCode = devCodeRes.GetProperty("device_code").GetString() ?? string.Empty;
            string userCode = devCodeRes.GetProperty("user_code").GetString() ?? string.Empty;
            string verificationUri = devCodeRes.GetProperty("verification_uri").GetString() ?? string.Empty;
            int interval = devCodeRes.GetProperty("interval").GetInt32();
            int expiresIn = devCodeRes.GetProperty("expires_in").GetInt32();

            // 调用回调，UI层会自动提取验证码并弹窗
            showCodeCallback?.Invoke($"请在浏览器打开 {verificationUri} 并输入代码：\n\n{userCode}");
            try { Process.Start(new ProcessStartInfo(verificationUri) { UseShellExecute = true }); } catch { }

            var tokenReq = new Dictionary<string, string>
            {
                { "client_id", ClientId }, { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" }, { "device_code", deviceCode }
            };

            string msAccessToken = null;
            DateTime startTime = DateTime.Now;
            while (DateTime.Now - startTime < TimeSpan.FromSeconds(expiresIn))
            {
                await Task.Delay(interval * 1000);
                try { msAccessToken = (await PostFormAsync($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", tokenReq)).GetProperty("access_token").GetString(); break; }
                catch (Exception ex) when (ex.Message.Contains("authorization_pending")) { }
            }

            if (string.IsNullOrEmpty(msAccessToken)) throw new Exception("登录超时或失败。");

            var xblReqData = new { Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = msAccessToken }, RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT" };
            var xblRes = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", xblReqData);
            string xblToken = xblRes.GetProperty("Token").GetString() ?? string.Empty;
            string uhs = xblRes.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString() ?? string.Empty;

            var xstsReqData = new { Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } }, RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT" };
            var xstsToken = (await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsReqData)).GetProperty("Token").GetString() ?? string.Empty;

            var mcAuthReqData = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
            string mcAccessToken = (await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcAuthReqData)).GetProperty("access_token").GetString() ?? string.Empty;

            Http.DefaultRequestHeaders.Clear();
            Http.DefaultRequestHeaders.Add("Authorization", $"Bearer {mcAccessToken}");
            var profileRes = await Http.GetAsync("https://api.minecraftservices.com/minecraft/profile");

            if (!profileRes.IsSuccessStatusCode) throw new Exception("该账号未购买 Minecraft。");

            string profileJson = await profileRes.Content.ReadAsStringAsync();
            var profileDoc = JsonDocument.Parse(profileJson).RootElement;

            return new MinecraftProfile
            {
                Name = profileDoc.GetProperty("name").GetString() ?? string.Empty,
                Uuid = profileDoc.GetProperty("id").GetString() ?? string.Empty,
                AccessToken = mcAccessToken
            };
        }

        private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> data)
        {
            var res = await Http.PostAsync(url, new FormUrlEncodedContent(data));
            string json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
            return JsonDocument.Parse(json).RootElement;
        }

        private async Task<JsonElement> PostJsonAsync(string url, object data)
        {
            var res = await Http.PostAsync(url, new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json"));
            string json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new Exception(json);
            return JsonDocument.Parse(json).RootElement;
        }
    }
}