using System.CommandLine;
using System.CommandLine.Parsing;
using System.Runtime.InteropServices;

namespace ChangeDisplaySettings
{
    internal class Program
    {
        public static bool IS_DEBUG = true;
        public static string[] DEBUG_ARGS = { "-r", "1280x720" };

        private enum OrientationEnum
        {
            Landscape,
            ReverseLandscape,
            Portrait,
            ReversePortrait
        }

        private class MonitorSetting(string name, Program.DEVMODE dM)
        {
            public string Name { get; set; } = name;
            public DEVMODE DM { get; set; } = dM;
        }

        private int? refreshRate;
        private string? resolution;
        private OrientationEnum? orientation;
        private List<int>? monitors;
        private List<MonitorSetting>? originalSettings;

        private static int Main(string[] args)
        {
            Program program = new();
            bool isSuccess = program.Run(IS_DEBUG ? DEBUG_ARGS : args);

            if (IS_DEBUG && isSuccess)
            {
                Console.WriteLine("DEBUG MODE");
                Console.WriteLine("Press 'Enter' to revert changes...");
                Console.ReadLine();
                program.RevertAllChanges();
            }

            return isSuccess ? 0 : 1;
        }

        public bool Run(string[] args)
        {
            try
            {
                HandleArgs(args);

                FindOriginalSettings();

                foreach (MonitorSetting originalSetting in originalSettings)
                {
                    ApplyChangesToMonitor(originalSetting);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                RevertAllChanges();
                return false;
            }
        }

        private void HandleArgs(string[] args)
        {
            Option<int?> refreshRateOption = new("--refresh-rate", "-rr");
            refreshRateOption.Description = "The refresh rate in Hz.";

            Option<string?> resolutionOption = new("--resolution", "-r");
            resolutionOption.Description = "The resolution in the format WIDTHxHEIGHT (e.g., 1920x1080).";

            Option<OrientationEnum?> orientationOption = new("--orientation", "-o");
            orientationOption.Description = "The display orientation: Landscape, ReverseLandscape, Portrait, ReversePortrait.";

            Option<List<int>> monitorsOption = new("--monitors", "-m");
            monitorsOption.Description = "Apply changes to multiple monitors (1-indexed).";
            monitorsOption.AllowMultipleArgumentsPerToken = true;
            monitorsOption.Arity = ArgumentArity.OneOrMore;

            RootCommand rootCommand = new("Adjusts display settings including resolution, refresh rate, and orientation.");
            rootCommand.Options.Add(refreshRateOption);
            rootCommand.Options.Add(resolutionOption);
            rootCommand.Options.Add(orientationOption);
            rootCommand.Options.Add(monitorsOption);

            ParseResult parseResult = rootCommand.Parse(args);
            if (parseResult.Errors.Any())
            {
                foreach (ParseError error in parseResult.Errors)
                {
                    Console.Error.WriteLine(error.Message);
                }
            }

            refreshRate = parseResult.GetValue(refreshRateOption);
            resolution = parseResult.GetValue(resolutionOption);
            orientation = parseResult.GetValue(orientationOption);
            monitors = parseResult.GetValue(monitorsOption);
            if (!monitors.Any())
            {
                monitors = null;
            }

            if (monitors != null)
            {
                monitors = monitors.Distinct().ToList();
            }

            if (refreshRate == null && resolution == null && orientation == null)
            {
                throw new Exception("At least one of --refresh-rate, --resolution, or --orientation must be provided.");
            }
        }

        private void FindOriginalSettings()
        {
            List<string> monitorNames = GetMonitorNames();
            originalSettings = [];

            foreach (string monitorName in monitorNames)
            {
                DEVMODE dm = new();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                if (NativeMethods.EnumDisplaySettings(
                    monitorName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm) == 0)
                {
                    throw new Exception($"Failed to retrieve current settings for monitor: {monitorName}");
                }

                originalSettings.Add(new MonitorSetting(monitorName, dm));
            }

            if (originalSettings.Count == 0)
            {
                throw new Exception("No monitors were found.");
            }
        }

        private List<string> GetMonitorNames()
        {
            if (monitors == null)
            {
                return [null]; // Primary monitor
            }

            List<string> monitorNames = [];
            foreach (int idx in monitors)
            {
                if (idx < 1)
                {
                    throw new ArgumentException($"Monitor index must be at least 1, got {idx}");
                }

                DISPLAY_DEVICE monitor = new();
                monitor.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

                if (!NativeMethods.EnumDisplayDevices(null, (uint)(idx - 1), ref monitor, 0))
                {
                    throw new ArgumentException($"Invalid monitor index: {idx}");
                }

                monitorNames.Add(monitor.DeviceName);
            }

            return monitorNames;
        }

        private bool ApplyChangesToMonitor(MonitorSetting originalSetting)
        {
            DEVMODE newDm = originalSetting.DM;

            if (resolution != null)
            {
                string[] parts = resolution.Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height))
                {
                    throw new ArgumentException("Invalid resolution format. Use WIDTHxHEIGHT (e.g., 1920x1080).");
                }

                newDm.dmPelsWidth = width;
                newDm.dmPelsHeight = height;
            }

            if (orientation != null)
            {
                int newOrientationValue = 0;
                switch (orientation)
                {
                    case OrientationEnum.Landscape:
                        newOrientationValue = 0;
                        break;
                    case OrientationEnum.ReverseLandscape:
                        newOrientationValue = 2;
                        break;
                    case OrientationEnum.Portrait:
                        newOrientationValue = 3;
                        break;
                    case OrientationEnum.ReversePortrait:
                        newOrientationValue = 1;
                        break;
                }

                if ((newDm.dmDisplayOrientation + newOrientationValue) % 2 == 1)
                {
                    (newDm.dmPelsWidth, newDm.dmPelsHeight) = (newDm.dmPelsHeight, newDm.dmPelsWidth);
                }

                newDm.dmDisplayOrientation = newOrientationValue;
            }

            if (refreshRate != null)
            {
                newDm.dmDisplayFrequency = refreshRate.Value;
            }

            newDm.dmFields = 0;
            if (resolution != null || orientation != null)
            {
                newDm.dmFields |= (uint)(DM.PelsWidth | DM.PelsHeight);
            }
            if (orientation != null)
            {
                newDm.dmFields |= (uint)DM.DisplayOrientation;
            }
            if (refreshRate != null)
            {
                newDm.dmFields |= (uint)DM.DisplayFrequency;
            }

            DISP_CHANGE result = NativeMethods.ChangeDisplaySettingsEx(originalSetting.Name, ref newDm, IntPtr.Zero,
                DisplaySettingsFlags.CDS_UPDATEREGISTRY, IntPtr.Zero);

            if (result != DISP_CHANGE.Successful)
            {
                throw new Exception($"Failed to change settings for monitor {originalSetting.Name}: {result}");
            }

            return true;
        }

        public void RevertAllChanges()
        {
            if (originalSettings == null || originalSettings.Count == 0)
            {
                return;
            }

            try
            {
                DEVMODE dm = new();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                uint i = 0;
                DISPLAY_DEVICE monitor = new();
                monitor.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

                while (NativeMethods.EnumDisplayDevices(null, i, ref monitor, 0))
                {
                    if (NativeMethods.EnumDisplaySettings(
                        monitor.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm) != 0)
                    {
                        NativeMethods.ChangeDisplaySettingsEx(
                            monitor.DeviceName, ref dm, IntPtr.Zero,
                            DisplaySettingsFlags.CDS_UPDATEREGISTRY, IntPtr.Zero);
                    }

                    monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

                    i++;
                }
            }
            catch (Exception revertEx)
            {
                Console.Error.WriteLine($"Error during revert: {revertEx.Message}");
            }
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi)]
        internal struct DEVMODE
        {
            public const int CCHDEVICENAME = 32;
            public const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            [FieldOffset(0)]
            public string dmDeviceName;
            [FieldOffset(32)]
            public short dmSpecVersion;
            [FieldOffset(34)]
            public short dmDriverVersion;
            [FieldOffset(36)]
            public short dmSize;
            [FieldOffset(38)]
            public short dmDriverExtra;
            [FieldOffset(40)]
            public uint dmFields;

            [FieldOffset(44)]
            public int dmPosition_x;
            [FieldOffset(48)]
            public int dmPosition_y;
            [FieldOffset(52)]
            public int dmDisplayOrientation;
            [FieldOffset(56)]
            public int dmDisplayFixedOutput;

            [FieldOffset(60)]
            public short dmColor;
            [FieldOffset(62)]
            public short dmDuplex;
            [FieldOffset(64)]
            public short dmYResolution;
            [FieldOffset(66)]
            public short dmTTOption;
            [FieldOffset(68)]
            public short dmCollate;
            [FieldOffset(72)]
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            [FieldOffset(102)]
            public short dmLogPixels;
            [FieldOffset(104)]
            public int dmBitsPerPel;
            [FieldOffset(108)]
            public int dmPelsWidth;
            [FieldOffset(112)]
            public int dmPelsHeight;
            [FieldOffset(116)]
            public int dmDisplayFlags;
            [FieldOffset(120)]
            public int dmDisplayFrequency;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct DISPLAY_DEVICE
        {
            [MarshalAs(UnmanagedType.U4)]
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            [MarshalAs(UnmanagedType.U4)]
            public DisplayDeviceStateFlags StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        internal static class NativeMethods
        {
            [DllImport("user32.dll")]
            internal static extern DISP_CHANGE ChangeDisplaySettingsEx(
                string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd,
                DisplaySettingsFlags dwflags, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Ansi)]
            internal static extern int EnumDisplaySettings(
                string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EnumDisplayDevices(
                string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice,
                uint dwFlags);

            public const int ENUM_CURRENT_SETTINGS = -1;
        }

        internal enum DISP_CHANGE : int
        {
            Successful = 0,
            Restart = 1,
            Failed = -1,
            BadMode = -2,
            NotUpdated = -3,
            BadFlags = -4,
            BadParam = -5,
            BadDualView = -6
        }

        [Flags]
        internal enum DisplaySettingsFlags : int
        {
            CDS_NONE = 0,
            CDS_UPDATEREGISTRY = 0x00000001,
            CDS_TEST = 0x00000002,
            CDS_FULLSCREEN = 0x00000004,
            CDS_GLOBAL = 0x00000008,
            CDS_SET_PRIMARY = 0x00000010,
            CDS_VIDEOPARAMETERS = 0x00000020,
            CDS_ENABLE_UNSAFE_MODES = 0x00000100,
            CDS_DISABLE_UNSAFE_MODES = 0x00000200,
            CDS_RESET = 0x40000000,
            CDS_RESET_EX = 0x20000000,
            CDS_NORESET = 0x10000000
        }

        [Flags]
        internal enum DisplayDeviceStateFlags : int
        {
            AttachedToDesktop = 0x1,
            MultiDriver = 0x2,
            PrimaryDevice = 0x4,
            MirroringDriver = 0x8,
            VGACompatible = 0x10,
            Removable = 0x20,
            ModesPruned = 0x8000000,
            Remote = 0x4000000,
            Disconnect = 0x2000000
        }

        [Flags]
        internal enum DM : uint
        {
            Orientation = 0x00000001,
            PaperSize = 0x00000002,
            PaperLength = 0x00000004,
            PaperWidth = 0x00000008,
            Scale = 0x00000010,
            Position = 0x00000020,
            NUP = 0x00000040,
            DisplayOrientation = 0x00000080,
            Copies = 0x00000100,
            DefaultSource = 0x00000200,
            PrintQuality = 0x00000400,
            Color = 0x00000800,
            Duplex = 0x00001000,
            YResolution = 0x00002000,
            TTOption = 0x00004000,
            Collate = 0x00008000,
            FormName = 0x00010000,
            LogPixels = 0x00020000,
            BitsPerPixel = 0x00040000,
            PelsWidth = 0x00080000,
            PelsHeight = 0x00100000,
            DisplayFlags = 0x00200000,
            DisplayFrequency = 0x00400000,
            ICMMethod = 0x00800000,
            ICMIntent = 0x01000000,
            MediaType = 0x02000000,
            DitherType = 0x04000000,
            PanningWidth = 0x08000000,
            PanningHeight = 0x10000000,
            DisplayFixedOutput = 0x20000000
        }
    }
}