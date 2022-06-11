using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using QuestPatcher.Models;
using QuestPatcher.ViewModels;
using QuestPatcher.Views;
using Serilog;
using Serilog.Events;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using QuestPatcher.ViewModels.Modding;
using QuestPatcher.Core;

namespace QuestPatcher.Services
{
    /// <summary>
    /// Implementation of QuestPatcherService that uses UI message boxes and creates the viewmodel for displaying in UI
    /// </summary>
    public class QuestPatcherUIService : QuestPatcherService
    {
        private readonly Window _mainWindow;

        private readonly IClassicDesktopStyleApplicationLifetime _appLifetime;

        private LoggingViewModel? _loggingViewModel;
        private OperationLocker? _operationLocker;
        private BrowseImportManager? _browseManager;
        private OtherItemsViewModel? _otherItemsView;
        private readonly ThemeManager _themeManager;
        private LoadedViewModel loadedView;
        private bool _isShuttingDown;

        public QuestPatcherUIService(IClassicDesktopStyleApplicationLifetime appLifetime) : base(new UIPrompter())
        {
            _appLifetime = appLifetime;
            _themeManager = new ThemeManager(Config, SpecialFolders, Logger);

            _mainWindow = InitialiseUI();

            _appLifetime.MainWindow = _mainWindow;
            UIPrompter prompter = (UIPrompter) Prompter;
            prompter.Init(_mainWindow, Config, this, SpecialFolders);

            _mainWindow.Opened += async (_, _) => await LoadAndHandleErrors();
            _mainWindow.Closing += OnMainWindowClosing;
        }

        private Window InitialiseUI()
        {
            _loggingViewModel = new LoggingViewModel();
            MainWindow window = new();
            window.Width = 900;
            window.Height = 550;
            _operationLocker = new();
            _operationLocker.StartOperation(); // Still loading
            _browseManager = new(OtherFilesManager, ModManager, window, Logger, PatchingManager, _operationLocker,SpecialFolders,this);
            ProgressViewModel progressViewModel = new(_operationLocker, FilesDownloader);
            _otherItemsView = new OtherItemsViewModel(OtherFilesManager, window, Logger, _browseManager, _operationLocker, progressViewModel);
            loadedView = new LoadedViewModel(
                    new PatchingViewModel(Config, _operationLocker, PatchingManager, window, Logger, progressViewModel, FilesDownloader),
                    new ManageModsViewModel(ModManager, PatchingManager, window, _operationLocker, progressViewModel, _browseManager),
                    _loggingViewModel,
                    new ToolsViewModel(Config, progressViewModel, _operationLocker, window, SpecialFolders, Logger, PatchingManager, DebugBridge, this, InfoDumper,
                        _themeManager,_browseManager),
                    _otherItemsView,
                    Config,
                    PatchingManager,
                    _browseManager,
                    Logger
                );
            MainWindowViewModel mainWindowViewModel = new(
                loadedView,
                new LoadingViewModel(progressViewModel, _loggingViewModel, Config),
                this
            );
            window.DataContext = mainWindowViewModel;

            return window;
        }



        private async Task LoadAndHandleErrors()
        {
            Debug.Assert(_operationLocker != null); // Main window has been loaded, so this is assigned
            if (_operationLocker.IsFree) // Necessary since the operation may have started earlier if this is the first load. Otherwise, we need to start the operation on subsequent loads
            {
                _operationLocker.StartOperation();
            }
            try
            {
                await RunStartup();
                // Files are not loaded during startup, since we need to check ADB status first
                // So instead, we refresh the currently selected file copy after starting, if there is one
                _otherItemsView?.RefreshFiles();
            }
            catch (Exception ex)
            {
                if(ex.ToString().Contains("___flag_beatsaber_not_exists___")) {
                    DialogBuilder builder1 = new()
                    {
                        Title = "尚未安装BeatSaber",
                        Text = "请先安装正版BeatSaber！",
                        HideCancelButton = true
                    };
                    builder1.OkButton.ReturnValue = false;
                    builder1.WithButtons(new ButtonInfo{
                        Text = "安装APK",
                        OnClick = async () =>
                        {
                           await _browseManager.askToInstall();
                        }
                    });
                    await builder1.OpenDialogue(_mainWindow);
                    ExitApplication();
                    return;
                }

                if(ex.ToString().Contains("___flag_beatsaber_version_too_low___"))
                {
                    DialogBuilder builder1 = new()
                    {
                        Title = "旧版的BeatSaber！",
                        Text = "已安装的BeatSaber版本过于老旧\n" +
                        "QuestPatcher只支持1.16.4及以上版本，不支持远古版本。",
                        HideCancelButton = true
                    };
                    builder1.OkButton.ReturnValue = false;
                    builder1.WithButtons( new ButtonInfo
                {
                    Text = "购买正版",
                    CloseDialogue = false,
                    ReturnValue = false,
                    OnClick = async () =>
                    {
                        ProcessStartInfo psi = new()
                        {
                            FileName = "https://www.oculus.com/experiences/quest/2448060205267927",
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                    }
                }, new ButtonInfo
                {
                    Text = "卸载当前版本",
                    CloseDialogue = true,
                    ReturnValue = true,
                    OnClick = async () =>
                    {
                        await PatchingManager.Uninstall();
                    }
                }
            );
                    await builder1.OpenDialogue(_mainWindow);
                    ExitApplication();
                    return;
                }

                
                if(ex.ToString().Contains("___flag_beatsaber_cracked_version___"))
                {
                    ProcessStartInfo psi = new()
                    {
                        FileName = "https://bs.wgzeyu.com/oq-guide-qp/#sbwc8866",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    DialogBuilder builder1 = new()
                    {
                        Title = "非原版BeatSaber！",
                        Text = "检测到已安装的BeatSaber版本可能存在异常，\n" +
                        "你安装的游戏有可能是盗版，QuestPatcher不兼容盗版，请支持正版！",
                        HideCancelButton = true
                    };
                    builder1.OkButton.ReturnValue = false;
                    builder1.WithButtons(
                new ButtonInfo
                {
                    Text = "为何不兼容盗版？",
                    CloseDialogue = false,
                    ReturnValue = false,
                    OnClick = async () =>
                    {
                        ProcessStartInfo psi = new()
                        {
                            FileName = "https://bs.wgzeyu.com/oq-guide-qp/#sbwc8866",
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                    }
                }, new ButtonInfo
                {
                    Text = "购买正版",
                    CloseDialogue = false,
                    ReturnValue = false,
                    OnClick = async () =>
                    {
                        ProcessStartInfo psi = new()
                        {
                            FileName = "https://www.oculus.com/experiences/quest/2448060205267927",
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                    }
                }, new ButtonInfo
                {
                    Text = "卸载当前版本",
                    CloseDialogue = true,
                    ReturnValue = true,
                    OnClick = async () =>
                    {
                        await PatchingManager.Uninstall();
                    }
                }
            );
                    await builder1.OpenDialogue(_mainWindow);
                    ExitApplication();
                    return;
                }
                

               DialogBuilder builder = new()
                {
                    Title = "Unhandled Load Error",
                    Text = "An error occured while loading",
                    HideCancelButton = true
                };
                builder.OkButton.ReturnValue = false;
                builder.WithException(ex);
                builder.WithButtons(
                    new ButtonInfo
                    {
                        Text = "Change App",
                        CloseDialogue = true,
                        ReturnValue = true,
                        OnClick = async () =>
                        {
                            await OpenChangeAppMenu(true);
                        }
                    }
                );

                // If the user did not select to change app, or closed the dialogue, we exit due to the error
                if (!await builder.OpenDialogue(_mainWindow))
                {
                    Logger.Error($"Exiting QuestPatcher due to unhandled load error: {ex}");
                    ExitApplication();
                }
            }   finally
            {
                _operationLocker.FinishOperation();
            }
        }

        private async void OnMainWindowClosing(object? sender, CancelEventArgs args)
        {
            Debug.Assert(_operationLocker != null);

            // Avoid showing this prompt if not in an operation, or if we are closing the window from exiting the application
            if (_operationLocker.IsFree || _isShuttingDown) return;

            // Closing while operations are in progress is a bad idea, so we warn the user
            // We must set this to true at first, even if the user might press OK later.
            // This is since the caller of the event will not wait for our async handler to finish
            args.Cancel = true;
            DialogBuilder builder = new()
            {
                Title = "操作仍在处理中！",
                Text = "QuestPatcher正在处理中。在处理完成前强行关闭可能会损坏你的游戏！"
            };
            builder.OkButton.Text = "强制关闭";

            // Now we can exit the application if the user decides to
            if (await builder.OpenDialogue(_mainWindow))
            {
                ExitApplication();
            }
        }

        /// <summary>
        /// Opens a menu which allows the user to change app ID
        /// </summary>
        public async Task OpenChangeAppMenu(bool quitIfNotSelected)
        {
            Config.AppId = "com.beatgames.beatsaber";
            DialogBuilder builder = new()
            {
                Title = "该改版无法Mod其他应用！",
                Text = "因为加了汉化，核心Mod安装等专对BeatSaber的功能，所以没有办法给其他游戏添加mod，属实抱歉~"
            };
            builder.OkButton.Text = "好的";
            builder.HideCancelButton = true;
            await builder.OpenDialogue(_mainWindow);
            if(quitIfNotSelected)
            {
                ExitApplication();
            }

        }

        public async Task Reload()
        {
            if (_loggingViewModel != null)
            {
                _loggingViewModel.LoggedText = ""; // Avoid confusing people by not showing existing logs
            }

            ModManager.Reset();
            PatchingManager.ResetInstalledApp();
            await LoadAndHandleErrors();
        }

        protected override void SetLoggingOptions(LoggerConfiguration configuration)
        {
            configuration.MinimumLevel.Verbose()
                .WriteTo.File(Path.Combine(SpecialFolders.LogsFolder, "log.log"), LogEventLevel.Verbose, "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console()
                .WriteTo.Sink(
                new StringDelegateSink(str =>
                {
                    if (_loggingViewModel != null)
                    {
                        _loggingViewModel.LoggedText += str + "\n";
                    }
                }),
                LogEventLevel.Information
            );
        }

        protected override void ExitApplication()
        {
            _isShuttingDown = true;
            _appLifetime.Shutdown();
        }
    }
}
