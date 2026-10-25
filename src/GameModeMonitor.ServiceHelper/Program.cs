using GameModeMonitor.Windows;
using System.Security.Principal;

// No scripts, JSON files, arbitrary paths, command lines or shell are accepted.
if (args.Length != 2 || args[0] is not ("start" or "stop") || !GameModeMonitor.Core.ServicePolicy.IsKnownName(args[1])) return 2;
using var identity = WindowsIdentity.GetCurrent();
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
try { NativeServices.Change(args[0], args[1]); return 0; }
catch (UnauthorizedAccessException) { return 3; }
catch (TimeoutException) { return 5; }
catch { return 4; }
