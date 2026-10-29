using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ActEnhanced.KeyboundProbe
{
    public sealed class DalamudAutoLauncher
    {
        private readonly string runtimeDirectory;
        private int attemptedGamePid;
        private DateTime nextCheckUtc = DateTime.MinValue;
        private string status = "等待 FF14 进程";

        public DalamudAutoLauncher(string runtimeDirectory)
        {
            this.runtimeDirectory = runtimeDirectory;
        }

        public string Status { get { return status; } }

        public void ResetAttempt()
        {
            attemptedGamePid = 0;
            nextCheckUtc = DateTime.MinValue;
            status = "已允许重新尝试";
        }

        public void Poll(bool enabled, bool bridgeConnected)
        {
            if (!enabled)
            {
                status = "已停用";
                return;
            }
            if (bridgeConnected)
            {
                status = "Dalamud 桥接已连接";
                return;
            }
            if (DateTime.UtcNow < nextCheckUtc)
                return;
            nextCheckUtc = DateTime.UtcNow.AddSeconds(2);

            Process game = FindGameProcess();
            if (game == null)
            {
                attemptedGamePid = 0;
                status = "等待 FF14 进程";
                return;
            }

            try
            {
                try
                {
                    if (DateTime.Now - game.StartTime < TimeSpan.FromSeconds(8))
                    {
                        status = "等待游戏初始化（PID " + game.Id + "）";
                        return;
                    }
                }
                catch
                {
                    status = "无法读取 FF14 启动时间，未注入";
                    return;
                }

                bool inspectionFailed;
                if (HasDalamudModule(game, out inspectionFailed))
                {
                    status = "游戏已加载其他 Dalamud，但桥接未连接；不会重复注入";
                    return;
                }
                if (inspectionFailed)
                {
                    status = "无法检查游戏模块，出于安全考虑未注入";
                    return;
                }
                if (attemptedGamePid == game.Id)
                    return;

                attemptedGamePid = game.Id;
                StartInjector(game.Id);
            }
            finally
            {
                game.Dispose();
            }
        }

        private void StartInjector(int gamePid)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string launcherRoot = Path.Combine(appData, "XIVLauncherCN");
                string hooksRoot = Path.Combine(launcherRoot, "addon", "Hooks");
                string hooks = FindNewestDirectoryWithFile(hooksRoot, "Dalamud.Injector.exe");
                string assets = FindNewestNumericDirectory(Path.Combine(launcherRoot, "dalamudAssets"));
                string dotnetRuntime = Path.Combine(launcherRoot, "runtime");
                string injector = hooks == null ? null : Path.Combine(hooks, "Dalamud.Injector.exe");
                string config = Path.Combine(runtimeDirectory, "dalamudConfig.json");
                string plugins = Path.Combine(runtimeDirectory, "installedPlugins");
                string logs = Path.Combine(runtimeDirectory, "logs");
                string temp = Path.Combine(runtimeDirectory, "temp");

                foreach (string required in new[] { injector, assets, dotnetRuntime, config })
                {
                    if (String.IsNullOrEmpty(required) || (!File.Exists(required) && !Directory.Exists(required)))
                    {
                        status = "最小 Dalamud 运行文件不完整：" + (required ?? "<未找到>");
                        return;
                    }
                }
                Directory.CreateDirectory(plugins);
                Directory.CreateDirectory(logs);
                Directory.CreateDirectory(temp);

                string[] arguments =
                {
                    "inject",
                    gamePid.ToString(CultureInfo.InvariantCulture),
                    "--dalamud-working-directory=" + hooks,
                    "--dalamud-configuration-path=" + config,
                    "--dalamud-plugin-directory=" + plugins,
                    "--dalamud-asset-directory=" + assets,
                    "--dalamud-temp-directory=" + temp,
                    "--dalamud-client-language=4",
                    "--dalamud-platform=win32",
                    "--dalamud-delay-initialize=0",
                    "--logpath=" + logs,
                    "--logname=-keybound"
                };

                var start = new ProcessStartInfo
                {
                    FileName = injector,
                    WorkingDirectory = hooks,
                    Arguments = String.Join(" ", arguments.Select(QuoteArgument)),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.EnvironmentVariables["DALAMUD_RUNTIME"] = dotnetRuntime;
                start.EnvironmentVariables["DOTNET_ROOT"] = dotnetRuntime;
                Process process = Process.Start(start);
                if (process == null)
                {
                    status = "无法启动 Dalamud.Injector";
                    return;
                }
                status = "已对 PID " + gamePid + " 启动最小 Dalamud，等待桥接";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        if (process.WaitForExit(15000) && process.ExitCode != 0)
                            status = "Dalamud.Injector 失败，退出码 " + process.ExitCode;
                    }
                    catch (Exception ex)
                    {
                        status = "Dalamud.Injector 状态异常：" + ex.Message;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                status = "启动最小 Dalamud 失败：" + ex.Message;
            }
        }

        private static Process FindGameProcess()
        {
            Process[] dx11 = Process.GetProcessesByName("ffxiv_dx11");
            Process[] candidates = dx11.Length != 0 ? dx11 : Process.GetProcessesByName("ffxiv");
            Process selected = candidates.OrderByDescending(SafeStartTime).FirstOrDefault();
            foreach (Process candidate in candidates)
                if (!Object.ReferenceEquals(candidate, selected))
                    candidate.Dispose();
            return selected;
        }

        private static DateTime SafeStartTime(Process process)
        {
            try { return process.StartTime; }
            catch { return DateTime.MinValue; }
        }

        private static bool HasDalamudModule(Process game, out bool inspectionFailed)
        {
            inspectionFailed = false;
            try
            {
                foreach (ProcessModule module in game.Modules)
                {
                    string name = module.ModuleName;
                    if (String.Equals(name, "Dalamud.Boot.dll", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(name, "Dalamud.dll", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch
            {
                inspectionFailed = true;
                return false;
            }
        }

        private static string FindNewestDirectoryWithFile(string root, string fileName)
        {
            if (!Directory.Exists(root))
                return null;
            return Directory.GetDirectories(root)
                .Where(path => File.Exists(Path.Combine(path, fileName)))
                .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }

        private static string FindNewestNumericDirectory(string root)
        {
            if (!Directory.Exists(root))
                return null;
            return Directory.GetDirectories(root)
                .Select(path => new { Path = path, Name = Path.GetFileName(path) })
                .Where(value => value.Name.All(Char.IsDigit))
                .OrderByDescending(value => Int32.Parse(value.Name, CultureInfo.InvariantCulture))
                .Select(value => value.Path)
                .FirstOrDefault();
        }

        private static string QuoteArgument(string value)
        {
            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return value;
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
