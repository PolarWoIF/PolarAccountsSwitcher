using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PolarWolves_Client
{
    internal sealed class TrayIconManager : IDisposable
    {
        private const string AppName = "Polar Account Switcher";
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "PolarAccountSwitcher";

        private readonly Window _window;
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ContextMenuStrip _contextMenu;
        private readonly Icon _trayIcon;
        private bool _isExiting;
        private bool _hasShownBalloonTip;
        private bool _disposed;

        public TrayIconManager(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _trayIcon = LoadTrayIcon();
            _contextMenu = BuildContextMenu();
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = AppName,
                Visible = true,
                ContextMenuStrip = _contextMenu
            };

            _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
            EnsureStartupRegistration();
        }

        public bool IsRealExitRequested => _isExiting;

        public void HandleWindowClosing(CancelEventArgs e)
        {
            if (e is null) return;

            if (_isExiting)
            {
                Dispose();
                return;
            }

            e.Cancel = true;
            HideToTray();
        }

        public void RequestExit()
        {
            if (_isExiting) return;

            _isExiting = true;
            _notifyIcon.Visible = false;

            _window.Dispatcher.Invoke(() =>
            {
                _window.ShowInTaskbar = true;
                Forms.Application.Exit();
                _window.Close();
                System.Windows.Application.Current?.Shutdown();
            });
        }

        private Forms.ContextMenuStrip BuildContextMenu()
        {
            var menu = new Forms.ContextMenuStrip();
            var openItem = new Forms.ToolStripMenuItem("Open");
            var exitItem = new Forms.ToolStripMenuItem("Exit");

            openItem.Click += (_, _) => RestoreWindow();
            exitItem.Click += (_, _) => RequestExit();

            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);
            return menu;
        }

        private void HideToTray()
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.ShowInTaskbar = false;
                _window.Hide();

                if (_hasShownBalloonTip) return;

                _notifyIcon.BalloonTipTitle = AppName;
                _notifyIcon.BalloonTipText = "التطبيق يعمل في الخلفية";
                _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(2500);
                _hasShownBalloonTip = true;
            });
        }

        private void RestoreWindow()
        {
            _window.Dispatcher.Invoke(() =>
            {
                _window.ShowInTaskbar = true;
                if (!_window.IsVisible)
                    _window.Show();

                _window.WindowState = WindowState.Normal;
                _window.Activate();
                _window.Focus();
            });
        }

        private static void EnsureStartupRegistration()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, true);
                if (key is null) return;

                var exePath = Forms.Application.ExecutablePath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

                var quotedPath = "\"" + exePath + "\"";
                var existingValue = key.GetValue(StartupValueName) as string;
                if (string.Equals(existingValue, quotedPath, StringComparison.OrdinalIgnoreCase))
                    return;

                key.SetValue(StartupValueName, quotedPath, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray startup registration failed: {ex}");
            }
        }

        private static Icon LoadTrayIcon()
        {
            var executablePath = Forms.Application.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                using var extracted = Icon.ExtractAssociatedIcon(executablePath);
                if (extracted is not null)
                    return (Icon)extracted.Clone();
            }

            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
            {
                using var extracted = Icon.ExtractAssociatedIcon(assemblyPath);
                if (extracted is not null)
                    return (Icon)extracted.Clone();
            }

            return (Icon)SystemIcons.Application.Clone();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _trayIcon.Dispose();
        }
    }
}
