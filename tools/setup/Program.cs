using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GrannyCoop.Setup
{
    /// <summary>
    /// Two faces on one binary: a form for people, and a silent command line for
    /// QA and for the "install over here" scripted case. The command line is
    /// chosen by the presence of --install.
    /// </summary>
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private const int AttachParentProcess = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            var opt = Options.Parse(args);

            if (opt.Help)
            {
                AttachConsole(AttachParentProcess);
                ReopenStdout();
                Console.WriteLine();
                Console.WriteLine(Options.HelpText);
                return 0;
            }

            if (opt.InstallDir != null)
            {
                AttachConsole(AttachParentProcess);
                ReopenStdout();
                return CommandLine.Run(opt);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(opt));
            return 0;
        }

        private static void ReopenStdout()
        {
            // after AttachConsole the inherited handles are not the ones Console captured
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true });
            }
            catch
            {
                // no console to talk to; the log file still gets everything
            }
        }
    }

    internal sealed class Options
    {
        public string InstallDir;
        public string Role;          // "off" | "host" | "join"; null = leave preferences alone
        public string PlayerName;
        public string Address;
        public int Port = 47777;
        public bool Silent;
        public bool SkipRuntime;
        public bool Force;
        public bool Help;

        public const string HelpText =
@"Granny Co-op Setup

  GrannyCoop-Setup.exe
      graphical installer: pick the game folder, choose Host or Join.

  GrannyCoop-Setup.exe --install ""<game dir>"" [options]
      install without the form (used by tools/run-pair.ps1 and CI).

    --role off|host|join   write the role into UserData/MelonPreferences.cfg
    --name <name>          player name
    --address <ip>         host address for --role join
    --port <n>             UDP port (default 47777)
    --skip-runtime         do not install the .NET 6 runtime
    --force                install even if the build looks untested
    --silent               no output other than progress lines
    --help                 this text

Exit codes: 0 ok, 1 usage, 2 not a Granny folder, 3 untested build,
            4 the game is running, 5 write failed, 6 runtime missing.";

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;

                switch (a.ToLowerInvariant())
                {
                    case "--install":
                        o.InstallDir = Value(ref i, args, a, next);
                        break;
                    case "--role":
                        o.Role = (Value(ref i, args, a, next) ?? "off").ToLowerInvariant();
                        break;
                    case "--name":
                        o.PlayerName = Value(ref i, args, a, next);
                        break;
                    case "--address":
                        o.Address = Value(ref i, args, a, next);
                        break;
                    case "--port":
                        int p;
                        if (!int.TryParse(Value(ref i, args, a, next), out p) || p < 1 || p > 65535)
                            throw new ArgumentException("--port needs a number 1..65535");
                        o.Port = p;
                        break;
                    case "--silent":
                        o.Silent = true;
                        break;
                    case "--skip-runtime":
                        o.SkipRuntime = true;
                        break;
                    case "--force":
                        o.Force = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        o.Help = true;
                        break;
                    default:
                        if (a.StartsWith("--install=", StringComparison.OrdinalIgnoreCase))
                            o.InstallDir = a.Substring("--install=".Length).Trim('"');
                        else if (a.StartsWith("--role=", StringComparison.OrdinalIgnoreCase))
                            o.Role = a.Substring("--role=".Length).ToLowerInvariant();
                        else if (a.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
                            o.PlayerName = a.Substring("--name=".Length);
                        else if (a.StartsWith("--address=", StringComparison.OrdinalIgnoreCase))
                            o.Address = a.Substring("--address=".Length);
                        else if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                            o.Port = int.Parse(a.Substring("--port=".Length));
                        else if (a.StartsWith("-") && a.Length > 1)
                            throw new ArgumentException("unknown option: " + a);
                        else if (o.InstallDir == null)
                            o.InstallDir = a.Trim('"');
                        break;
                }
            }
            return o;
        }

        private static string Value(ref int i, string[] args, string name, string next)
        {
            if (next == null) throw new ArgumentException(name + " needs a value");
            i++;
            return next;
        }
    }

    internal static class CommandLine
    {
        public static int Run(Options o)
        {
            string logPath = Log.NewPath();
            Action<string> log = line =>
            {
                Log.Write(logPath, line);
                Console.WriteLine(line);
            };

            try
            {
                return Installer.Run(o, log, null);
            }
            catch (Exception e)
            {
                log("ERROR " + e.GetType().Name + ": " + e.Message);
                return 5;
            }
            finally
            {
                Console.WriteLine("log: " + logPath);
            }
        }
    }

    internal static class Log
    {
        public static string NewPath()
        {
            return Path.Combine(Path.GetTempPath(),
                "grannycoop-setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        }

        public static void Write(string path, string line)
        {
            try
            {
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
            }
            catch
            {
                // a log that cannot be written must not stop the install
            }
        }
    }
}
