// Copyright (C) 2026 FlyDisk Headless contributors
// SPDX-License-Identifier: GPL-3.0-or-later
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using FlyDisk.Engine;

namespace FlyDisk.Headless;

internal static class Program
{
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, StateStore.Json));
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            string command = args.FirstOrDefault() ?? "help";
            if (command is "help" or "--help")
            {
                Console.WriteLine("FlyDisk Headless (Windows, whole-disk read cache / write-through)\n" +
                    "  list\n  sample-config\n  preflight [--config PATH]\n  run [--config PATH]\n" +
                      "  install --config PATH [--automatic]\n  start\n  stop\n  status\n  configure-recovery\n  supervise\n" +
                    "  recover [--config PATH]\n  uninstall\n" +
                    "Install defaults to manual start. --automatic selects delayed automatic start.\n" +
                    "stop restores the original disk after safely detaching iSCSI; recover handles abandoned offline disks.\n" +
                    "Existing L2 is fully verified on every start. Forced process termination needs explicit recovery.");
                return 0;
            }
            if (command == "sample-config") { Print(new HostConfig()); return 0; }
            string configPath = ServiceManager.ConfigPath;
            bool automatic = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length && command is "install" or "run" or "preflight" or "recover") configPath = Path.GetFullPath(args[++i]);
                else if (args[i] == "--automatic" && command == "install") automatic = true;
                else throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
            }
            if (command == "status")
            {
                string path = Path.Combine(ServiceManager.DataDirectory, "status.json");
                var state = File.Exists(path) ? JsonSerializer.Deserialize<HostState>(File.ReadAllText(path), StateStore.Json) : null;
                bool alive = state != null && StateStore.IsAlive(state);
                Print(new { Service = ServiceManager.GetStatus(), ProcessAlive = alive,
                    StateIsStale = state != null && !alive && state.RecoveryRequired, State = state });
                return 0;
            }
            ServiceManager.RequireAdministrator();
            switch (command)
            {
                case "list":
                    Print(PhysicalDiskHandle.Enumerate().Select(d => new { Identity = PhysicalDiskHandle.DescribeIdentity(d), Disk = d }));
                    break;
                case "install": ServiceManager.Install(configPath, automatic); Print(new { Installed = true, Started = false, Automatic = automatic }); break;
                case "uninstall": ServiceManager.Uninstall(); Print(new { Uninstalled = true }); break;
                case "start": ServiceManager.Start(); Print(new { Service = ServiceManager.GetStatus() }); break;
                  case "stop": RecoverySupervisor.CancelIntent(); ServiceManager.Stop(); Print(new { Service = ServiceManager.GetStatus() }); break;
                  case "supervise": Print(RecoverySupervisor.Run()); break;
                  case "configure-recovery": ServiceManager.ConfigureRecovery(); Print(new { RecoveryConfigured = true }); break;
                case "preflight":
                    using (var owner = HostProcess.AcquireOwnership())
                    { new NativeBackend(HostConfig.Load(configPath)).Preflight(); Print(new { PreflightPassed = true, DiskTakenOffline = false }); }
                    break;
                case "recover":
                    using (var owner = HostProcess.AcquireOwnership())
                    {
                        var config = HostConfig.Load(configPath);
                        var store = new StateStore(ServiceManager.DataDirectory, config);
                        store.AssertRecoveryConfigMatches();
                        new Lifecycle(new NativeBackend(config, true), (p, e, r) => store.Write(p, e, r)).Recover();
                        Print(new { Restored = true });
                    }
                    break;
                case "service":
                    using (var host = new HostProcess(HostConfig.Load(configPath))) ServiceBase.Run(DiskService.Create(host));
                    break;
                case "run":
                    using (var host = new HostProcess(HostConfig.Load(configPath)))
                    using (var signal = new ManualResetEventSlim())
                    {
                        Console.CancelKeyPress += (_, e) => { e.Cancel = true; host.CancelStartup(); signal.Set(); };
                        host.Start();
                        Print(new { Running = true });
                        while (true)
                        {
                            signal.Wait(); signal.Reset();
                            try { host.Stop(); break; }
                            catch (Exception ex) { Console.Error.WriteLine("Stop refused; host retained. Close disk users and press Ctrl+C again. " + ex.Message); }
                        }
                    }
                    break;
                default: throw new ArgumentException("Unknown command: " + command);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Error = ex.Message, Type = ex.GetType().Name }, StateStore.Json));
            return ex is ArgumentException ? 2 : 1;
        }
    }
}
