
// PolarWolves - A Super fast account switcher
// Copyright (C) 2019-2025 PolarWolves Team (Wesley Pyburn)
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CefSharp;
using CefSharp.Wpf;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using PolarWolves_Globals;
using PolarWolves_Server;
using PolarWolves_Server.Data;
using Point = System.Drawing.Point;

namespace PolarWolves_Client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>

    // This is reported as "never used" by JetBrains Inspector... what?
    public partial class MainWindow
    {
        private static readonly Thread Server = new(RunServer);
        private static string _address = "";
        private string _mainBrowser = "WebView"; // <CEF/WebView>
        private const int WmNcHitTest = 0x0084;
        private const int HtCaption = 2;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const int ResizeBorderPixels = 8;
        private const double DragCaptionHeight = 52;
        private const double DragCaptionLeftInset = 250;
        private const double DragCaptionRightInset = 170;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;
        private const uint DwmColorNone = 0xFFFFFFFE;
        // DWM uses COLORREF (0x00BBGGRR), so #05080D becomes 0x000D0805.
        private const uint ShellFrameColor = 0x000D0805;
        private const uint ShellFrameTextColor = 0x00FFFFFF;
        private static readonly SolidColorBrush ShellBackground = new(System.Windows.Media.Color.FromRgb(5, 8, 13));
        // Keep the packed ARGB value local so WebView-only installs don't need the CEF runtime during type init.
        private const uint ShellBackgroundCef = 0xFF05080D;
        private readonly TrayIconManager _trayIconManager;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

        private static void RunServer()
        {
            const string serverPath = "PolarWolves-Server_main.exe";
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(serverPath)).Length > 0)
            {
                Globals.WriteToLog("Server was already running. Killing process.");
                Globals.KillProcess(serverPath); // Kill server if already running
            }

            var attempts = 0;
            while (!Program.MainProgram(new[] { _address, "nobrowser" }) && attempts < 10)
            {
                Program.NewPort();
                _address = "--urls=http://localhost:" + AppSettings.ServerPort + "/";
                attempts++;
            }
            if (attempts == 10)
                MessageBox.Show("The PolarWolves-Server.exe attempted to launch 10 times and failed every time. Every attempted port is taken, or another issue occurred. Check the log file for more info.", "Server start failed!", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static bool IsAdmin()
        {
            // Checks whether program is running as Admin or not
            var securityIdentifier = WindowsIdentity.GetCurrent().Owner;
            return securityIdentifier is not null && securityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        }

        public MainWindow()
        {
            // Ensure first-run installs create their user data before touching the working directory.
            Globals.CreateDataFolder(false);
            if (!Directory.Exists(Globals.UserDataFolder))
                Directory.CreateDirectory(Globals.UserDataFolder);
            // Keep the working directory on the install folder so browser runtimes can load
            // their native files from the packaged layout even on first launch.
            Directory.SetCurrentDirectory(Globals.AppDataFolder);
            _mainBrowser = ResolvePreferredBrowser();

            if (Directory.Exists(Path.Join(Globals.AppDataFolder, "wwwroot")))
            {
                if (Globals.InstalledToProgramFiles() && !IsAdmin() || !Globals.HasFolderAccess(Globals.AppDataFolder))
                    Restart("", true);
                Globals.RecursiveDelete(Globals.OriginalWwwroot, false);
                Directory.Move(Path.Join(Globals.AppDataFolder, "wwwroot"), Globals.OriginalWwwroot);
            }

            Program.FindOpenPort();
            _address = "--urls=http://localhost:" + AppSettings.ServerPort + "/";

            AppData.PolarWolvesClientApp = true;

            // Start web server
            Server.IsBackground = true;
            Server.Start();

            // Initialize the selected browser without touching optional runtimes from the wrong code path.
            BrowserInit();

            // Initialise and connect to web server above
            InitializeComponent();
            AddBrowser();

            FinishBrowserStartup();

            Background = ShellBackground;
            MainBackground.Background = ShellBackground;
            _trayIconManager = new TrayIconManager(this);

            Width = AppSettings.WindowSize.X;
            Height = AppSettings.WindowSize.Y;
            // Keep the shell opaque so resizing never exposes the desktop through the WebView.
            AllowsTransparency = false;
            StateChanged += WindowStateChange;
            // Each window in the program would have its own size. IE Resize for Steam, and more.

            // Center:
            if (!AppSettings.StartCentered) return;
            Left = (SystemParameters.PrimaryScreenWidth / 2) - (Width / 2);
            Top = (SystemParameters.PrimaryScreenHeight / 2) - (Height / 2);
        }

        private object _cefView;
        private WebView2 _mView2;
        private ChromiumWebBrowser CefView => (ChromiumWebBrowser)_cefView;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            ApplyDarkWindowFrame(hwnd);
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget is not null)
                source.CompositionTarget.BackgroundColor = System.Windows.Media.Color.FromRgb(5, 8, 13);
            source?.AddHook(WindowProc);
        }

        private static void ApplyDarkWindowFrame(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            TrySetDwmInt(hwnd, DwmwaUseImmersiveDarkMode, 1);
            TrySetDwmInt(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, 1);
            TrySetDwmColor(hwnd, DwmwaBorderColor, DwmColorNone);
            TrySetDwmColor(hwnd, DwmwaCaptionColor, ShellFrameColor);
            TrySetDwmColor(hwnd, DwmwaTextColor, ShellFrameTextColor);
        }

        private static void TrySetDwmInt(IntPtr hwnd, int attribute, int value)
        {
            try
            {
                _ = DwmSetWindowAttribute(hwnd, attribute, ref value, Marshal.SizeOf<int>());
            }
            catch
            {
                // Older Windows builds may not support every DWM attribute.
            }
        }

        private static void TrySetDwmColor(IntPtr hwnd, int attribute, uint color)
        {
            try
            {
                _ = DwmSetWindowAttribute(hwnd, attribute, ref color, Marshal.SizeOf<uint>());
            }
            catch
            {
                // Keep startup safe if the OS ignores newer frame color APIs.
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmNcHitTest || WindowState == WindowState.Maximized || ResizeMode == ResizeMode.NoResize)
                return IntPtr.Zero;

            var raw = lParam.ToInt64();
            var screenPoint = new System.Windows.Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
            var point = PointFromScreen(screenPoint);
            var dpi = VisualTreeHelper.GetDpi(this);
            var border = ResizeBorderPixels / Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);

            var left = point.X >= 0 && point.X <= border;
            var right = point.X <= ActualWidth && point.X >= ActualWidth - border;
            var top = point.Y >= 0 && point.Y <= border;
            var bottom = point.Y <= ActualHeight && point.Y >= ActualHeight - border;

            if (top && left) return HitTestResult(HtTopLeft, ref handled);
            if (top && right) return HitTestResult(HtTopRight, ref handled);
            if (bottom && left) return HitTestResult(HtBottomLeft, ref handled);
            if (bottom && right) return HitTestResult(HtBottomRight, ref handled);
            if (left) return HitTestResult(HtLeft, ref handled);
            if (right) return HitTestResult(HtRight, ref handled);
            if (top) return HitTestResult(HtTop, ref handled);
            if (bottom) return HitTestResult(HtBottom, ref handled);
            if (IsNativeDragCaptionRegion(point)) return HitTestResult(HtCaption, ref handled);

            return IntPtr.Zero;
        }

        private bool IsNativeDragCaptionRegion(System.Windows.Point point)
        {
            if (point.Y < 0 || point.Y > DragCaptionHeight) return false;
            if (point.X < DragCaptionLeftInset) return false;
            if (point.X > ActualWidth - DragCaptionRightInset) return false;
            return true;
        }

        private static IntPtr HitTestResult(int hitTest, ref bool handled)
        {
            handled = true;
            return new IntPtr(hitTest);
        }

        private void DragCaptionBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            try
            {
                DragMove();
                e.Handled = true;
            }
            catch
            {
                // Ignore drag race conditions if the window state changes mid-drag.
            }
        }

        private static bool HasCefRuntimeFiles()
        {
            string[] cefFiles =
            {
                "libcef.dll",
                "icudtl.dat",
                "resources.pak",
                "libGLESv2.dll",
                "d3dcompiler_47.dll",
                "vk_swiftshader.dll",
                "chrome_elf.dll",
                "CefSharp.BrowserSubprocess.Core.dll"
            };

            foreach (var cefFile in cefFiles)
            {
                var path = Path.Join(Globals.AppDataFolder, "runtimes\\win-x64\\native\\", cefFile);
                if (!File.Exists(path) || new FileInfo(path).Length <= 10)
                    return false;
            }

            return true;
        }

        private static string ResolvePreferredBrowser()
        {
            var preferredBrowser = AppSettings.ActiveBrowser;
            if (string.Equals(preferredBrowser, "CEF", StringComparison.OrdinalIgnoreCase) && !HasCefRuntimeFiles())
            {
                Globals.WriteToLog("CEF was selected but runtime files were missing. Falling back to WebView.");
                AppSettings.ActiveBrowser = "WebView";
                AppSettings.SaveSettings();
                return "WebView";
            }

            if (string.Equals(preferredBrowser, "CEF", StringComparison.OrdinalIgnoreCase))
                return "CEF";

            if (!string.Equals(preferredBrowser, "WebView", StringComparison.OrdinalIgnoreCase))
            {
                AppSettings.ActiveBrowser = "WebView";
                AppSettings.SaveSettings();
            }

            return "WebView";
        }

        private void BrowserInit()
        {
            switch (_mainBrowser)
            {
                case "WebView":
                    InitialiseWebViewBrowser();
                    break;
                case "CEF":
                    InitialiseCefBrowser();
                    break;
            }
        }

        private void InitialiseWebViewBrowser()
        {
            _mView2 = new WebView2();
            _mView2.Initialized += MView2_OnInitialised;
            _mView2.NavigationCompleted += MView2_OnNavigationCompleted;
        }

        private void InitialiseCefBrowser()
        {
            CheckCefFiles();

            InitializeChromium();
            _cefView = new ChromiumWebBrowser();
            CefView.Background = ShellBackground;
            CefView.BrowserSettings = new BrowserSettings
            {
                BackgroundColor = ShellBackgroundCef,
                WindowlessFrameRate = 60
            };
            CefView.JavascriptMessageReceived += CefView_OnJavascriptMessageReceived;
            CefView.AddressChanged += CefViewOnAddressChanged;
            CefView.PreviewMouseUp += MainBackgroundOnPreviewMouseUp;
            CefView.ConsoleMessage += CefViewOnConsoleMessage;
            CefView.KeyboardHandler = new CefKeyboardHandler();
            CefView.MenuHandler = new CefMenuHandler();
        }

        private void FinishBrowserStartup()
        {
            if (_mainBrowser == "WebView")
            {
                // Attempt to fix window showing as blank.
                // See https://github.com/MicrosoftEdge/WebView2Feedback/issues/1077#issuecomment-856222593593
                _mView2.Visibility = Visibility.Hidden;
                _mView2.Visibility = Visibility.Visible;
                return;
            }

            if (_mainBrowser == "CEF")
            {
                CefView.BrowserSettings.WindowlessFrameRate = 60;
                CefView.Visibility = Visibility.Visible;
                CefView.Load("http://localhost:" + AppSettings.ServerPort + "/");
            }
        }

        private void CefViewOnConsoleMessage(object? sender, ConsoleMessageEventArgs e)
        {
            if (e.Level == LogSeverity.Error)
            {
                // Filter out non-critical errors that shouldn't trigger refreshes
                var message = e.Message ?? "";
                var isNonCriticalError = message.Contains("runtime.lastError") || 
                                        message.Contains("message port closed") ||
                                        message.Contains("Extension context invalidated");

                if (isNonCriticalError)
                {
                    // Log but don't refresh for non-critical errors
                    Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - CEF WARNING (Non-critical): {message + Environment.NewLine}LINE: {e.Line + Environment.NewLine}SOURCE: {e.Source}");
                    return;
                }

                Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - CEF EXCEPTION (Handled: refreshed): {message + Environment.NewLine}LINE: {e.Line + Environment.NewLine}SOURCE: {e.Source}");
                _refreshFixAttempts++;
                if (_refreshFixAttempts < 5)
                {
                    CefView.Reload();
                }
                else
                {
                    // Stop refreshing and log the error instead of crashing
                    Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - CEF EXCEPTION (Stopped refreshing after {_refreshFixAttempts} attempts): {message}");
                    _refreshFixAttempts = 0; // Reset counter after max attempts
                }
            }
            else
            {
#if RELEASE
                try
                {
                    // ReSharper disable once PossibleNullReferenceException
                    Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - CEF: " + e.Message.Replace("\n", "\n\t"));
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog(ex);
                }
#endif
            }
        }

        private void MainBackgroundOnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.XButton1 && e.ChangedButton != MouseButton.XButton2) return;
            // Back
            e.Handled = true;
            CefView.ExecuteScriptAsync("btnBack_Click()");
        }

        private void AddBrowser()
        {
            if (_mainBrowser == "WebView")
            {
                AddWebViewBrowser();
            }
            else if (_mainBrowser == "CEF")
            {
                AddCefBrowser();
            }
        }

        private void AddWebViewBrowser()
        {
            _mView2.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 5, 8, 13);
            MainBackground.Children.Add(_mView2);
        }

        private void AddCefBrowser()
        {
            CefView.Background = ShellBackground;
            MainBackground.Children.Add(CefView);
        }

        #region CEF
        private static void InitializeChromium()
        {
            Globals.DebugWriteLine(@"[Func:(Client-CEF)MainWindow.xaml.cs.InitializeChromium]");
            try
            {
                var settings = new CefSettings
                {
                    CachePath = Path.Join(Globals.UserDataFolder, "CEF\\Cache"),
                    UserAgent = "PolarWolves-CEF 1.0",
                    WindowlessRenderingEnabled = true,
                    BackgroundColor = ShellBackgroundCef
                };
                settings.CefCommandLineArgs.Add("-off-screen-rendering-enabled", "0");
                settings.CefCommandLineArgs.Add("--off-screen-frame-rate", "60");
                settings.SetOffScreenRenderingBestPerformanceArgs();

                Cef.Initialize(settings);
                //CefView.DragHandler = new DragDropHandler();
                //CefView.IsBrowserInitializedChanged += CefView_IsBrowserInitializedChanged;
                //CefView.FrameLoadEnd += OnFrameLoadEnd;
            } catch (Exception ex) {
                Globals.WriteToLog(ex);
                // Fresh installs should recover automatically instead of sending users to the updater.
                AppSettings.ActiveBrowser = "WebView";
                AppSettings.SaveSettings();
                _ = MessageBox.Show(
                    "CEF failed to load. Polar Account Switcher will switch to WebView2 automatically.",
                    "CEF fallback",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Restart();
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Check if all CEF Files are available. If not > Close and download, or revert to WebView.
        /// </summary>
        private static void CheckCefFiles()
        {
            if (HasCefRuntimeFiles()) return;

            Globals.WriteToLog("CEF files were missing at startup. Falling back to WebView.");
            AppSettings.ActiveBrowser = "WebView";
            AppSettings.SaveSettings();
            Restart();
            Environment.Exit(1);
        }

        private void CefView_OnJavascriptMessageReceived(object? sender, JavascriptMessageReceivedEventArgs e)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var actionValue = (IDictionary<string, object>)e.Message;
                var eventForwarder = new EventForwarder(new WindowInteropHelper(this).Handle);
                switch (actionValue["action"].ToString())
                {
                    case "WindowAction":
                        eventForwarder.WindowAction((int)actionValue["value"]);
                        break;
                    case "HideWindow":
                        eventForwarder.HideWindow();
                        break;
                    case "MouseResizeDrag":
                        eventForwarder.MouseResizeDrag((int)actionValue["value"]);
                        break;
                    case "MouseDownDrag":
                        eventForwarder.MouseDownDrag();
                        break;
                }
            }));
        }
        #endregion

        #region BROWSER_SHARED
        private void CefViewOnAddressChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Globals.DebugWriteLine(@"[Func:(Client)MainWindow.xaml.cs.UrlChanged]");
            UrlChanged(e.NewValue.ToString() ?? string.Empty);
            // Reset refresh counter on successful navigation
            _refreshFixAttempts = 0;
        }
        private void MViewUrlChanged(object sender, CoreWebView2NavigationStartingEventArgs args)
        {
            Globals.DebugWriteLine(@"[Func:(Client)MainWindow.xaml.cs.UrlChanged]");
            UrlChanged(args.Uri);
        }
        /// <summary>
        /// Runs on URI change in the WebView.
        /// </summary>
        private void UrlChanged(string uri)
        {
            // // Unused:
            // // This was originally for allowing users to activate keys for specific accounts
            // // This was never fleshed out fully, and remains just coments for now.

            //Globals.WriteToLog(uri);

            //// This is used with Steam/SteamKeys.cs for future functionality!
            //if (uri.Contains("store.steampowered.com"))
            //    _ = RunCookieCheck("steampowered.com");

            //if (uri.Contains("EXIT_APP")) Environment.Exit(0);
        }

        /// <summary>
        /// Gets all cookies, with optional filter.
        /// </summary>
        /// <returns>Cookies string "Key=Val; ..."</returns>
        private async Task<string> RunCookieCheck(string filter)
        {
            // Currently only used for Steam, but the filter is implemented for future possible functionality.

            var cookies = await _mView2.CoreWebView2.CookieManager.GetCookiesAsync(null);
            var cookiesTxt = "";
            var failedCookies = new List<string>();
            foreach (var c in cookies.Where(c => c.Domain.Contains(filter)))
            {
                if (string.IsNullOrWhiteSpace(c.Value))
                    failedCookies.Add(c.Name);
                else
                    cookiesTxt += $"{c.Name}={c.Value}; ";
            }

            // Reiterate over cookies with no values (They have values, just sometimes one or two are missed for some reason.
            foreach (var failedCookie in failedCookies)
            {
                if (cookiesTxt.Contains($"{failedCookie}=")) continue;
                var attempts = 0;
                while (attempts < 5)
                {
                    attempts++;
                    cookies = await _mView2.CoreWebView2.CookieManager.GetCookiesAsync(null);
                    if (!cookies.Any(c => c.Name == failedCookie && !string.IsNullOrWhiteSpace(c.Value))) continue;
                    cookiesTxt += $"{failedCookie}={cookies.First(c => c.Name == failedCookie).Value}; ";
                    break;
                }
            }

            // "sessionid" cookie not found? (for Steam only)
            if (filter == "steampowered.com")
            {

            }
            if (!cookiesTxt.Contains("sessionid="))
            {
                var docCookies = await _mView2.CoreWebView2.ExecuteScriptAsync("document.cookie");
                var sid = docCookies.Split("sessionid=")[1].Split(";")[0];
                if (sid[^1] == '"') sid = sid[..^1]; // If last char is quotation mark: remove
                cookiesTxt += $"sessionid={sid};";
            }
            Console.WriteLine(cookiesTxt);
            return cookiesTxt;
        }
        #endregion

        #region WebView
        private async void MView2_OnInitialised(object sender, EventArgs e)
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Globals.UserDataFolder);
                await _mView2.EnsureCoreWebView2Async(env);
                _mView2.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 5, 8, 13);
                _mView2.CoreWebView2.Settings.UserAgent = "PolarWolves 1.0";
                _ = await _mView2.CoreWebView2.CallDevToolsProtocolMethodAsync(
                    "Emulation.setDefaultBackgroundColorOverride",
                    "{\"color\":{\"r\":5,\"g\":8,\"b\":13,\"a\":1}}");

                _mView2.Source = new Uri($"http://localhost:{AppSettings.ServerPort}/{App.StartPage}");
                MViewAddForwarders();
                _mView2.NavigationStarting += MViewUrlChanged;
                _mView2.CoreWebView2.ProcessFailed += CoreWebView2OnProcessFailed;

                _mView2.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled")
                    .DevToolsProtocolEventReceived += ConsoleMessage;
                _mView2.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown")
                    .DevToolsProtocolEventReceived += ConsoleMessage;
                _ = await _mView2.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            }
            catch (Exception ex) when (ex is BadImageFormatException or WebView2RuntimeNotFoundException or COMException)
            {
                if (ex is COMException && !ex.ToString().Contains("WebView2"))
                {
                    // Is not a WebView2 exception
                    throw;
                }

                // WebView2 is not installed!
                // Create counter for WebView failed checks
                var failFile = Path.Join(Globals.UserDataFolder, "WebViewNotInstalled");
                if (!File.Exists(failFile))
                    await File.WriteAllTextAsync(failFile, "1");
                else
                {
                    if (await File.ReadAllTextAsync(failFile) == "1") await File.WriteAllTextAsync(failFile, "2");
                    else
                    {
                        AppSettings.ActiveBrowser = "CEF";
                        AppSettings.SaveSettings();
                        _ = MessageBox.Show(
                            "WebView2 Runtime is not installed. The program will now download and use the fallback CEF browser. (Less performance, more compatibility)",
                            "Required runtime not found! Using fallback.", MessageBoxButton.OK, MessageBoxImage.Error);
                        AppSettings.AutoStartUpdaterAsAdmin("downloadCEF");
                        Globals.DeleteFile(failFile);
                        Environment.Exit(1);
                    }
                }

                var result =
                    MessageBox.Show(
                        "WebView2 Runtime is not installed. I've opened the website you need to download it from.",
                        "Required runtime not found!", MessageBoxButton.OKCancel, MessageBoxImage.Error);
                if (result == MessageBoxResult.OK)
                {
                    _ = Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                    {
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                else Environment.Exit(1);
            }

            //MView2.CoreWebView2.OpenDevToolsWindow();
        }

        // For draggable regions:
        // https://github.com/MicrosoftEdge/WebView2Feedback/issues/200
        private void MViewAddForwarders()
        {
            if (_mainBrowser != "WebView") return;
            Globals.DebugWriteLine(@"[Func:(Client)MainWindow.xaml.cs.MViewAddForwarders]");
            var eventForwarder = new EventForwarder(new WindowInteropHelper(this).Handle);

            try
            {
                _mView2.CoreWebView2.AddHostObjectToScript("eventForwarder", eventForwarder);
            }
            catch (NullReferenceException)
            {
                // To mitigate: Object reference not set to an instance of an object - Was getting a few of these a day with CrashLog reports
                if (_mView2.IsInitialized)
                    _mView2.Reload();
                else throw;
            }
            _ = _mView2.Focus();
        }
        private void CoreWebView2OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _ = MessageBox.Show("The WebView browser process has crashed! The program will now exit.", "Fatal error", MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
            Environment.Exit(1);
        }

        private static bool _firstLoad = true;
        private void MView2_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // Reset refresh counter on successful navigation
            if (e.IsSuccess)
            {
                _refreshFixAttempts = 0;
                _ = _mView2.CoreWebView2.ExecuteScriptAsync("""
                    (() => {
                      const dark = '#05080d';
                      document.documentElement.style.background = dark;
                      document.documentElement.style.backgroundColor = dark;
                      document.body.style.background = dark;
                      document.body.style.backgroundColor = dark;
                      const root = document.getElementById('root');
                      if (root) {
                        root.style.background = dark;
                        root.style.backgroundColor = dark;
                      }
                    })();
                    """);
            }

            if (!_firstLoad) return;
            _mView2.Visibility = Visibility.Hidden;
            _mView2.Visibility = Visibility.Visible;
            _firstLoad = false;
        }
        #endregion

        private int _refreshFixAttempts;

        /// <summary>
        /// Handles console messages, and logs them to a file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ConsoleMessage(object sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            if (_mainBrowser != "WebView") return;
            if (e?.ParameterObjectAsJson == null) return;
            var message = JObject.Parse(e.ParameterObjectAsJson);
            if (message.ContainsKey("exceptionDetails"))
            {
                var expandedError = "";
                var details = message["exceptionDetails"];
                if (details != null)
                {
                    var ex = details.Value<JObject>("exception");
                    if (ex != null)
                    {
                        expandedError = $"{Environment.NewLine}{(string)details["url"]}:{(string)details["lineNumber"]}:{(string)details["columnNumber"]} - {(string)ex["description"]}{Environment.NewLine}{Environment.NewLine}Stack Trace:{Environment.NewLine}";
                    }

                    var stackTrace = details.Value<JObject>("stackTrace");
                    var callFrames = stackTrace?["callFrames"]?.ToObject<JArray>();
                    if (callFrames != null)
                    {
                        foreach (var callFrame in callFrames)
                        {
                            expandedError += $"    at {(string)callFrame["functionName"]} in {(string)details["url"]}:line {(string)callFrame["lineNumber"]}:{(string)callFrame["columnNumber"]}{Environment.NewLine}";
                        }
                    }
                }




                var errorDescription = message.SelectToken("exceptionDetails.exception.description")?.ToString() ?? "";
                
                // Filter out non-critical errors that shouldn't trigger refreshes
                var isNonCriticalError = errorDescription.Contains("runtime.lastError") || 
                                        errorDescription.Contains("message port closed") ||
                                        errorDescription.Contains("Extension context invalidated");

                if (isNonCriticalError)
                {
                    // Log but don't refresh for non-critical errors
                    Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - WebView2 WARNING (Non-critical): {errorDescription}");
                    return;
                }

                Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - WebView2 EXCEPTION (Handled: refreshed): {errorDescription}{Environment.NewLine}{expandedError}{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}FULL ERROR: {e.ParameterObjectAsJson}");
                // Load json from string e.ParameterObjectAsJson
                _refreshFixAttempts++;
                if (_refreshFixAttempts < 5)
                {
                    _mView2.Reload();
                }
                else
                {
                    // Stop refreshing and log the error instead of crashing
                    Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - WebView2 EXCEPTION (Stopped refreshing after {_refreshFixAttempts} attempts): {errorDescription}");
                    _refreshFixAttempts = 0; // Reset counter after max attempts
                }
            }
            else
            {
#if RELEASE
                try
                {
                    // ReSharper disable once PossibleNullReferenceException
                    foreach (var jo in message.SelectToken("args"))
                    {
                        Globals.WriteToLog(@$"{DateTime.Now:dd-MM-yy_hh:mm:ss.fff} - WebView2: " + jo.SelectToken("value")?.ToString().Replace("\n", "\n\t"));
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog(ex);
                }
#endif
            }
        }

        /// <summary>
        /// Rungs on WindowStateChange, to update window controls in the WebView2.
        /// </summary>
        private void WindowStateChange(object sender, EventArgs e)
        {
            Globals.DebugWriteLine(@"[Func:(Client)MainWindow.xaml.cs.WindowStateChange]");

            var state = WindowState switch
            {
                WindowState.Maximized => "add",
                WindowState.Normal => "remove",
                _ => ""
            };
            if (_mainBrowser == "WebView")
                _ = _mView2.ExecuteScriptAsync("document.body.classList." + state + "('maximised')");
            else if (_mainBrowser == "CEF")
                UpdateCefWindowState(state);
        }

        private void UpdateCefWindowState(string state)
        {
            CefView.ExecuteScriptAsync("document.body.classList." + state + "('maximised')");
        }

        /// <summary>
        /// Saves window size when closing.
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            Globals.DebugWriteLine(@"[Func:(Client)MainWindow.xaml.cs.OnClosing]");
            _trayIconManager?.HandleWindowClosing(e);
            if (e.Cancel) return;
            AppSettings.WindowSize = new Point { X = Convert.ToInt32(Width), Y = Convert.ToInt32(Height) };
            AppSettings.SaveSettings();
            _trayIconManager?.Dispose();
        }

        public static void Restart(string args = "", bool admin = false)
        {
            var proc = new ProcessStartInfo
            {
                WorkingDirectory = Environment.CurrentDirectory,
                FileName = Assembly.GetEntryAssembly()?.Location.Replace(".dll", ".exe") ?? "PolarWolves_main.exe",
                UseShellExecute = true,
                Arguments = args,
                Verb = admin ? "runas" : ""
            };
            try
            {
                _ = Process.Start(proc);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Globals.WriteToLog(@"This program must be run as an administrator!" + Environment.NewLine + ex);
                Environment.Exit(0);
            }
        }
    }
}

